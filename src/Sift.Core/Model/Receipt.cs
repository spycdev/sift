namespace Sift.Core.Model;

/// <summary>Identifies an imported email for duplicate detection. Both values are SHA-256 hex digests.</summary>
public sealed record ImportId(string? MessageIdHash, string ContentHash);

public sealed record Receipt(
    ImportId ImportId,
    Extracted<string>? Merchant,
    Extracted<DateTimeOffset>? PurchasedAt,
    Extracted<string>? Currency,
    Extracted<string>? OrderNumber,
    IReadOnlyList<ReceiptItem> Items,
    IReadOnlyList<Adjustment> Adjustments,
    Extracted<decimal>? Subtotal,
    Extracted<decimal>? Tax,
    Extracted<decimal>? Total,
    IReadOnlyList<ValidationWarning> Warnings)
{
    public Receipt WithWarning(ValidationWarning warning) => this with { Warnings = [.. Warnings, warning] };
}

public sealed record ReceiptItem(
    Extracted<string> Description,
    Extracted<decimal>? Quantity,
    Extracted<decimal>? UnitPrice,
    Extracted<decimal>? LineTotal);

public enum AdjustmentKind { Discount, Shipping, Fee, Tip, Other }

/// <summary>Signed amount: discounts are negative, charges are positive.</summary>
public sealed record Adjustment(AdjustmentKind Kind, string Label, Extracted<decimal> Amount);

public enum WarningSeverity { Info, Warning, Error }

/// <param name="Field">Receipt property the warning concerns, e.g. "Total" or "Items[1].LineTotal"; null for the whole receipt.</param>
public sealed record ValidationWarning(string Code, string? Field, WarningSeverity Severity, string Message);

public static class WarningCodes
{
    // Raised during extraction or import.
    public const string AmbiguousDecimal = "AMBIGUOUS_DECIMAL";
    public const string AmbiguousCurrency = "AMBIGUOUS_CURRENCY";
    public const string MixedCurrency = "MIXED_CURRENCY";
    public const string AmbiguousDate = "AMBIGUOUS_DATE";
    public const string BodyPartsDisagree = "BODY_PARTS_DISAGREE";
    public const string DuplicateImport = "DUPLICATE_IMPORT";
    public const string UngroundedValue = "UNGROUNDED_VALUE";
    public const string ModelUnavailable = "MODEL_UNAVAILABLE";
    public const string TextTruncated = "TEXT_TRUNCATED";

    // Raised by ReceiptValidator; recomputed on every validation.
    public const string MissingField = "MISSING_FIELD";
    public const string ItemArithmeticMismatch = "ITEM_ARITHMETIC_MISMATCH";
    public const string ItemsSubtotalMismatch = "ITEMS_SUBTOTAL_MISMATCH";
    public const string TotalMismatch = "TOTAL_MISMATCH";
    public const string DerivedValue = "DERIVED_VALUE";
    public const string LowConfidence = "LOW_CONFIDENCE";

    public static readonly IReadOnlySet<string> ValidatorCodes = new HashSet<string>
    {
        MissingField, ItemArithmeticMismatch, ItemsSubtotalMismatch, TotalMismatch, DerivedValue, LowConfidence,
    };
}
