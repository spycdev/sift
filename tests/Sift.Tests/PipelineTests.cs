using Sift.Core.Model;
using Sift.Core.Pipeline;
using Sift.Core.Storage;
using Sift.Tests.Support;

namespace Sift.Tests;

public sealed class PipelineTests
{
    private sealed class FixedIndex(ImportId? known) : IImportIndex
    {
        public string? FindDuplicate(ImportId importId) => importId == known ? "existing" : null;
    }

    [Fact]
    public async Task Rejected_emails_explain_why()
    {
        var result = (await ReceiptPipeline.CreateDefault().ProcessAsync(Fixtures.Load("dev", "10-shipping-notice").Raw));

        Assert.Equal(PipelineStatus.NotAReceipt, result.Status);
        Assert.Contains(result.Classification!.Signals, s => s.Contains("shipping"));
    }

    [Fact]
    public async Task User_can_override_the_classifier()
    {
        var result = (await ReceiptPipeline.CreateDefault().ProcessAsync(Fixtures.Load("dev", "09-newsletter-promo").Raw, skipClassification: true));

        Assert.Equal(PipelineStatus.Parsed, result.Status);
        Assert.Null(result.Receipt!.Total);   // still no fabricated total
    }

    [Fact]
    public async Task Known_merchants_use_their_rule_and_others_fall_back()
    {
        var pipeline = ReceiptPipeline.CreateDefault();

        Assert.Equal("merchant:Northwind Coffee", (await pipeline.ProcessAsync(Fixtures.Load("dev", "01-northwind-coffee-html").Raw)).ExtractorName);
        Assert.Equal("generic", (await pipeline.ProcessAsync(Fixtures.Load("dev", "02-contoso-electronics-plain").Raw)).ExtractorName);
    }

    [Fact]
    public async Task Every_extracted_value_carries_provenance()
    {
        var receipt = (await ReceiptPipeline.CreateDefault().ProcessAsync(Fixtures.Load("dev", "02-contoso-electronics-plain").Raw)).Receipt!;

        Assert.All([receipt.Merchant!, receipt.Currency!, receipt.OrderNumber!], f => Assert.False(string.IsNullOrEmpty(f.SourceSnippet)));
        Assert.Contains("$68.21", receipt.Total!.SourceSnippet);
        Assert.InRange(receipt.Total.Confidence, 0.01, 1.0);
    }

    [Fact]
    public async Task Second_import_of_the_same_email_is_flagged()
    {
        var raw = Fixtures.Load("dev", "01-northwind-coffee-html").Raw;
        var first = (await ReceiptPipeline.CreateDefault().ProcessAsync(raw)).Receipt!;

        var second = (await ReceiptPipeline.CreateDefault(new FixedIndex(first.ImportId)).ProcessAsync(raw)).Receipt!;

        Assert.DoesNotContain(first.Warnings, w => w.Code == WarningCodes.DuplicateImport);
        Assert.Contains(second.Warnings, w => w.Code == WarningCodes.DuplicateImport);
    }

    [Fact]
    public async Task Html_and_plain_text_parts_that_disagree_are_flagged()
    {
        var eml = """
            From: Fabrikam Books <shop@fabrikam-books.example>
            Subject: Your receipt
            Date: Sat, 19 Sep 2026 11:15:00 +0200
            MIME-Version: 1.0
            Content-Type: multipart/alternative; boundary="b"

            --b
            Content-Type: text/plain; charset=utf-8

            Subtotal    10,00 €
            Total       12,00 €

            --b
            Content-Type: text/html; charset=utf-8

            <table><tr><td>Subtotal</td><td>10,00 €</td></tr><tr><td>Total</td><td>11,00 €</td></tr></table>
            --b--
            """;

        var receipt = (await ReceiptPipeline.CreateDefault().ProcessAsync(Fixtures.Eml(eml))).Receipt!;

        Assert.Contains(receipt.Warnings, w => w.Code == WarningCodes.BodyPartsDisagree);
    }

    [Fact]
    public async Task Ambiguous_numeric_dates_are_not_guessed()
    {
        var eml = """
            From: Shop <orders@shop.example>
            Subject: Your receipt
            Date: Mon, 05 Oct 2026 10:00:00 +0000

            Order date: 03/04/2026
            Total   EUR 10.00
            """;

        var receipt = (await ReceiptPipeline.CreateDefault().ProcessAsync(Fixtures.Eml(eml))).Receipt!;

        Assert.Contains(receipt.Warnings, w => w.Code == WarningCodes.AmbiguousDate);
        Assert.Null(receipt.PurchasedAt);
    }

    [Fact]
    public async Task The_email_sent_date_is_never_used_as_the_purchase_date()
    {
        var eml = """
            From: Shop <orders@shop.example>
            Subject: Your receipt
            Date: Mon, 05 Oct 2026 10:00:00 +0000

            Order #A-1001
            Total   EUR 10.00
            """;

        var receipt = (await ReceiptPipeline.CreateDefault().ProcessAsync(Fixtures.Eml(eml))).Receipt!;

        Assert.Null(receipt.PurchasedAt);
        Assert.Contains(receipt.Warnings, w => w is { Code: WarningCodes.MissingField, Field: nameof(Receipt.PurchasedAt) });
    }

    [Fact]
    public async Task Ambiguous_amounts_are_left_out_and_flagged()
    {
        var eml = """
            From: Shop <orders@shop.example>
            Subject: Your receipt
            Date: Mon, 05 Oct 2026 10:00:00 +0000

            Order #A-1001
            Total   $1,250
            """;

        var receipt = (await ReceiptPipeline.CreateDefault().ProcessAsync(Fixtures.Eml(eml))).Receipt!;

        Assert.Null(receipt.Total);
        Assert.Contains(receipt.Warnings, w => w.Code == WarningCodes.AmbiguousDecimal);
    }
}
