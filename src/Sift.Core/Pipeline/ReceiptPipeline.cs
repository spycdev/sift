using Sift.Core.Classification;
using Sift.Core.Extraction;
using Sift.Core.Extraction.LocalModel;
using Sift.Core.Ingestion;
using Sift.Core.Model;
using Sift.Core.Storage;
using Sift.Core.Validation;

namespace Sift.Core.Pipeline;

public enum PipelineStatus { Parsed, NotAReceipt, Unsupported, Failed }

public sealed record PipelineResult(
    PipelineStatus Status,
    string? Message,
    ParsedEmail? Email = null,
    ClassificationResult? Classification = null,
    Receipt? Receipt = null,
    string? ExtractorName = null);

/// <summary>Raw email → parse → classify → extract (best available extractor) → validate.</summary>
public sealed class ReceiptPipeline(
    EmailParser parser,
    ReceiptClassifier classifier,
    IReadOnlyList<IReceiptExtractor> extractors,
    ReceiptValidator validator,
    IImportIndex? importIndex = null)
{
    /// <param name="model">When given, the local model reads every receipt and the rules are only a fallback.</param>
    public static ReceiptPipeline CreateDefault(
        IImportIndex? importIndex = null,
        ILocalModelClient? model = null,
        IReadOnlyList<MerchantProfile>? merchants = null)
    {
        merchants ??= MerchantProfiles.Default;
        List<IReceiptExtractor> extractors = [.. merchants.Select(m => new MerchantRuleExtractor(m)), new GenericReceiptExtractor()];
        if (model is not null) extractors.Insert(0, new ModelReceiptExtractor(model, merchants));
        return new ReceiptPipeline(new EmailParser(), new ReceiptClassifier(merchants), extractors, new ReceiptValidator(), importIndex);
    }

    /// <param name="skipClassification">The user asked to parse an email the classifier rejected.</param>
    public async Task<PipelineResult> ProcessAsync(RawEmail raw, bool skipClassification = false, CancellationToken ct = default)
    {
        var parsed = parser.Parse(raw);
        if (parsed.Email is not { } email) return new PipelineResult(PipelineStatus.Failed, parsed.Error);

        if (email.Attachments.Any(a => a.IsPdf) && !MoneyParser.FindTokens(email.PrimaryText).Any(t => t.IsMoney))
        {
            return new PipelineResult(PipelineStatus.Unsupported,
                "This receipt appears to be a PDF attachment. PDF receipts aren't supported in this prototype.", email);
        }

        var rulesVerdict = classifier.Classify(email);

        // Best extractor first; if a model-backed one can't run, fall back to the next. The rules
        // classifier only gates extractors that don't judge receipt-ness themselves.
        ExtractionResult? extraction = null;
        ValidationWarning? fallback = null;
        foreach (var extractor in extractors.Where(e => e.CanHandle(email) > 0).OrderByDescending(e => e.CanHandle(email)))
        {
            if (!extractor.ClassifiesEmails && !rulesVerdict.IsReceipt && !skipClassification)
            {
                return new PipelineResult(PipelineStatus.NotAReceipt,
                    "This email doesn't look like a receipt.", email, rulesVerdict);
            }
            try
            {
                extraction = await extractor.ExtractAsync(email, ct);
                break;
            }
            catch (LocalModelException ex)
            {
                fallback = new ValidationWarning(WarningCodes.ModelUnavailable, null, WarningSeverity.Warning,
                    $"{ex.Message} The built-in rules were used instead; they handle fewer layouts.");
            }
        }
        if (extraction is null) throw new InvalidOperationException("No receipt extractor could handle this email.");

        var classification = extraction.Classification ?? rulesVerdict;
        if (extraction.Receipt is not { } receipt || (!classification.IsReceipt && !skipClassification))
        {
            if (!skipClassification)
                return new PipelineResult(PipelineStatus.NotAReceipt, "This email doesn't look like a receipt.", email, classification);
            // The user overrode a model that declined: read it with the rules instead.
            receipt = (await new GenericReceiptExtractor().ExtractAsync(email, ct)).Receipt!;
        }

        if (fallback is not null) receipt = receipt.WithWarning(fallback);
        if (BodyPartsDisagree(email) is { } disagreement) receipt = receipt.WithWarning(disagreement);
        if (importIndex?.FindDuplicate(receipt.ImportId) is not null)
        {
            receipt = receipt.WithWarning(new ValidationWarning(WarningCodes.DuplicateImport, null, WarningSeverity.Warning,
                "A receipt from this email has already been saved."));
        }

        return new PipelineResult(PipelineStatus.Parsed, null, email, classification, validator.Validate(receipt),
            extraction.Receipt is null ? "generic" : extraction.ExtractorName);
    }

    /// <summary>
    /// multipart/alternative: the plain-text and HTML parts should tell the same story. Compared with the
    /// (cheap) rules on each part, so this never costs a second model call.
    /// </summary>
    private static ValidationWarning? BodyPartsDisagree(ParsedEmail email)
    {
        if (email is not { HtmlText: not null, PlainText: not null }) return null;

        var htmlTotal = LineExtraction.Extract(email with { PlainText = null }, ExtractionHints.None, ExtractionMethod.GenericHeuristic, 0.75).Total;
        var plainTotal = LineExtraction.Extract(email with { HtmlText = null }, ExtractionHints.None, ExtractionMethod.GenericHeuristic, 0.75).Total;
        if (htmlTotal is null || plainTotal is null || htmlTotal.Value == plainTotal.Value) return null;

        return new ValidationWarning(WarningCodes.BodyPartsDisagree, nameof(Receipt.Total), WarningSeverity.Warning,
            $"The HTML and plain-text versions of this email show different totals ({htmlTotal.Value} vs {plainTotal.Value}).");
    }
}
