using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sift.Core.Ingestion;
using Sift.Core.Model;

namespace Sift.Core.Extraction.LocalModel;

/// <summary>
/// Reads receipts with a local language model, then trusts nothing it says without checking:
/// <list type="bullet">
/// <item>Every value must appear in the email (grounding). Ungrounded values are discarded and flagged.</item>
/// <item>The model copies amounts as printed ("18,90 €"); <see cref="MoneyParser"/> turns them into numbers,
/// so the model never does arithmetic or guesses a decimal format.</item>
/// <item>Currency comes from <see cref="CurrencyDetector"/>, and dates are re-read from the quoted text.</item>
/// </list>
/// The validator then checks the arithmetic, which catches values that are real but assigned to the wrong field.
/// </summary>
public sealed partial class ModelReceiptExtractor(ILocalModelClient client, IReadOnlyList<MerchantProfile>? merchants = null) : IReceiptExtractor
{
    public const int MaxTextLength = 16_000;
    private const double Confidence = 0.8;

    public string Name => $"model:{client.ModelName}";

    public double CanHandle(ParsedEmail email) => 0.99;

    public bool ClassifiesEmails => true;

    public async Task<ExtractionResult> ExtractAsync(ParsedEmail email, CancellationToken ct = default)
    {
        var text = email.PrimaryText;
        var warnings = new List<ValidationWarning>();
        var shown = text.Replace("\t", " | ");
        if (shown.Length > MaxTextLength)
        {
            shown = shown[..MaxTextLength];
            warnings.Add(new ValidationWarning(WarningCodes.TextTruncated, null, WarningSeverity.Info,
                "The email is very long; only the first part was read."));
        }

        var prompt = $"""
            From: {email.FromName} <{email.FromAddress}>
            Subject: {email.Subject}

            Email text (table cells are separated by " | "):
            {shown}
            """;

        var json = await client.CompleteJsonAsync(SystemPrompt, prompt, Schema, ct);
        ModelOutput output;
        try
        {
            output = JsonSerializer.Deserialize<ModelOutput>(json, JsonOptions)
                ?? throw new LocalModelException("The local model returned no data.");
        }
        catch (JsonException ex)
        {
            throw new LocalModelException("The local model returned data that couldn't be read.", ex);
        }

        var reason = string.IsNullOrWhiteSpace(output.NotReceiptReason) ? null : output.NotReceiptReason.Trim();
        var classification = new Classification.ClassificationResult(
            output.IsReceipt ? 1 : 0,
            output.IsReceipt,
            [output.IsReceipt ? "local model: this is a receipt" : $"local model: {reason ?? "not a receipt"}"]);

        return output.IsReceipt
            ? new ExtractionResult(new Grounding(email, warnings, merchants?.FirstOrDefault(m => m.Matches(email.SenderDomain))).Build(output), Name, classification)
            : new ExtractionResult(null, Name, classification);
    }

    /// <summary>Checks the model's answer against the email and converts it to a <see cref="Receipt"/>.</summary>
    private sealed class Grounding
    {
        private readonly ParsedEmail _email;
        private readonly List<ValidationWarning> _warnings;
        private readonly string[] _lines;
        private readonly string _body;
        private readonly string _header;
        private readonly NumberFormat _format;
        private readonly HashSet<string> _printedAmounts;
        private readonly Extracted<string>? _currency;
        private bool _unparsedAmount;

        public Grounding(ParsedEmail email, List<ValidationWarning> warnings, MerchantProfile? knownMerchant)
        {
            _email = email;
            _warnings = warnings;
            var text = email.PrimaryText;
            _lines = text.Length == 0 ? [] : text.Split('\n');
            _body = Normalize(text);
            _header = Normalize($"{email.FromName} {email.FromAddress} {email.Subject}");

            var detected = CurrencyDetector.Detect(text, email.SenderDomain);
            if (knownMerchant?.Currency is { } known && (detected.Code is null || detected.Code == known))
            {
                // A recognized merchant's currency settles a bare "$".
                _currency = new Extracted<string>(known, ExtractionMethod.MerchantRule, 0.95,
                    LineExtraction.Snip(detected.Snippet ?? $"Known currency for {knownMerchant.Name}"));
            }
            else
            {
                if (detected.Warning is not null) warnings.Add(detected.Warning);
                _currency = detected.Code is null ? null
                    : new Extracted<string>(detected.Code, ExtractionMethod.GenericHeuristic, detected.Confidence, LineExtraction.Snip(detected.Snippet));
            }
            var tokens = MoneyParser.FindTokens(text);
            _format = MoneyParser.InferFormat(tokens, Currencies.MinorUnits(_currency?.Value));
            _printedAmounts = tokens.Select(t => t.Number).ToHashSet();
        }

