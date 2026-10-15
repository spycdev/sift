using System.Globalization;
using System.Text.RegularExpressions;
using Sift.Core.Ingestion;
using Sift.Core.Model;

namespace Sift.Core.Extraction;

internal sealed record ExtractionHints(string? MerchantName, string? Currency)
{
    public static readonly ExtractionHints None = new(null, null);
}

/// <summary>
/// The shared line-based reader behind every extractor. Summary lines ("Subtotal", "Tax", "Total",
/// shipping, discounts) are found by label; item lines are the priced lines above the subtotal.
/// Anything it can't read confidently is left null, with a warning when there is something to say.
/// </summary>
internal static partial class LineExtraction
{
    private enum SummaryKind { None, Subtotal, Total, Tax, Shipping, Discount, Tip, Fee }

    private const int SnippetLength = 160;

    public static Receipt Extract(ParsedEmail email, ExtractionHints hints, ExtractionMethod method, double confidence)
    {
        var text = email.PrimaryText;
        var lines = text.Length == 0 ? [] : text.Split('\n');
        var warnings = new List<ValidationWarning>();

        // Currency comes first: it decides how many decimal places an amount can have.
        var currency = ExtractCurrency(email, text, hints, method, warnings);

        var tokens = lines.Select(MoneyParser.FindTokens).ToArray();
        var format = MoneyParser.InferFormat(tokens.SelectMany(t => t), Currencies.MinorUnits(currency?.Value));
        var amounts = ParseAmounts(tokens, format, out var unparsed);
        if (unparsed)
        {
            warnings.Add(new ValidationWarning(WarningCodes.AmbiguousDecimal, null, WarningSeverity.Warning,
                "Some amounts could be read two ways (for example \"1,250\" as 1250 or 1.25) and the email doesn't say which, so they were left out."));
        }
        else if (format.Ambiguous)
        {
            warnings.Add(new ValidationWarning(WarningCodes.AmbiguousDecimal, null, WarningSeverity.Warning,
                "The email uses both \".\" and \",\" as a decimal separator; check the amounts."));
        }

        // Summary lines.
        Extracted<decimal>? subtotal = null, tax = null, total = null;
        var adjustments = new List<Adjustment>();
        int firstSummary = lines.Length, subtotalLine = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (amounts[i] is null) continue;
            var label = LabelOf(lines[i], tokens[i]);
            var kind = Classify(label);
            if (kind == SummaryKind.None) continue;

            // The amount is the last one on the line, or on the next line if that line is just an amount.
            var snippet = lines[i];
            decimal? amount = amounts[i]!.Count > 0 ? amounts[i]![^1] : null;
            if (amount is null && i + 1 < lines.Length && amounts[i + 1] is { Count: > 0 } next && LabelOf(lines[i + 1], tokens[i + 1]).Length == 0)
            {
                amount = next[^1];
                snippet += " " + lines[i + 1];
            }
            if (amount is null) continue;   // e.g. a "Total" column header

            firstSummary = Math.Min(firstSummary, i);
            var value = new Extracted<decimal>(amount.Value, method, confidence, Snip(snippet));
            switch (kind)
            {
                case SummaryKind.Subtotal when subtotal is null:
                    subtotal = value;
                    subtotalLine = i;
                    break;
                case SummaryKind.Total:
                    total ??= value;
                    break;
                case SummaryKind.Tax:
                    // Several tax lines (e.g. GST + PST) add up to the tax.
                    tax = tax is null ? value : tax with { Value = tax.Value + amount.Value, SourceSnippet = Snip(tax.SourceSnippet + " / " + snippet) };
                    break;
                case SummaryKind.Discount:
                    adjustments.Add(new Adjustment(AdjustmentKind.Discount, label, value with { Value = -Math.Abs(amount.Value) }));
                    break;
                case SummaryKind.Shipping:
                    adjustments.Add(new Adjustment(AdjustmentKind.Shipping, label, value));
                    break;
                case SummaryKind.Tip:
                    adjustments.Add(new Adjustment(AdjustmentKind.Tip, label, value));
                    break;
                case SummaryKind.Fee:
                    adjustments.Add(new Adjustment(AdjustmentKind.Fee, label, value));
                    break;
            }
        }

        // Item lines: priced lines above the subtotal (or above the first summary line).
        var items = new List<ReceiptItem>();
        var itemEnd = subtotalLine >= 0 ? subtotalLine : firstSummary;
        for (var i = 0; i < itemEnd; i++)
        {
            if (amounts[i] is not { Count: > 0 } values) continue;
            if (Classify(LabelOf(lines[i], tokens[i])) != SummaryKind.None) continue;
            if (ParseItem(lines[i], tokens[i], values, method, confidence * 0.9) is { } item) items.Add(item);
        }

        var merchant = hints.MerchantName is not null
            ? new Extracted<string>(hints.MerchantName, method, 0.95, Snip($"From: {email.FromAddress}"))
            : MerchantFromSender(email);

        var purchasedAt = ExtractDate(email, text, method, confidence, warnings);

