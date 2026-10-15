using Sift.Core.Ingestion;
using Sift.Tests.Support;

namespace Sift.Tests;

public sealed class IngestionTests
{
    [Fact]
    public void Html_tables_become_tab_separated_lines()
    {
        var text = HtmlToText.Convert("<table><tr><td>Latte</td><td>2</td><td>$9.00</td></tr><tr><td>Total</td><td>$9.00</td></tr></table>");

        Assert.Equal("Latte\t2\t$9.00\nTotal\t$9.00", text);
    }

    [Fact]
    public void Hidden_preheaders_scripts_styles_and_images_are_dropped()
    {
        var text = HtmlToText.Convert("""
            <html><head><title>T</title><style>p{}</style></head><body>
            <div style="display: none">Preheader teaser</div>
            <span style="font-size:0px;max-height:0">tiny</span>
            <script>alert(1)</script>
            <img src="https://tracker.example/pixel.gif" alt="pixel">
            <p>Visible</p>
            </body></html>
            """);

        Assert.Equal("Visible", text);
    }

    [Fact]
    public void Plain_text_column_alignment_becomes_cells()
    {
        Assert.Equal("Subtotal\t$5.00", TextNormalizer.Normalize("  Subtotal        $5.00  \r\n\r\n"));
    }

    [Fact]
    public void Empty_file_fails_cleanly()
    {
        var result = new EmailParser().Parse(new RawEmail("empty.eml", []));

        Assert.False(result.Success);
        Assert.Contains("empty", result.Error);
    }

    [Fact]
    public void Oversized_file_fails_before_parsing()
    {
        var result = new EmailParser(maxBytes: 1024).Parse(new RawEmail("big.eml", new byte[1025]));

        Assert.False(result.Success);
        Assert.Contains("limit", result.Error);
    }

    [Fact]
    public async Task Reading_a_stream_stops_just_past_the_limit()
    {
        using var stream = new MemoryStream(new byte[10_000]);

        var raw = await RawEmail.ReadAsync(stream, "big.eml", maxBytes: 1000);

        Assert.Equal(1001, raw.Content.Length);
    }

    [Fact]
    public async Task Binary_garbage_is_rejected_without_throwing()
    {
        var garbage = Enumerable.Range(0, 2048).Select(i => (byte)(i * 37 % 256)).ToArray();

        var result = new EmailParser().Parse(new RawEmail("garbage.eml", garbage));

        // Either outcome is acceptable as long as it doesn't throw; if it parses, it must not look like a receipt.
        if (result.Success)
            Assert.False((await Sift.Core.Pipeline.ReceiptPipeline.CreateDefault().ProcessAsync(new RawEmail("garbage.eml", garbage))).Status
                == Sift.Core.Pipeline.PipelineStatus.Parsed);
    }

    [Fact]
    public void Attached_forward_uses_the_original_sender()
    {
        var (raw, _) = Fixtures.Load("dev", "05-forwarded-attachment-gbp");

        var email = new EmailParser().Parse(raw).Email!;

        Assert.Equal(ForwardKind.Attached, email.Forwarded);
        Assert.Equal("billing@wideworldimporters.example", email.FromAddress);
        Assert.Equal("Your Wide World Importers receipt", email.Subject);
    }

    [Fact]
    public void Inline_forward_uses_the_original_sender_and_body()
    {
        var (raw, _) = Fixtures.Load("dev", "06-forwarded-inline-cad");

        var email = new EmailParser().Parse(raw).Email!;

        Assert.Equal(ForwardKind.Inline, email.Forwarded);
        Assert.Equal("Adventure Works", email.FromName);
        Assert.Equal("receipts@adventureworks.example.ca", email.FromAddress);
        Assert.DoesNotContain("expense report", email.PrimaryText);
        Assert.StartsWith("Adventure Works - Receipt", email.PrimaryText);
    }

    [Fact]
    public void Pdf_attachments_are_listed()
    {
        var (raw, _) = Fixtures.Load("dev", "12-pdf-only");

        var email = new EmailParser().Parse(raw).Email!;

        Assert.Contains(email.Attachments, a => a.IsPdf && a.FileName == "invoice-311.pdf");
    }
}
