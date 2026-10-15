using Sift.Core.Editing;
using Sift.Core.Model;
using Sift.Core.Validation;

namespace Sift.Tests;

public sealed class ValidationAndEditingTests
{
    private static readonly ImportId AnyImport = new(null, "hash");

    private static Extracted<T> X<T>(T value) => new(value, ExtractionMethod.GenericHeuristic, 0.8, "snippet");

    private static Receipt MakeReceipt(
        decimal? subtotal = 10m, decimal? tax = 1m, decimal? total = 11m, string? currency = "USD",
        IReadOnlyList<ReceiptItem>? items = null, IReadOnlyList<Adjustment>? adjustments = null) => new(
        AnyImport,
        X("Shop"),
        X(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)),
        currency is null ? null : X(currency),
        null,
        items ?? [],
        adjustments ?? [],
        subtotal is null ? null : X(subtotal.Value),
        tax is null ? null : X(tax.Value),
        total is null ? null : X(total.Value),
        []);

    private static IEnumerable<string> Codes(Receipt r) => r.Warnings.Select(w => w.Code);

    [Fact]
    public void Consistent_receipt_has_no_warnings()
    {
        Assert.Empty(new ReceiptValidator().Validate(MakeReceipt()).Warnings);
    }

    [Fact]
    public void Zero_tax_is_a_value_and_missing_tax_is_not()
    {
        var zeroTax = new ReceiptValidator().Validate(MakeReceipt(tax: 0m, total: 10m));
        var missingTax = new ReceiptValidator().Validate(MakeReceipt(tax: null, total: 11m));

        Assert.Empty(zeroTax.Warnings);
        var mismatch = Assert.Single(missingTax.Warnings, w => w.Code == WarningCodes.TotalMismatch);
        Assert.Contains("No tax line", mismatch.Message);
    }

    [Fact]
    public void Rounding_within_one_cent_per_term_is_tolerated()
    {
        Assert.Empty(new ReceiptValidator().Validate(MakeReceipt(subtotal: 10m, tax: 1.005m, total: 11.01m)).Warnings);
    }

    [Fact]
    public void Tolerance_follows_currency_precision()
    {
        // 0.5 off is fine for nothing: JPY's smallest unit is 1, so 2 terms tolerate up to 2 yen.
        Assert.Empty(new ReceiptValidator().Validate(MakeReceipt(subtotal: 1000m, tax: 100m, total: 1102m, currency: "JPY")).Warnings);
        Assert.Contains(WarningCodes.TotalMismatch, Codes(new ReceiptValidator().Validate(MakeReceipt(subtotal: 1000m, tax: 100m, total: 1110m, currency: "JPY"))));
    }

    [Fact]
    public void Adjustments_count_towards_the_total()
    {
        var receipt = MakeReceipt(subtotal: 10m, tax: 1m, total: 14m, adjustments:
        [
            new Adjustment(AdjustmentKind.Discount, "Promo", X(-2m)),
            new Adjustment(AdjustmentKind.Shipping, "Shipping", X(5m)),
        ]);

        Assert.Empty(new ReceiptValidator().Validate(receipt).Warnings);
    }

    [Fact]
    public void Item_arithmetic_and_subtotal_are_checked()
    {
        var receipt = MakeReceipt(subtotal: 10m, items:
        [
            new ReceiptItem(X("Pens"), X(2m), X(3m), X(7m)),
            new ReceiptItem(X("Paper"), null, null, X(4m)),
        ]);

        var codes = Codes(new ReceiptValidator().Validate(receipt)).ToList();

        Assert.Contains(WarningCodes.ItemArithmeticMismatch, codes);
        Assert.Contains(WarningCodes.ItemsSubtotalMismatch, codes);   // 7 + 4 = 11, subtotal says 10
    }

    [Fact]
    public void Missing_subtotal_is_derived_and_labelled_as_derived()
    {
        var receipt = MakeReceipt(subtotal: null, items: [new ReceiptItem(X("A"), null, null, X(4m)), new ReceiptItem(X("B"), null, null, X(6m))]);

        var validated = new ReceiptValidator().Validate(receipt);

        Assert.Equal(ExtractionMethod.Derived, validated.Subtotal!.Method);
        Assert.Equal(10m, validated.Subtotal.Value);
        Assert.Contains(WarningCodes.DerivedValue, Codes(validated));
    }

    [Fact]
    public void Missing_total_is_an_error_not_a_zero()
    {
        var validated = new ReceiptValidator().Validate(MakeReceipt(total: null));

        Assert.Null(validated.Total);
        Assert.Contains(validated.Warnings, w => w is { Code: WarningCodes.MissingField, Field: "Total", Severity: WarningSeverity.Error });
    }

    [Fact]
    public void Validation_is_repeatable()
    {
        var validator = new ReceiptValidator();
        var once = validator.Validate(MakeReceipt(total: 99m));
        var twice = validator.Validate(once);

        Assert.Equal(Codes(once), Codes(twice));
    }

    [Fact]
    public void Edited_fields_become_user_edited_and_their_warnings_are_settled()
    {
        var validator = new ReceiptValidator();
        var original = validator.Validate(MakeReceipt(currency: null)
            .WithWarning(new ValidationWarning(WarningCodes.AmbiguousCurrency, "Currency", WarningSeverity.Warning, "?")));
        var edit = ReceiptEditor.ToEdit(original) with { Currency = "cad" };

        var edited = validator.Validate(ReceiptEditor.Apply(original, edit));

        Assert.Equal("CAD", edited.Currency!.Value);
        Assert.Equal(ExtractionMethod.UserEdited, edited.Currency.Method);
        Assert.Equal(1.0, edited.Currency.Confidence);
        Assert.DoesNotContain(edited.Warnings, w => w.Field == "Currency");
        Assert.Equal(ExtractionMethod.GenericHeuristic, edited.Merchant!.Method);   // untouched field keeps provenance
    }

    [Fact]
    public void Unchanged_edit_changes_nothing()
    {
        var original = new ReceiptValidator().Validate(MakeReceipt(items: [new ReceiptItem(X("A"), X(1m), X(10m), X(10m))]));
        var edit = ReceiptEditor.ToEdit(original);
        edit = edit with { Items = [.. edit.Items, new ItemEdit(null, null, null, null)] };   // blank row from the form

        var applied = ReceiptEditor.Apply(original, edit);

        Assert.Equal(original.Items, applied.Items);
        Assert.Equal(original.Warnings, applied.Warnings);
        Assert.Same(original.Total, applied.Total);
    }

    [Fact]
    public void Clearing_a_field_removes_it()
    {
        var original = MakeReceipt();

        var applied = ReceiptEditor.Apply(original, ReceiptEditor.ToEdit(original) with { Tax = null });

        Assert.Null(applied.Tax);
    }
}
