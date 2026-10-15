using Sift.Core.Extraction;
using Sift.Core.Model;

namespace Sift.Core.Validation;

/// <summary>
/// Checks a receipt's arithmetic and completeness. Validation is repeatable: warnings it raised
/// before are dropped and recomputed, so it can run again after every user edit.
/// </summary>
public sealed class ReceiptValidator
{
    public const double LowConfidenceThreshold = 0.5;

    public Receipt Validate(Receipt receipt)
    {
        var warnings = receipt.Warnings.Where(w => !WarningCodes.ValidatorCodes.Contains(w.Code)).ToList();
        var r = receipt;
        var currency = r.Currency?.Value;
        var unit = Currencies.SmallestUnit(currency);
        string Money(decimal amount) => Currencies.Format(amount, currency);

        // A missing subtotal can be derived from line items, but it's marked as derived, never as extracted.
        if (r.Subtotal is null && r.Items.Count > 0 && r.Items.All(i => i.LineTotal is not null))
            r = r with { Subtotal = new Extracted<decimal>(r.Items.Sum(i => i.LineTotal!.Value), ExtractionMethod.Derived, 0.7, "Sum of line items") };
        if (r.Subtotal?.Method == ExtractionMethod.Derived)
            warnings.Add(new ValidationWarning(WarningCodes.DerivedValue, nameof(Receipt.Subtotal), WarningSeverity.Info,
                "No subtotal line was found; the subtotal shown is the sum of the line items."));

        // Tolerances allow one smallest currency unit per rounded term.
        for (var i = 0; i < r.Items.Count; i++)
        {
            if (r.Items[i] is { Quantity: { } q, UnitPrice: { } p, LineTotal: { } lt }
                && Math.Abs(q.Value * p.Value - lt.Value) > unit)
            {
                warnings.Add(new ValidationWarning(WarningCodes.ItemArithmeticMismatch, $"Items[{i}].LineTotal", WarningSeverity.Warning,
                    $"{q.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} × {Money(p.Value)} is {Money(q.Value * p.Value)}, but the line total is {Money(lt.Value)}."));
            }
        }

        if (r.Subtotal is { Method: not ExtractionMethod.Derived } subtotal
            && r.Items.Count > 0 && r.Items.All(i => i.LineTotal is not null))
        {
            var sum = r.Items.Sum(i => i.LineTotal!.Value);
            if (Math.Abs(sum - subtotal.Value) > unit * r.Items.Count)
                warnings.Add(new ValidationWarning(WarningCodes.ItemsSubtotalMismatch, nameof(Receipt.Subtotal), WarningSeverity.Warning,
                    $"The line items add up to {Money(sum)}, but the subtotal is {Money(subtotal.Value)}."));
        }

        if (r is { Total: { } total, Subtotal: { } sub })
        {
            var expected = sub.Value + (r.Tax?.Value ?? 0) + r.Adjustments.Sum(a => a.Amount.Value);
            var terms = 2 + (r.Tax is null ? 0 : 1) + r.Adjustments.Count;
            if (Math.Abs(expected - total.Value) > unit * terms)
            {
                var noTax = r.Tax is null ? " No tax line was found, so no tax is included." : "";
                warnings.Add(new ValidationWarning(WarningCodes.TotalMismatch, nameof(Receipt.Total), WarningSeverity.Warning,
                    $"Subtotal, tax and adjustments add up to {Money(expected)}, but the total is {Money(total.Value)}.{noTax}"));
            }
        }

        void Missing(object? field, string name, WarningSeverity severity)
        {
            if (field is null && !warnings.Any(w => w.Field == name))
                warnings.Add(new ValidationWarning(WarningCodes.MissingField, name, severity, $"No {Describe(name)} was found."));
        }
        Missing(r.Total, nameof(Receipt.Total), WarningSeverity.Error);
        Missing(r.Merchant, nameof(Receipt.Merchant), WarningSeverity.Warning);
        Missing(r.PurchasedAt, nameof(Receipt.PurchasedAt), WarningSeverity.Warning);
        Missing(r.Currency, nameof(Receipt.Currency), WarningSeverity.Warning);

        foreach (var (name, confidence) in Confidences(r))
        {
            if (confidence < LowConfidenceThreshold)
                warnings.Add(new ValidationWarning(WarningCodes.LowConfidence, name, WarningSeverity.Info,
                    $"The {Describe(name)} is a low-confidence guess; please check it."));
        }

        return r with { Warnings = warnings };
    }

    private static IEnumerable<(string Name, double Confidence)> Confidences(Receipt r)
    {
        if (r.Merchant is { } m) yield return (nameof(Receipt.Merchant), m.Confidence);
        if (r.PurchasedAt is { } d) yield return (nameof(Receipt.PurchasedAt), d.Confidence);
        if (r.Currency is { } c) yield return (nameof(Receipt.Currency), c.Confidence);
        if (r.Total is { } t) yield return (nameof(Receipt.Total), t.Confidence);
    }

    private static string Describe(string field) => field switch
    {
        nameof(Receipt.PurchasedAt) => "purchase date",
        nameof(Receipt.OrderNumber) => "order number",
        _ => field.ToLowerInvariant(),
    };
}