        public Receipt Build(ModelOutput o)
        {
            var items = new List<ReceiptItem>();
            foreach (var item in o.Items ?? [])
            {
                if (string.IsNullOrWhiteSpace(item.Description)) continue;
                if (!InBody(item.Description))
                {
                    Ungrounded("Items", $"item \"{Short(item.Description)}\"");
                    continue;
                }

                var snippet = LineContaining(item.Quote) ?? LineContaining(item.Description);
                var quantity = item.Quantity is { } q && QuantityIsWritten(q, snippet ?? item.Quote) ? q : (decimal?)null;
                var unitPrice = Amount(item.UnitPrice, "Items", snippet);
                var lineTotal = Amount(item.LineTotal, "Items", snippet);

                // One printed amount can't be both the unit price and the line total.
                if (unitPrice is not null && lineTotal is not null && unitPrice.Value == lineTotal.Value
                    && snippet is not null && MoneyParser.FindTokens(snippet).Count(t => t.IsMoney) < 2)
                    unitPrice = null;

                items.Add(new ReceiptItem(
                    Value(FullCell(item.Description, snippet), snippet),
                    quantity is null ? null : Value(quantity.Value, snippet),
                    unitPrice,
                    lineTotal));
            }

            var adjustments = new List<Adjustment>();
            foreach (var adjustment in o.Adjustments ?? [])
            {
                var kind = adjustment.Kind?.ToLowerInvariant() switch
                {
                    "discount" => AdjustmentKind.Discount,
                    "shipping" => AdjustmentKind.Shipping,
                    "fee" => AdjustmentKind.Fee,
                    "tip" => AdjustmentKind.Tip,
                    _ => AdjustmentKind.Other,
                };
                if (Amount(adjustment.Amount, "Adjustments", null) is not { } amount) continue;
                var label = !string.IsNullOrWhiteSpace(adjustment.Label) && InBody(adjustment.Label) ? adjustment.Label.Trim() : kind.ToString();
                if (kind == AdjustmentKind.Discount) amount = amount with { Value = -Math.Abs(amount.Value) };
                adjustments.Add(new Adjustment(kind, label, amount));
            }

            Extracted<decimal>? tax = null;
            foreach (var taxText in o.Taxes ?? [])
            {
                if (Amount(taxText, nameof(Receipt.Tax), null) is not { } part) continue;
                tax = tax is null ? part : tax with { Value = tax.Value + part.Value, SourceSnippet = LineExtraction.Snip($"{tax.SourceSnippet} / {part.SourceSnippet}") };
            }

            if (_unparsedAmount)
            {
                _warnings.Add(new ValidationWarning(WarningCodes.AmbiguousDecimal, null, WarningSeverity.Warning,
                    "Some amounts could be read two ways (for example \"1,250\" as 1250 or 1.25) and the email doesn't say which, so they were left out."));
            }

            return new Receipt(
                ImportIds.For(_email),
                Merchant(o.Merchant),
                PurchaseDate(o.PurchaseDate),
                _currency,
                OrderNumber(o.OrderNumber),
                items,
                adjustments,
                Amount(o.Subtotal, nameof(Receipt.Subtotal), null),
                tax,
                Amount(o.Total, nameof(Receipt.Total), null),
                _warnings);
        }

        private Extracted<string>? Merchant(Quoted? merchant)
        {
            if (merchant?.Value is { } name && !string.IsNullOrWhiteSpace(name))
            {
                if (InBody(name)) return Value(name.Trim(), LineContaining(name));
                if (_header.Contains(Normalize(name))) return Value(name.Trim(), $"From: {_email.FromName} <{_email.FromAddress}>");
                Ungrounded(nameof(Receipt.Merchant), "merchant name");
            }
            // The sender's display name is grounded in the headers by definition.
            return LineExtraction.MerchantFromSender(_email);
        }

