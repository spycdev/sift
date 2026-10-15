using Sift.Core.Model;

namespace Sift.Core.Editing;

/// <summary>The user's version of a receipt's fields. Null or blank means "not present".</summary>
public sealed record ReceiptEdit(
    string? Merchant,
    DateOnly? PurchasedOn,
    string? Currency,
    string? OrderNumber,
    IReadOnlyList<ItemEdit> Items,
    IReadOnlyList<AdjustmentEdit> Adjustments,
    decimal? Subtotal,
    decimal? Tax,
    decimal? Total);

public sealed record ItemEdit(string? Description, decimal? Quantity, decimal? UnitPrice, decimal? LineTotal);

public sealed record AdjustmentEdit(AdjustmentKind Kind, string? Label, decimal? Amount);

/// <summary>
/// Applies user edits. Changed fields become <see cref="ExtractionMethod.UserEdited"/> with full
/// confidence; unchanged fields keep their original provenance; warnings about edited fields are
/// dropped. Run <see cref="Validation.ReceiptValidator"/> afterwards.
/// </summary>
public static class ReceiptEditor
{
    public static ReceiptEdit ToEdit(Receipt r) => new(
        r.Merchant?.Value,
        r.PurchasedAt is { } d ? DateOnly.FromDateTime(d.Value.DateTime) : null,
        r.Currency?.Value,
        r.OrderNumber?.Value,
        r.Items.Select(i => new ItemEdit(i.Description.Value, i.Quantity?.Value, i.UnitPrice?.Value, i.LineTotal?.Value)).ToList(),
        r.Adjustments.Select(a => new AdjustmentEdit(a.Kind, a.Label, a.Amount.Value)).ToList(),
        r.Subtotal?.Value,
        r.Tax?.Value,
        r.Total?.Value);

    public static Receipt Apply(Receipt r, ReceiptEdit edit)
    {
        var edited = new HashSet<string>();

        var items = new List<ReceiptItem>();
        foreach (var itemEdit in edit.Items)
        {
            var index = items.Count;
            var original = index < r.Items.Count ? r.Items[index] : null;
            var description = Text(original?.Description, itemEdit.Description, $"Items[{index}].Description", edited);
            if (description is null)
            {
                if (original is not null) edited.Add("Items");
                continue;
            }
            items.Add(new ReceiptItem(
                description,
                Amount(original?.Quantity, itemEdit.Quantity, $"Items[{index}].Quantity", edited),
                Amount(original?.UnitPrice, itemEdit.UnitPrice, $"Items[{index}].UnitPrice", edited),
                Amount(original?.LineTotal, itemEdit.LineTotal, $"Items[{index}].LineTotal", edited)));
        }
        if (items.Count != r.Items.Count) edited.Add("Items");

        var adjustments = new List<Adjustment>();
        foreach (var adjustmentEdit in edit.Adjustments)
        {
            var original = adjustments.Count < r.Adjustments.Count ? r.Adjustments[adjustments.Count] : null;
            if (adjustmentEdit.Amount is not { } amount) continue;

            var label = string.IsNullOrWhiteSpace(adjustmentEdit.Label) ? adjustmentEdit.Kind.ToString() : adjustmentEdit.Label.Trim();
            var unchanged = original is not null && original.Kind == adjustmentEdit.Kind && original.Label == label && original.Amount.Value == amount;
            adjustments.Add(unchanged ? original! : new Adjustment(adjustmentEdit.Kind, label, UserValue(amount)));
        }
        if (adjustments.Count != r.Adjustments.Count || !adjustments.SequenceEqual(r.Adjustments)) edited.Add("Adjustments");

        var currency = edit.Currency?.Trim().ToUpperInvariant();
        var result = r with
        {
            Merchant = Text(r.Merchant, edit.Merchant, nameof(Receipt.Merchant), edited),
            PurchasedAt = Date(r.PurchasedAt, edit.PurchasedOn, edited),
            Currency = Text(r.Currency, currency, nameof(Receipt.Currency), edited),
            OrderNumber = Text(r.OrderNumber, edit.OrderNumber, nameof(Receipt.OrderNumber), edited),
            Items = items,
            Adjustments = adjustments,
            Subtotal = Amount(r.Subtotal, edit.Subtotal, nameof(Receipt.Subtotal), edited),
            Tax = Amount(r.Tax, edit.Tax, nameof(Receipt.Tax), edited),
            Total = Amount(r.Total, edit.Total, nameof(Receipt.Total), edited),
        };

        return result with { Warnings = r.Warnings.Where(w => !IsSettled(w, edited)).ToList() };
    }

    /// <summary>A warning is settled once the user has edited the field it concerns.</summary>
    private static bool IsSettled(ValidationWarning warning, HashSet<string> edited) => warning.Field switch
    {
        // A decimal-format doubt applies to all amounts; editing any of them means the user has reviewed them.
        null => warning.Code == WarningCodes.AmbiguousDecimal && edited.Any(IsAmountField),
        var field when edited.Contains(field) => true,
        var field => field.StartsWith("Items[", StringComparison.Ordinal) && edited.Contains("Items"),
    };

    private static bool IsAmountField(string field) =>
        field is nameof(Receipt.Subtotal) or nameof(Receipt.Tax) or nameof(Receipt.Total) or "Items" or "Adjustments"
        || field.StartsWith("Items[", StringComparison.Ordinal);

    private static Extracted<string>? Text(Extracted<string>? original, string? value, string field, HashSet<string> edited)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (original?.Value == trimmed) return original;
        edited.Add(field);
        return trimmed is null ? null : UserValue(trimmed);
    }

    private static Extracted<decimal>? Amount(Extracted<decimal>? original, decimal? value, string field, HashSet<string> edited)
    {
        if (original?.Value == value) return original;
        edited.Add(field);
        return value is null ? null : UserValue(value.Value);
    }

    private static Extracted<DateTimeOffset>? Date(Extracted<DateTimeOffset>? original, DateOnly? value, HashSet<string> edited)
    {
        var originalDate = original is null ? (DateOnly?)null : DateOnly.FromDateTime(original.Value.DateTime);
        if (originalDate == value) return original;
        edited.Add(nameof(Receipt.PurchasedAt));
        if (value is null) return null;
        var offset = original?.Value.Offset ?? TimeSpan.Zero;
        return UserValue(new DateTimeOffset(value.Value.ToDateTime(TimeOnly.MinValue), offset));
    }

    private static Extracted<T> UserValue<T>(T value) => new(value, ExtractionMethod.UserEdited, 1.0);
}
