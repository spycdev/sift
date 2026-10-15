using System.Globalization;
using Sift.Core.Editing;
using Sift.Core.Extraction;
using Sift.Core.Model;

namespace Sift.Web;

/// <summary>Form-bindable mirror of <see cref="ReceiptEdit"/>, with one blank row for adding an item or adjustment.</summary>
public sealed class ReceiptForm
{
    public string? Merchant { get; set; }
    public DateOnly? PurchasedOn { get; set; }
    public string? Currency { get; set; }
    public string? OrderNumber { get; set; }
    public List<ItemRow> Items { get; set; } = [];
    public List<AdjustmentRow> Adjustments { get; set; } = [];
    public decimal? Subtotal { get; set; }
    public decimal? Tax { get; set; }
    public decimal? Total { get; set; }

    public static ReceiptForm From(Receipt receipt)
    {
        var edit = ReceiptEditor.ToEdit(receipt);
        return new ReceiptForm
        {
            Merchant = edit.Merchant,
            PurchasedOn = edit.PurchasedOn,
            Currency = edit.Currency,
            OrderNumber = edit.OrderNumber,
            Items = [.. edit.Items.Select(i => new ItemRow { Description = i.Description, Quantity = i.Quantity, UnitPrice = i.UnitPrice, LineTotal = i.LineTotal }), new ItemRow()],
            Adjustments = [.. edit.Adjustments.Select(a => new AdjustmentRow { Kind = a.Kind, Label = a.Label, Amount = a.Amount }), new AdjustmentRow()],
            Subtotal = edit.Subtotal,
            Tax = edit.Tax,
            Total = edit.Total,
        };
    }

    public ReceiptEdit ToEdit() => new(
        Merchant, PurchasedOn, Currency, OrderNumber,
        Items.Select(i => new ItemEdit(i.Description, i.Quantity, i.UnitPrice, i.LineTotal)).ToList(),
        Adjustments.Select(a => new AdjustmentEdit(a.Kind, a.Label, a.Amount)).ToList(),
        Subtotal, Tax, Total);
}

public sealed class ItemRow
{
    public string? Description { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? LineTotal { get; set; }
}

public sealed class AdjustmentRow
{
    public AdjustmentKind Kind { get; set; } = AdjustmentKind.Discount;
    public string? Label { get; set; }
    public decimal? Amount { get; set; }
}

/// <summary>Formatting helpers for the views.</summary>
public static class Show
{
    public static string Method(ExtractionMethod method) => method switch
    {
        ExtractionMethod.MerchantRule => "merchant rule",
        ExtractionMethod.GenericHeuristic => "generic parser",
        ExtractionMethod.LocalModel => "local model",
        ExtractionMethod.Derived => "derived",
        ExtractionMethod.UserEdited => "edited by you",
        _ => method.ToString(),
    };

    public static string Provenance<T>(Extracted<T>? value) =>
        value is null ? "not found" : $"{Method(value.Method)} · {Math.Round(value.Confidence * 100)}%";

    public static string Money(Extracted<decimal>? value, string? currency) =>
        value is null ? "—" : Currencies.Format(value.Value, currency);

    public static string Money(decimal? value, string? currency) =>
        value is null ? "—" : Currencies.Format(value.Value, currency);

    public static string Date(Extracted<DateTimeOffset>? value) =>
        value is null ? "—" : value.Value.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    public static string Date(DateTimeOffset? value) =>
        value is null ? "—" : value.Value.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    public static string Severity(WarningSeverity severity) => severity.ToString().ToLowerInvariant();

    public static IEnumerable<ValidationWarning> For(Receipt receipt, string field) =>
        receipt.Warnings.Where(w => w.Field == field || (w.Field?.StartsWith(field + "[", StringComparison.Ordinal) ?? false));

    /// <summary>Warnings not tied to a field rendered next to it.</summary>
    public static IEnumerable<ValidationWarning> General(Receipt receipt) =>
        receipt.Warnings.Where(w => w.Field is null || !ShownByField.Any(f => w.Field == f || w.Field.StartsWith(f + "[", StringComparison.Ordinal)));

    private static readonly string[] ShownByField =
        [nameof(Receipt.Merchant), nameof(Receipt.PurchasedAt), nameof(Receipt.Currency), nameof(Receipt.OrderNumber),
         nameof(Receipt.Subtotal), nameof(Receipt.Tax), nameof(Receipt.Total)];
}
