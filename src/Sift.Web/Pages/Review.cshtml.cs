using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sift.Core.Crypto;
using Sift.Core.Editing;
using Sift.Core.Model;
using Sift.Core.Storage;
using Sift.Core.Validation;

namespace Sift.Web.Pages;

public sealed class ReviewModel(
    PendingImports pending,
    ReceiptValidator validator,
    LocalReceiptStore store,
    ILogger<ReviewModel> logger) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Id { get; set; }

    [BindProperty]
    public ReceiptForm Form { get; set; } = new();

    [BindProperty]
    public string? Passphrase { get; set; }

    public PendingImport Import { get; private set; } = null!;
    public Receipt Receipt => Import.Result.Receipt!;
    public string? Error { get; private set; }

    public IActionResult OnGet()
    {
        if (!TryLoad()) return RedirectToPage("/Index");
        Form = ReceiptForm.From(Receipt);
        return Page();
    }

    public IActionResult OnPostRecheck() => WithEdits(Page);

    public IActionResult OnPostSave() => WithEdits(() =>
    {
        var id = store.Save(Receipt);
        pending.Remove(Import.Id);
        logger.LogInformation("Saved receipt {ReceiptId}", id);
        return RedirectToPage("/Receipts/Details", new { id });
    });

    public IActionResult OnPostExport() => WithEdits(() =>
    {
        if (Passphrase is null || Passphrase.Length < ExportCrypto.MinPassphraseLength)
        {
            Error = $"Choose a passphrase of at least {ExportCrypto.MinPassphraseLength} characters.";
            return Page();
        }
        return File(ReceiptExporter.Export(Receipt, Passphrase), "application/octet-stream", ReceiptExporter.FileName(DateTimeOffset.Now));
    });

    public IActionResult OnPostDiscard()
    {
        if (Id is not null) pending.Remove(Id);
        return RedirectToPage("/Index");
    }

    /// <summary>Applies the posted edits, re-validates, keeps the result for the next request, then continues.</summary>
    private IActionResult WithEdits(Func<IActionResult> next)
    {
        if (!TryLoad()) return RedirectToPage("/Index");
        if (!ModelState.IsValid)
        {
            Error = "Some values couldn't be read. Amounts use a dot for decimals, e.g. 12.50.";
            return Page();
        }

        var updated = validator.Validate(ReceiptEditor.Apply(Receipt, Form.ToEdit()));
        Import = Import with { Result = Import.Result with { Receipt = updated } };
        pending.Update(Import);

        ModelState.Clear();
        Form = ReceiptForm.From(updated);
        return next();
    }

    private bool TryLoad()
    {
        if (pending.Get(Id) is not { Result.Receipt: not null } import) return false;
        Import = import;
        return true;
    }
}
