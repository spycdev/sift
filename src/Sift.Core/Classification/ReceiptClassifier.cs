using System.Globalization;
using System.Text.RegularExpressions;
using Sift.Core.Extraction;
using Sift.Core.Ingestion;

namespace Sift.Core.Classification;

/// <param name="Signals">Human-readable reasons with their weights, e.g. "+0.25 has a total line".</param>
public sealed record ClassificationResult(double Score, bool IsReceipt, IReadOnlyList<string> Signals);

/// <summary>
/// Scores how much an email looks like a receipt from simple, explainable signals. Emails below
/// the threshold are rejected with their signals so the user can see why (and override).
/// </summary>
public sealed partial class ReceiptClassifier(IEnumerable<MerchantProfile>? knownMerchants = null, double threshold = 0.5)
{
    private readonly MerchantProfile[] _knownMerchants = knownMerchants?.ToArray() ?? [];

    public double Threshold => threshold;

    public ClassificationResult Classify(ParsedEmail email)
    {
        var subject = email.Subject ?? string.Empty;
        var text = email.PrimaryText;
        var signals = new List<string>();
        var score = 0.0;

        void Signal(double weight, string reason)
        {
            score += weight;
            signals.Add(string.Create(CultureInfo.InvariantCulture, $"{weight:+0.00;-0.00} {reason}"));
        }

        if (SubjectReceipt().IsMatch(subject)) Signal(0.35, "subject mentions a receipt, invoice or order confirmation");
        if (BodyReceipt().IsMatch(text)) Signal(0.10, "body mentions a receipt or purchase");
        if (TotalLine().IsMatch(text)) Signal(0.25, "has a total line");
        if (SubtotalOrTaxLine().IsMatch(text)) Signal(0.10, "has a subtotal or tax line");
        if (MoneyParser.FindTokens(text).Count(t => t.IsMoney) >= 2) Signal(0.10, "contains several money amounts");
        if (OrderNumbers.Find(text) is not null) Signal(0.15, "has an order or receipt number");
        if (_knownMerchants.Any(m => m.Matches(email.SenderDomain))) Signal(0.10, "sender is a known merchant");

        if (SubjectShipping().IsMatch(subject)) Signal(-0.40, "subject looks like a shipping notice");
        if (SubjectPromo().IsMatch(subject)) Signal(-0.30, "subject looks promotional");
        if (SubjectAccount().IsMatch(subject)) Signal(-0.50, "subject looks like an account notice");
        if (Unsubscribe().IsMatch(text) && CallToAction().IsMatch(text)) Signal(-0.15, "body looks like marketing");

        var clamped = Math.Clamp(score, 0, 1);
        return new ClassificationResult(clamped, clamped >= threshold, signals);
    }

    private const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const RegexOptions CiLines = Ci | RegexOptions.Multiline;

    [GeneratedRegex(@"\b(?:receipt|invoice|order confirm(?:ation|ed)|thanks?(?: you)? for your (?:order|purchase)|payment (?:received|confirmation)|order summary|purchase confirmation)\b", Ci)]
    private static partial Regex SubjectReceipt();

    [GeneratedRegex(@"\b(?:receipt|invoice|thank(?:s| you) for your (?:order|purchase))\b", Ci)]
    private static partial Regex BodyReceipt();

    [GeneratedRegex(@"^(?:order\s+|grand\s+|invoice\s+)?total\b|^amount\s+(?:paid|due|charged)\b|^you\s+paid\b|^(?:balance|total)\s+due\b", CiLines)]
    private static partial Regex TotalLine();

    [GeneratedRegex(@"^(?:sub[\s-]?total|(?:sales\s+)?tax|vat|gst|hst)\b", CiLines)]
    private static partial Regex SubtotalOrTaxLine();

    [GeneratedRegex(@"\b(?:shipped|has shipped|on (?:its|the) way|out for delivery|delivered|tracking)\b", Ci)]
    private static partial Regex SubjectShipping();

    [GeneratedRegex(@"\d+\s*%\s*off|\bsale\b|\bnewsletter\b|\bdeals?\b|\bnew arrivals\b", Ci)]
    private static partial Regex SubjectPromo();

    [GeneratedRegex(@"\b(?:password|verify your|sign[- ]in|security alert|confirm your email)\b", Ci)]
    private static partial Regex SubjectAccount();

    [GeneratedRegex(@"\bunsubscribe\b", Ci)]
    private static partial Regex Unsubscribe();

    [GeneratedRegex(@"\b(?:shop now|buy now|order now|use code)\b", Ci)]
    private static partial Regex CallToAction();
}