        var orderNumber = OrderNumbers.Find(text) is { } order
            ? new Extracted<string>(order.Value, method, confidence, Snip(order.Line))
            : null;

        return new Receipt(
            ImportIds.For(email), merchant, purchasedAt, currency, orderNumber,
            items, adjustments, subtotal, tax, total, warnings);
    }

    private static Extracted<string>? ExtractCurrency(
        ParsedEmail email, string text, ExtractionHints hints, ExtractionMethod method, List<ValidationWarning> warnings)
    {
        var detected = CurrencyDetector.Detect(text, email.SenderDomain);
        if (hints.Currency is not null && (detected.Code is null || detected.Code == hints.Currency))
            return new Extracted<string>(hints.Currency, method, 0.95, Snip(detected.Snippet ?? $"Known currency for {hints.MerchantName}"));

        if (detected.Warning is not null) warnings.Add(detected.Warning);
        return detected.Code is null ? null : new Extracted<string>(detected.Code, method, detected.Confidence, Snip(detected.Snippet));
    }

    /// <returns>Per line: the parsed money amounts, or null when the line has an amount that can't be read safely.</returns>
    private static List<decimal>?[] ParseAmounts(List<MoneyToken>[] tokens, NumberFormat format, out bool unparsed)
    {
        unparsed = false;
        var result = new List<decimal>?[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
        {
            var values = new List<decimal>();
            foreach (var token in tokens[i].Where(t => t.IsMoney))
            {
                if (MoneyParser.TryParse(token, format, out var value))
                {
                    values.Add(value);
                }
                else
                {
                    unparsed = true;
                    values = null;
                    break;
                }
            }
            result[i] = values;
        }
        return result;
    }

    private static Extracted<DateTimeOffset>? ExtractDate(
        ParsedEmail email, string text, ExtractionMethod method, double confidence, List<ValidationWarning> warnings)
    {
        var found = DateFinder.FindLabeled(text);
        if (found.Date is { } date) return AtEmailOffset(date, email, method, confidence, found.Line);

        // The date the email was sent is not used as a fallback: it is often not the purchase date,
        // and a plausible wrong date is worse than a flagged missing one.
        if (found.Ambiguous)
        {
            warnings.Add(new ValidationWarning(WarningCodes.AmbiguousDate, nameof(Receipt.PurchasedAt), WarningSeverity.Warning,
                "The purchase date could be day/month or month/day, so it was left blank."));
        }
        return null;
    }

    internal static Extracted<DateTimeOffset> AtEmailOffset(DateOnly date, ParsedEmail email, ExtractionMethod method, double confidence, string? snippet) =>
        new(new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), email.Date?.Offset ?? TimeSpan.Zero), method, confidence, Snip(snippet));

    private static ReceiptItem? ParseItem(string line, List<MoneyToken> tokens, List<decimal> values, ExtractionMethod method, double confidence)
    {
        // Turn amounts into cell breaks; what's left is the description and maybe a quantity.
        var rest = line;
        foreach (var token in tokens.Where(t => t.IsMoney).OrderByDescending(t => t.Index))
            rest = rest.Remove(token.Index, token.Text.Length).Insert(token.Index, "\t");

        var cells = rest.Split('\t').Select(c => c.Trim()).Where(c => c.Length > 0).ToList();

        decimal? quantity = null;
        var bareQuantity = cells.Skip(1).FirstOrDefault(c => BareQuantity().IsMatch(c));
        if (bareQuantity is not null)
        {
            quantity = decimal.Parse(bareQuantity, CultureInfo.InvariantCulture);
            cells.Remove(bareQuantity);
        }
        else
        {
            foreach (var pattern in new[] { QuantityLabel(), CountTimes(), TimesCount() })
            {
                var match = cells.Select(c => pattern.Match(c)).FirstOrDefault(m => m.Success);
                if (match is null) continue;
                quantity = decimal.Parse(match.Groups["q"].Value, CultureInfo.InvariantCulture);
                cells = cells.Select(c => pattern.Replace(c, " ")).ToList();
                break;
            }
        }

        var description = cells
            .Select(c => Whitespace().Replace(c, " ").Trim(' ', '@', '-', '–', ':', '*', '·', '|'))
            .FirstOrDefault(c => c.Count(char.IsLetter) >= 2);

        if (description is null
            || description.Length > 80
            || description.Split(' ').Length > 10
            || NotAnItem().IsMatch(description))
            return null;

        decimal? unitPrice = values.Count >= 2 ? values[^2] : null;
        var lineTotal = values[^1];
        var snippet = Snip(line);

        return new ReceiptItem(
            new Extracted<string>(description, method, confidence, snippet),
            quantity is null ? null : new Extracted<decimal>(quantity.Value, method, confidence, snippet),
            unitPrice is null ? null : new Extracted<decimal>(unitPrice.Value, method, confidence, snippet),
            new Extracted<decimal>(lineTotal, method, confidence, snippet));
    }

    internal static Extracted<string>? MerchantFromSender(ParsedEmail email)
    {
        if (email.FromName is { } fromName)
        {
            var cleaned = SenderNoise().Replace(fromName, " ");
            cleaned = Whitespace().Replace(cleaned, " ").Trim(' ', '-', '–', '|', ':', '•', ',');
            if (cleaned.Count(char.IsLetter) >= 2)
                return new Extracted<string>(cleaned, ExtractionMethod.GenericHeuristic, 0.7, Snip($"From: {fromName} <{email.FromAddress}>"));
        }

        var domain = email.SenderDomain;
        var label = domain?.Split('.').FirstOrDefault(l => l.Length > 2 && !GenericDomainLabel().IsMatch(l));
        if (label is null) return null;

        var name = string.Join(' ', label.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        return new Extracted<string>(name, ExtractionMethod.GenericHeuristic, 0.4, $"Sender domain: {domain}");
    }

    private static string LabelOf(string line, List<MoneyToken> tokens)
    {
        var label = line;
        foreach (var token in tokens.Where(t => t.IsMoney).OrderByDescending(t => t.Index))
            label = label.Remove(token.Index, token.Text.Length);
        return Whitespace().Replace(label.Replace('\t', ' '), " ").Trim().TrimEnd(':', '-', '–', '=').Trim();
    }

    private static SummaryKind Classify(string label)
    {
        if (label.Length == 0 || label.Length > 40 || label.Split(' ').Length > 6) return SummaryKind.None;
        if (SubtotalLabel().IsMatch(label)) return SummaryKind.Subtotal;
        if (TotalLabel().IsMatch(label)) return SummaryKind.Total;
        if (TaxLabel().IsMatch(label)) return SummaryKind.Tax;
        if (ShippingLabel().IsMatch(label)) return SummaryKind.Shipping;
        if (DiscountLabel().IsMatch(label)) return SummaryKind.Discount;
        if (TipLabel().IsMatch(label)) return SummaryKind.Tip;
        if (FeeLabel().IsMatch(label)) return SummaryKind.Fee;
        return SummaryKind.None;
    }

    internal static string? Snip(string? text)
    {
        if (text is null) return null;
        var flat = text.Replace('\t', ' ').Trim();
        return flat.Length <= SnippetLength ? flat : flat[..(SnippetLength - 1)] + "…";
    }

    private const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    [GeneratedRegex(@"^(?:items?\s+|merchandise\s+)?sub[\s-]?total\b|^merchandise\b|^items?\s+total\b", Ci)]
    private static partial Regex SubtotalLabel();

    [GeneratedRegex(@"^(?:order\s+|grand\s+|invoice\s+)?total\b|^amount\s+(?:paid|due|charged)\b|^you\s+paid\b|^(?:balance|total)\s+due\b", Ci)]
    private static partial Regex TotalLabel();

    [GeneratedRegex(@"\b(?:sales\s+|consumption\s+)?tax\b|\b(?:vat|gst|hst|pst|qst)\b", Ci)]
    private static partial Regex TaxLabel();

    [GeneratedRegex(@"\b(?:shipping|delivery|postage|freight|handling)\b", Ci)]
    private static partial Regex ShippingLabel();

    [GeneratedRegex(@"\b(?:discount|promo(?:tion)?|coupon|voucher)\b", Ci)]
    private static partial Regex DiscountLabel();

    [GeneratedRegex(@"\b(?:tip|gratuity)\b", Ci)]
    private static partial Regex TipLabel();

    [GeneratedRegex(@"\b(?:fee|service\s+charge|surcharge|deposit)\b", Ci)]
    private static partial Regex FeeLabel();

    [GeneratedRegex(@"^\d{1,3}$")]
    private static partial Regex BareQuantity();

    [GeneratedRegex(@"\b(?:qty|quantity)\s*:?\s*(?<q>\d{1,3})\b", Ci)]
    private static partial Regex QuantityLabel();

    [GeneratedRegex(@"(?<![\w.,])(?<q>\d{1,3})\s*[x×](?=\s|$)", Ci)]
    private static partial Regex CountTimes();

    [GeneratedRegex(@"(?<=^|\s)[x×]\s*(?<q>\d{1,3})\b", Ci)]
    private static partial Regex TimesCount();

    [GeneratedRegex(@"\b(?:order|invoice|receipt|date|visa|mastercard|amex|card|ending|payment|paid|balance|total|subtotal|tax|vat|shipping|delivery|discount|refund|points|rewards?|saved|savings|questions?|call|https?|www|earn|spend|spent|unsubscribe)\b", Ci)]
    private static partial Regex NotAnItem();

    [GeneratedRegex(@"\b(?:no-?reply|do\s+not\s+reply|orders?|receipts?|billing|notifications?|support|customer\s+service|team)\b", Ci)]
    private static partial Regex SenderNoise();

    [GeneratedRegex(@"^(?:www|mail|email|e-mail|orders?|shop|store|receipts?|billing|news|noreply|no-reply|example|com|net|org|co)$", Ci)]
    private static partial Regex GenericDomainLabel();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
