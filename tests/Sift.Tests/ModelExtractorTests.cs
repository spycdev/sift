using System.Text.Json.Nodes;
using Sift.Core.Extraction.LocalModel;
using Sift.Core.Model;
using Sift.Core.Pipeline;
using Sift.Tests.Support;

namespace Sift.Tests;

/// <summary>
/// The model extractor with a scripted fake model. These tests are about what Sift does with a
/// model's answer (grounding, parsing, fallback), not about any particular model's accuracy.
/// </summary>
public sealed class ModelExtractorTests
{
    private sealed class FakeModel(string? answer, Exception? failure = null) : ILocalModelClient
    {
        public string? LastPrompt { get; private set; }
        public string ModelName => "fake";
        public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(failure is null);

        public Task<string> CompleteJsonAsync(string systemPrompt, string userPrompt, JsonObject schema, CancellationToken ct = default)
        {
            LastPrompt = userPrompt;
            return failure is null ? Task.FromResult(answer!) : Task.FromException<string>(failure);
        }
    }

    private const string Email = """
        From: Atelier Lumen <bonjour@atelier-lumen.example>
        Subject: Votre reçu
        Date: Thu, 15 Oct 2026 09:00:00 +0200

        Atelier Lumen
        Commande n° AL-5521
        Date de commande : 11 octobre 2026

        Bougie parfumée    2 x 12,50 €    25,00 €
        Savon artisanal    1 x 6,00 €     6,00 €

        Sous-total    31,00 €
        Remise fidélité    -3,10 €
        TVA 20 %    5,58 €
        Total    33,48 €
        """;

    private static string Answer(
        string merchant = "Atelier Lumen",
        string dateValue = "2026-10-11", string dateQuote = "11 octobre 2026",
        string order = "AL-5521",
        string items = """
            [{"description":"Bougie parfumée","quantity":2,"unit_price":"12,50 €","line_total":"25,00 €","quote":"Bougie parfumée 2 x 12,50 € 25,00 €"},
             {"description":"Savon artisanal","quantity":1,"unit_price":"6,00 €","line_total":"6,00 €","quote":"Savon artisanal 1 x 6,00 € 6,00 €"}]
            """,
        string adjustments = """[{"kind":"discount","label":"Remise fidélité","amount":"3,10 €"}]""",
        string subtotal = "\"31,00 €\"", string taxes = """["5,58 €"]""", string total = "\"33,48 €\"") => $$"""
        {
          "is_receipt": true,
          "not_receipt_reason": null,
          "merchant": {"value": "{{merchant}}", "quote": "{{merchant}}"},
          "purchase_date": {"value": "{{dateValue}}", "quote": "{{dateQuote}}"},
          "order_number": {"value": "{{order}}", "quote": "Commande n° {{order}}"},
          "items": {{items}},
          "adjustments": {{adjustments}},
          "subtotal": {{subtotal}},
          "taxes": {{taxes}},
          "total": {{total}}
        }
        """;

    private static async Task<(PipelineResult Result, FakeModel Model)> Run(string? answer, Exception? failure = null, bool skipClassification = false)
    {
        var model = new FakeModel(answer, failure);
        var result = await ReceiptPipeline.CreateDefault(model: model).ProcessAsync(Fixtures.Eml(Email), skipClassification);
        return (result, model);
    }

    [Fact]
    public async Task The_model_decides_whether_it_is_a_receipt_not_the_english_keyword_rules()
    {
        // The rules classifier rejects this French receipt (no English keywords); the model reads it anyway.
        Assert.False(new Sift.Core.Classification.ReceiptClassifier().Classify(
            new Sift.Core.Ingestion.EmailParser().Parse(Fixtures.Eml(Email)).Email!).IsReceipt);

        var (result, _) = await Run(Answer());

        Assert.Equal(PipelineStatus.Parsed, result.Status);
    }

    [Fact]
    public async Task A_model_that_declines_explains_why()
    {
        var (result, _) = await Run("""
            {"is_receipt": false, "not_receipt_reason": "This is a newsletter.", "merchant": null, "purchase_date": null, "order_number": null,
             "items": [], "adjustments": [], "subtotal": null, "taxes": [], "total": null}
            """);

        Assert.Equal(PipelineStatus.NotAReceipt, result.Status);
        Assert.Contains("local model: This is a newsletter.", result.Classification!.Signals);
    }

    [Fact]
    public async Task Grounded_answer_becomes_a_receipt_with_amounts_parsed_from_the_email()
    {
        var (result, _) = await Run(Answer());
        var r = result.Receipt!;

        Assert.Equal("model:fake", result.ExtractorName);
        Assert.Equal("Atelier Lumen", r.Merchant!.Value);
        Assert.Equal(new DateOnly(2026, 10, 11), DateOnly.FromDateTime(r.PurchasedAt!.Value.DateTime));
        Assert.Equal("EUR", r.Currency!.Value);   // from the currency detector, not the model
        Assert.Equal("AL-5521", r.OrderNumber!.Value);
        Assert.Equal([25.00m, 6.00m], r.Items.Select(i => i.LineTotal!.Value));
        Assert.Equal(2m, r.Items[0].Quantity!.Value);
        Assert.Equal(-3.10m, Assert.Single(r.Adjustments).Amount.Value);   // discounts are negative
        Assert.Equal(31.00m, r.Subtotal!.Value);
        Assert.Equal(5.58m, r.Tax!.Value);
        Assert.Equal(33.48m, r.Total!.Value);
        Assert.Empty(r.Warnings);   // 31.00 - 3.10 + 5.58 = 33.48
        Assert.Equal(ExtractionMethod.LocalModel, r.Total.Method);
        Assert.Equal("Total 33,48 €", r.Total.SourceSnippet);
    }

