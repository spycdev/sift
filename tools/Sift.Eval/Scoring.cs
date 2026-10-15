using System.Globalization;
using Sift.Core.Model;
using Sift.Core.Pipeline;

namespace Sift.Eval;

public enum Outcome
{
    /// <summary>Matches the expected value (including "correctly absent").</summary>
    Correct,
    /// <summary>A value is present but incorrect: the worst outcome.</summary>
    Wrong,
    /// <summary>Missing, but with a warning the user can see.</summary>
    Flagged,
    /// <summary>Missing with no warning.</summary>
    SilentMiss,
}

public sealed record FieldScore(string Field, Outcome Outcome, string Expected, string Actual);

/// <summary>Scores a pipeline result against a fixture's expectations, field by field.</summary>
public static class Scoring
{
    public static readonly string[] Fields =
        ["Merchant", "PurchasedAt", "Currency", "OrderNumber", "Items", "Adjustments", "Subtotal", "Tax", "Total"];

    public static IReadOnlyList<FieldScore> Score(ExpectedReceipt expected, PipelineResult actual)
    {
        if (actual.Receipt is not { } r)
        {
            // Not parsed: the user sees a rejection message, so nothing is silently wrong.
            var outcome = expected.Status == actual.Status.ToString() ? Outcome.Correct : Outcome.Flagged;
            return Fields.Select(f => new FieldScore(f, outcome, expected.Status, actual.Status.ToString())).ToList();
        }
        if (expected.Status != nameof(PipelineStatus.Parsed))
            return Fields.Select(f => new FieldScore(f, Outcome.Wrong, expected.Status, "Parsed")).ToList();

        bool Flagged(string field) => r.Warnings.Any(w => w.Field == field || (w.Field is null && w.Code == WarningCodes.AmbiguousDecimal));

        return
        [
            Compare("Merchant", expected.Merchant, r.Merchant?.Value, Flagged, StringComparer.OrdinalIgnoreCase),
            Compare("PurchasedAt", expected.PurchasedOn?.ToString("yyyy-MM-dd"), r.PurchasedAt is { } d ? DateOnly.FromDateTime(d.Value.DateTime).ToString("yyyy-MM-dd") : null, Flagged),
            Compare("Currency", expected.Currency, r.Currency?.Value, Flagged),
            Compare("OrderNumber", expected.OrderNumber, r.OrderNumber?.Value, Flagged),
            Compare("Items", Items(expected.Items), r.Items.Count == 0 ? null : Items(r.Items), Flagged),
            Compare("Adjustments", Adjustments(expected.Adjustments), r.Adjustments.Count == 0 ? null : Adjustments(r.Adjustments), Flagged),
            Compare("Subtotal", Amount(expected.Subtotal), Amount(r.Subtotal?.Value), Flagged),
            Compare("Tax", Amount(expected.Tax), Amount(r.Tax?.Value), Flagged),
            Compare("Total", Amount(expected.Total), Amount(r.Total?.Value), Flagged),
        ];
    }

    private static FieldScore Compare(string field, string? expected, string? actual, Func<string, bool> flagged, StringComparer? comparer = null)
    {
        comparer ??= StringComparer.Ordinal;
        var outcome = (expected, actual) switch
        {
            (null, null) => Outcome.Correct,
            (null, _) => Outcome.Wrong,
            (_, null) => flagged(field) ? Outcome.Flagged : Outcome.SilentMiss,
            _ => comparer.Equals(expected, actual) ? Outcome.Correct : Outcome.Wrong,
        };
        return new FieldScore(field, outcome, expected ?? "(none)", actual ?? "(none)");
    }

    private static string? Amount(decimal? value) => value?.ToString("0.00##", CultureInfo.InvariantCulture);

    private static string? Items(List<ExpectedItem>? items) =>
        items is null or { Count: 0 } ? null
        : string.Join(" | ", items.Select(i => Item(i.Description, i.Quantity, i.UnitPrice, i.LineTotal)));

    private static string Items(IReadOnlyList<ReceiptItem> items) =>
        string.Join(" | ", items.Select(i => Item(i.Description.Value, i.Quantity?.Value, i.UnitPrice?.Value, i.LineTotal?.Value)));

    private static string Item(string description, decimal? quantity, decimal? unitPrice, decimal? lineTotal) =>
        $"{description.ToLowerInvariant()} ×{Amount(quantity) ?? "?"} @{Amount(unitPrice) ?? "?"} ={Amount(lineTotal) ?? "?"}";

    private static string? Adjustments(List<ExpectedAdjustment>? adjustments) =>
        adjustments is null or { Count: 0 } ? null : string.Join(" | ", adjustments.Select(a => $"{a.Kind} {Amount(a.Amount)}"));

    private static string Adjustments(IReadOnlyList<Adjustment> adjustments) =>
        string.Join(" | ", adjustments.Select(a => $"{a.Kind} {Amount(a.Amount.Value)}"));
}
