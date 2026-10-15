using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sift.Core.Crypto;
using Sift.Core.Extraction.LocalModel;
using Sift.Core.Model;
using Sift.Core.Validation;
using Sift.Core.Ingestion;
using Sift.Core.Pipeline;
using Sift.Core.Storage;

namespace Sift.Web.Pages;

public sealed class IndexModel(
    ReceiptPipeline pipeline,
    PendingImports pending,
    LocalReceiptStore store,
    ReceiptValidator validator,
    ILogger<IndexModel> logger,
    ILocalModelClient? model = null) : PageModel
{
    public const string ExportExtractorName = "export";
    private const int MaxExportBytes = 1024 * 1024;

    public IReadOnlyList<StoredReceiptSummary> Saved { get; private set; } = [];
    public string? Error { get; private set; }
    public string? OpenError { get; private set; }

    /// <summary>Model name, and whether it's ready; null when the model is disabled.</summary>
    public (string Name, bool Ready)? Model { get; private set; }

    /// <summary>An email the classifier rejected, which the user may parse anyway.</summary>
    public PendingImport? Rejected { get; private set; }

    [BindProperty]
    public IFormFile? Upload { get; set; }

    public async Task OnGetAsync(CancellationToken ct) => await LoadPageAsync(ct);

    public async Task<IActionResult> OnPostUploadAsync(CancellationToken ct)
    {
        await LoadPageAsync(ct);
        if (Upload is null || Upload.Length == 0)
        {
            Error = "Choose an .eml file to import.";
            return Page();
        }
        if (Upload.Length > EmailParser.DefaultMaxBytes)
        {
            Error = $"The file is larger than the {EmailParser.DefaultMaxBytes / (1024 * 1024)} MB limit.";
            return Page();
        }

        await using var stream = Upload.OpenReadStream();
        var raw = await RawEmail.ReadAsync(stream, "upload", EmailParser.DefaultMaxBytes, ct);
        var result = await pipeline.ProcessAsync(raw, ct: ct);
        LogResult(result);

        switch (result.Status)
        {
            case PipelineStatus.Parsed:
                return RedirectToPage("/Review", new { id = pending.Add(raw, result).Id });
            case PipelineStatus.NotAReceipt:
                Rejected = pending.Add(raw, result);
                return Page();
            default:
                Error = result.Message;
                return Page();
        }
    }

    public async Task<IActionResult> OnPostParseAnywayAsync(string id, CancellationToken ct)
    {
        await LoadPageAsync(ct);
        if (pending.Get(id) is not { } import)
        {
            Error = "That import has expired. Please upload the file again.";
            return Page();
        }

        var result = await pipeline.ProcessAsync(import.Raw, skipClassification: true, ct);
        LogResult(result);
        if (result.Status != PipelineStatus.Parsed)
        {
            Error = result.Message;
            return Page();
        }

        pending.Update(import with { Result = result });
        return RedirectToPage("/Review", new { id });
    }

    /// <summary>Decrypts a .sift export and opens it on the review page (in memory only until the user saves it).</summary>
    public async Task<IActionResult> OnPostOpenExportAsync(IFormFile? exportFile, string? passphrase, CancellationToken ct)
    {
        await LoadPageAsync(ct);
        if (exportFile is null || exportFile.Length == 0 || string.IsNullOrEmpty(passphrase))
        {
            OpenError = "Choose a .sift file and enter its passphrase.";
            return Page();
        }
        if (exportFile.Length > MaxExportBytes)
        {
            OpenError = "That file is too large to be a Sift export.";
            return Page();
        }

        await using var stream = exportFile.OpenReadStream();
        var data = (await RawEmail.ReadAsync(stream, "export", MaxExportBytes, ct)).Content;

        Receipt receipt;
        try
        {
            receipt = ReceiptExporter.Import(data, passphrase);
        }
        catch (Exception ex) when (ex is SiftCryptoException or System.Text.Json.JsonException or InvalidDataException)
        {
            OpenError = ex is SiftCryptoException ? ex.Message : "The file decrypted, but its contents aren't a Sift receipt.";
            logger.LogInformation("Export could not be opened: {Reason}", ex.GetType().Name);
            return Page();
        }

        if (store.FindDuplicate(receipt.ImportId) is not null && !receipt.Warnings.Any(w => w.Code == WarningCodes.DuplicateImport))
        {
            receipt = receipt.WithWarning(new ValidationWarning(WarningCodes.DuplicateImport, null, WarningSeverity.Warning,
                "A receipt from the same email is already saved on this computer."));
        }

        var result = new PipelineResult(PipelineStatus.Parsed, null, Receipt: validator.Validate(receipt), ExtractorName: ExportExtractorName);
        var import = pending.Add(new RawEmail("export", []), result);
        logger.LogInformation("Opened export {ImportHash}", receipt.ImportId.ContentHash[..12]);
        return RedirectToPage("/Review", new { id = import.Id });
    }

    private async Task LoadPageAsync(CancellationToken ct)
    {
        if (model is not null) Model = (model.ModelName, await model.IsReadyAsync(ct));
        LoadSaved();
    }

    private void LoadSaved()
    {
        try
        {
            Saved = store.List();
        }
        catch (SiftCryptoException ex)
        {
            Error = ex.Message;
        }
    }

    /// <summary>Logs only a short hash, the outcome and warning codes: never names, amounts, bodies or snippets.</summary>
    private void LogResult(PipelineResult result) =>
        logger.LogInformation("Import {ImportHash}: {Status} via {Extractor}; warnings [{Warnings}]",
            result.Receipt?.ImportId.ContentHash[..12] ?? "-",
            result.Status,
            result.ExtractorName ?? "-",
            string.Join(",", result.Receipt?.Warnings.Select(w => w.Code) ?? []));
}