        private Extracted<DateTimeOffset>? PurchaseDate(Quoted? date)
        {
            if (date?.Quote is not { } quote || string.IsNullOrWhiteSpace(quote)) return null;
            if (!InBody(quote))
            {
                Ungrounded(nameof(Receipt.PurchasedAt), "purchase date");
                return null;
            }

            // The date is re-read from the quoted text; the model's own reading is only a fallback.
            var parsed = DateFinder.Parse(quote);
            if (parsed.Date is { } fromText)
                return LineExtraction.AtEmailOffset(fromText, _email, ExtractionMethod.LocalModel, Confidence, LineContaining(quote));
            if (parsed.Ambiguous)
            {
                _warnings.Add(new ValidationWarning(WarningCodes.AmbiguousDate, nameof(Receipt.PurchasedAt), WarningSeverity.Warning,
                    "The purchase date could be day/month or month/day, so it was left blank."));
                return null;
            }

            // Formats the date reader doesn't know: accept the model's reading only if the quote shows its year and day.
            if (DateOnly.TryParseExact(date.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fromModel)
                && quote.Contains(fromModel.Year.ToString(CultureInfo.InvariantCulture))
                && Regex.IsMatch(quote, $@"(?<!\d)0?{fromModel.Day}(?!\d)"))
            {
                return LineExtraction.AtEmailOffset(fromModel, _email, ExtractionMethod.LocalModel, 0.6, LineContaining(quote));
            }

            Ungrounded(nameof(Receipt.PurchasedAt), "purchase date");
            return null;
        }

        private Extracted<string>? OrderNumber(Quoted? order)
        {
            if (order?.Value is not { } value || string.IsNullOrWhiteSpace(value)) return null;
            if (InBody(value)) return Value(value.Trim(), LineContaining(value));

            // Small models sometimes split an id while copying it ("FB-5 5102"). If the email has exactly
            // those characters with different spacing, use the email's own text.
            var compact = Whitespace().Replace(value, "");
            var pattern = new Regex(@"(?<![\w-])" + string.Join(@"\s*", compact.Select(c => Regex.Escape(c.ToString()))) + @"(?![\w-])", RegexOptions.IgnoreCase);
            var matches = _lines.Select(l => pattern.Match(l)).Where(m => m.Success).Select(m => m.Value).Distinct().ToList();
            if (matches.Count == 1) return Value(matches[0], LineContaining(matches[0]));

            Ungrounded(nameof(Receipt.OrderNumber), "order number");
            return null;
        }

        /// <summary>Parses an amount the model copied from the email, using the receipt-wide number format.</summary>
        private Extracted<decimal>? Amount(string? printed, string field, string? snippet)
        {
            if (string.IsNullOrWhiteSpace(printed)) return null;
            if (!InBody(printed))
            {
                Ungrounded(field, $"amount \"{Short(printed)}\"");
                return null;
            }

            var token = MoneyParser.FindTokens(printed).LastOrDefault(t => t.IsMoney)
                ?? MoneyParser.FindTokens(printed).LastOrDefault();
            if (token is null) return null;

            // The number must be a whole amount from the email: "$24" is inside "$24.50" but isn't printed anywhere.
            if (!_printedAmounts.Contains(token.Number))
            {
                Ungrounded(field, $"amount \"{Short(printed)}\"");
                return null;
            }
            if (!MoneyParser.TryParse(token, _format, out var value))
            {
                _unparsedAmount = true;
                return null;
            }
            return Value(value, snippet ?? LineContaining(printed));
        }

        /// <summary>
        /// A description the model cut short ("Printer Paper (50") is widened to the table cell it came from,
        /// when the line has cells and that cell holds no amounts.
        /// </summary>
        private static string FullCell(string description, string? line)
        {
            var trimmed = description.Trim();
            if (line is null || !line.Contains('\t')) return trimmed;
            var needle = Normalize(trimmed);
            var cell = line.Split('\t').FirstOrDefault(c => Normalize(c).Contains(needle) && !MoneyParser.FindTokens(c).Any(t => t.IsMoney));
            return cell?.Trim() ?? trimmed;
        }

        private static bool QuantityIsWritten(decimal quantity, string? where) =>
            where is not null && Regex.IsMatch(where,
                $@"(?<![\d.,]){Regex.Escape(quantity.ToString("0.###", CultureInfo.InvariantCulture))}(?![\d]|[.,]\d)");

        private Extracted<T> Value<T>(T value, string? snippet) => new(value, ExtractionMethod.LocalModel, Confidence, LineExtraction.Snip(snippet));

        private void Ungrounded(string field, string what) =>
            _warnings.Add(new ValidationWarning(WarningCodes.UngroundedValue, field, WarningSeverity.Warning,
                $"The model suggested a {what} that doesn't appear in the email, so it was discarded."));

        private bool InBody(string value) => Normalize(value) is { Length: > 0 } n && _body.Contains(n);

        private string? LineContaining(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var needle = Normalize(value);
            return _lines.FirstOrDefault(l => Normalize(l).Contains(needle));
        }

        private static string Short(string value) => value.Length <= 40 ? value : value[..39] + "…";

        /// <summary>Lower-case, with tabs, cell separators and runs of whitespace collapsed to single spaces.</summary>
        private static string Normalize(string value) =>
            Whitespace().Replace(value.Replace('|', ' '), " ").Trim().ToLowerInvariant();
    }