    [Fact]
    public async Task Values_that_are_not_in_the_email_are_discarded_and_flagged()
    {
        var (result, _) = await Run(Answer(
            order: "AL-9999",
            items: """[{"description":"Vase en céramique","quantity":1,"unit_price":null,"line_total":"40,00 €","quote":"Vase en céramique 40,00 €"}]""",
            total: "\"34,48 €\""));
        var r = result.Receipt!;

        Assert.Null(r.OrderNumber);
        Assert.Empty(r.Items);
        Assert.Null(r.Total);
        Assert.Contains(r.Warnings, w => w is { Code: WarningCodes.UngroundedValue, Field: "OrderNumber" });
        Assert.Contains(r.Warnings, w => w is { Code: WarningCodes.UngroundedValue, Field: "Items" });
        Assert.Contains(r.Warnings, w => w is { Code: WarningCodes.UngroundedValue, Field: "Total" });   // explains the missing total
    }

    [Fact]
    public async Task Amounts_must_be_whole_amounts_from_the_email()
    {
        // "25" is inside "25,00 €" but isn't an amount the email prints.
        var (result, _) = await Run(Answer(total: "\"25\""));

        Assert.Null(result.Receipt!.Total);
        Assert.Contains(result.Receipt.Warnings, w => w is { Code: WarningCodes.UngroundedValue, Field: "Total" });
    }

    [Fact]
    public async Task Ids_copied_with_stray_spaces_are_restored_from_the_email()
    {
        var (result, _) = await Run(Answer(order: "AL-55 21"));

        Assert.Equal("AL-5521", result.Receipt!.OrderNumber!.Value);
    }

    [Fact]
    public async Task Dates_are_reread_from_the_quoted_text_not_taken_from_the_model()
    {
        var (wrongValue, _) = await Run(Answer(dateValue: "2026-11-10"));
        var (inventedQuote, _) = await Run(Answer(dateQuote: "15 octobre 2026"));

        Assert.Equal(new DateOnly(2026, 10, 11), DateOnly.FromDateTime(wrongValue.Receipt!.PurchasedAt!.Value.DateTime));
        Assert.Null(inventedQuote.Receipt!.PurchasedAt);   // that's the sent date, which isn't in the body
        Assert.Contains(inventedQuote.Receipt.Warnings, w => w is { Code: WarningCodes.UngroundedValue, Field: "PurchasedAt" });
    }

    [Fact]
    public async Task Quantities_the_email_does_not_show_are_dropped()
    {
        var (result, _) = await Run(Answer(items: """
            [{"description":"Bougie parfumée","quantity":3,"unit_price":"12,50 €","line_total":"25,00 €","quote":"Bougie parfumée 2 x 12,50 € 25,00 €"}]
            """));

        Assert.Null(Assert.Single(result.Receipt!.Items).Quantity);
    }

    [Fact]
    public async Task A_real_amount_in_the_wrong_field_is_caught_by_the_validator()
    {
        // 25,00 € is in the email, but it's a line total, not the total.
        var (result, _) = await Run(Answer(total: "\"25,00 €\""));

        Assert.Equal(25.00m, result.Receipt!.Total!.Value);
        Assert.Contains(result.Receipt.Warnings, w => w.Code == WarningCodes.TotalMismatch);
    }

    [Fact]
    public async Task The_prompt_contains_the_email_but_not_its_sent_date()
    {
        var (_, model) = await Run(Answer());

        Assert.Contains("Bougie parfumée | 2 x 12,50 € | 25,00 €", model.LastPrompt);
        Assert.Contains("Subject: Votre reçu", model.LastPrompt);
        Assert.DoesNotContain("15 Oct 2026", model.LastPrompt);
        Assert.DoesNotContain("2026-10-15", model.LastPrompt);
    }

    [Fact]
    public async Task Falls_back_to_rules_when_the_model_is_unavailable()
    {
        var (result, _) = await Run(null, new LocalModelException("The local model service isn't running."), skipClassification: true);

        Assert.Equal(PipelineStatus.Parsed, result.Status);
        Assert.Equal("generic", result.ExtractorName);
        Assert.Contains(result.Receipt!.Warnings, w => w.Code == WarningCodes.ModelUnavailable);
    }

    [Fact]
    public async Task Unreadable_model_output_falls_back_too()
    {
        var (result, _) = await Run("not json at all", skipClassification: true);

        Assert.Equal("generic", result.ExtractorName);
        Assert.Contains(result.Receipt!.Warnings, w => w.Code == WarningCodes.ModelUnavailable);
    }

    [Fact]
    public void Model_endpoints_off_this_machine_are_refused()
    {
        Assert.Throws<ArgumentException>(() => new OllamaClient(new OllamaOptions { Endpoint = new Uri("http://192.168.1.20:11434") }));
        Assert.Throws<ArgumentException>(() => new OllamaClient(new OllamaOptions { Endpoint = new Uri("https://models.example.com") }));
        using var local = new OllamaClient(new OllamaOptions { Endpoint = new Uri("http://localhost:11434") });
    }
}
