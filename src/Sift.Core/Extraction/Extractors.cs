using System.Security.Cryptography;
using System.Text;
using Sift.Core.Ingestion;
using Sift.Core.Model;

namespace Sift.Core.Extraction;

/// <param name="Receipt">Null when the extractor decided the email isn't a receipt (see <paramref name="Classification"/>).</param>
/// <param name="Classification">Set by extractors that judge receipt-ness themselves; it replaces the rules classifier's verdict.</param>
public sealed record ExtractionResult(Receipt? Receipt, string ExtractorName, Classification.ClassificationResult? Classification = null);

/// <summary>
/// One way of reading receipts. The pipeline asks every extractor how well it can handle an email
/// and runs the one with the highest score.
/// </summary>
public interface IReceiptExtractor
{
    string Name { get; }

    /// <returns>0 = cannot handle, 1 = certain.</returns>
    double CanHandle(ParsedEmail email);

    /// <summary>
    /// True when the extractor decides for itself whether an email is a receipt. Otherwise the rules
    /// classifier gates which emails reach it.
    /// </summary>
    bool ClassifiesEmails => false;

    /// <exception cref="LocalModel.LocalModelException">The extractor's model could not be used; the pipeline falls back to the next extractor.</exception>
    Task<ExtractionResult> ExtractAsync(ParsedEmail email, CancellationToken ct = default);
}

/// <summary>What is known in advance about a merchant whose emails we recognize.</summary>
public sealed record MerchantProfile(string Name, IReadOnlyList<string> SenderDomains, string? Currency)
{
    public bool Matches(string? senderDomain) =>
        senderDomain is not null && SenderDomains.Any(d =>
            senderDomain.Equals(d, StringComparison.OrdinalIgnoreCase)
            || senderDomain.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
}

public static class MerchantProfiles
{
    /// <summary>Fictional merchants used by the synthetic samples (".example" domains are reserved and never resolve).</summary>
    public static readonly IReadOnlyList<MerchantProfile> Default =
    [
        new("Northwind Coffee", ["northwindcoffee.example"], "USD"),
    ];
}

public sealed class MerchantRuleExtractor(MerchantProfile profile) : IReceiptExtractor
{
    public string Name => $"merchant:{profile.Name}";

    public double CanHandle(ParsedEmail email) => profile.Matches(email.SenderDomain) ? 0.95 : 0;

    public Task<ExtractionResult> ExtractAsync(ParsedEmail email, CancellationToken ct = default) => Task.FromResult(new ExtractionResult(
        LineExtraction.Extract(email, new ExtractionHints(profile.Name, profile.Currency), ExtractionMethod.MerchantRule, 0.9),
        Name));
}

/// <summary>Fallback for any sender: line and table heuristics only.</summary>
public sealed class GenericReceiptExtractor : IReceiptExtractor
{
    public string Name => "generic";

    public double CanHandle(ParsedEmail email) => 0.1;

    public Task<ExtractionResult> ExtractAsync(ParsedEmail email, CancellationToken ct = default) => Task.FromResult(new ExtractionResult(
        LineExtraction.Extract(email, ExtractionHints.None, ExtractionMethod.GenericHeuristic, 0.75),
        Name));
}

public static class ImportIds
{
    public static ImportId For(ParsedEmail email) => new(
        email.MessageId is null ? null : Sha256(email.MessageId.Trim().ToLowerInvariant()),
        Sha256(email.PrimaryText));

    private static string Sha256(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