    // ----- Model contract -----

    internal const string SystemPrompt = """
        You read purchase receipt emails and copy out the purchase details as JSON.

        Rules:
        - is_receipt: true if the email confirms a purchase or payment and shows what it cost (a receipt, order confirmation or invoice), in any language. false for marketing, newsletters, shipping or delivery updates, account notices and anything else. When false, give a short not_receipt_reason and leave the other fields null or empty.
        - Copy text exactly as it appears in the email. Never calculate, convert, translate, or guess.
        - Amounts: copy each amount exactly as printed, including its currency symbol or code and sign, e.g. "$1,250.00", "18,90 €", "-$6.35".
        - If something isn't in the email, use null or an empty list.
        - merchant.value: the business that sold the goods or services.
        - purchase_date: the order, purchase or transaction date written in the email text. value is YYYY-MM-DD; quote is the date exactly as written. Never use the date the email was sent.
        - order_number.value: the order, invoice, receipt, booking or reference number exactly as written, without its label.
        - items: each product or service bought, one entry per line item. Never include subtotals, taxes, shipping, discounts, fees, tips, totals or payment lines.
          description is the item's full name exactly as written. quantity is the number of units or the weight, only if written (e.g. 2 in "2 x $6.25", "x2", "Qty: 2" or a quantity column). unit_price is the per-unit price and line_total the amount for that line, each copied as printed; if the line shows only one amount, it is the line_total and unit_price is null. quote is the item's line as written.
        - subtotal: the amount labeled as the subtotal (in any language), before tax and adjustments. null if there is no such line.
        - taxes: every tax amount (sales tax, VAT, TVA, GST, HST, PST, MwSt, IVA, consumption tax...), copied as printed, including a tax line of zero.
        - adjustments: discounts, promotions, coupons, shipping or delivery charges, fees and tips. Not taxes. kind is one of discount, shipping, fee, tip, other; label is the line's label as written.
        - total: the final amount charged, paid or due.
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    internal static readonly JsonObject Schema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "is_receipt": { "type": "boolean" },
            "not_receipt_reason": { "type": ["string", "null"] },
            "merchant": { "$ref": "#/$defs/quoted" },
            "purchase_date": { "$ref": "#/$defs/quoted" },
            "order_number": { "$ref": "#/$defs/quoted" },
            "items": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "description": { "type": "string" },
                  "quantity": { "type": ["number", "null"] },
                  "unit_price": { "type": ["string", "null"] },
                  "line_total": { "type": ["string", "null"] },
                  "quote": { "type": "string" }
                },
                "required": ["description", "quantity", "unit_price", "line_total", "quote"]
              }
            },
            "adjustments": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "kind": { "type": "string", "enum": ["discount", "shipping", "fee", "tip", "other"] },
                  "label": { "type": "string" },
                  "amount": { "type": "string" }
                },
                "required": ["kind", "label", "amount"]
              }
            },
            "subtotal": { "type": ["string", "null"] },
            "taxes": { "type": "array", "items": { "type": "string" } },
            "total": { "type": ["string", "null"] }
          },
          "required": ["is_receipt", "not_receipt_reason", "merchant", "purchase_date", "order_number", "items", "adjustments", "subtotal", "taxes", "total"],
          "$defs": {
            "quoted": {
              "type": "object",
              "properties": {
                "value": { "type": ["string", "null"] },
                "quote": { "type": ["string", "null"] }
              },
              "required": ["value", "quote"]
            }
          }
        }
        """)!.AsObject();

    internal sealed record Quoted(string? Value, string? Quote);

    internal sealed record ModelItem(string? Description, decimal? Quantity, string? UnitPrice, string? LineTotal, string? Quote);

    internal sealed record ModelAdjustment(string? Kind, string? Label, string? Amount);

    internal sealed record ModelOutput(
        bool IsReceipt,
        string? NotReceiptReason,
        Quoted? Merchant,
        Quoted? PurchaseDate,
        Quoted? OrderNumber,
        List<ModelItem>? Items,
        List<ModelAdjustment>? Adjustments,
        string? Subtotal,
        List<string>? Taxes,
        string? Total);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
