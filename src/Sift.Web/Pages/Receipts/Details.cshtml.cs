using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sift.Core.Crypto;
using Sift.Core.Model;
using Sift.Core.Storage;

namespace Sift.Web.Pages.Receipts;

public sealed class DetailsModel(LocalReceiptStore store) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Id { get; set; }

    [BindProperty]
    public string? Passphrase { get; set; }

    public Receipt? Receipt { get; private set; }
    public string? Error { get; private set; }

    public IActionResult OnGet() => Load() ? Page() : NotFound();

    public IActionResult OnPostExport()
    {
        if (!Load()) return NotFound();
        if (Receipt is null) return Page();
        if (Passphrase is null || Passphrase.Length < ExportCrypto.MinPassphraseLength)
        {
            Error = $"Choose a passphrase of at least {ExportCrypto.MinPassphraseLength} characters.";
            return Page();
        }
        return File(ReceiptExporter.Export(Receipt, Passphrase), "application/octet-stream", ReceiptExporter.FileName(DateTimeOffset.Now));
    }

    public IActionResult OnPostDelete()
    {
        if (Id is not null) store.Delete(Id);
        return RedirectToPage("/Index");
    }

    /// <returns>False when there is no such receipt. A receipt that fails its integrity check is reported, not hidden.</returns>
    private bool Load()
    {
        try
        {
            Receipt = store.Load(Id ?? "");
            return Receipt is not null;
        }
        catch (SiftCryptoException ex)
        {
            Error = ex.Message;
            return true;
        }
    }
}
