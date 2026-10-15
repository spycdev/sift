# Sift

A privacy-first receipt parser. Import a receipt email (`.eml`), get structured purchase data
(merchant, date, currency, items, discounts, tax, total), review and correct it, then store it
encrypted on your machine or export an encrypted copy.

Everything runs locally. Emails are never uploaded, remote images (tracking pixels) are never
loaded, and nothing readable is written to disk.

## Running it

Requires Windows and the .NET 10 SDK. For model-based reading (the default), also install
[Ollama](https://ollama.com) and pull the model:

```powershell
ollama pull qwen3:4b-instruct-2507-q4_K_M
dotnet run --project src/Sift.Web
```

The model is set under `Sift:Model` in `appsettings.json` (`Name`, `Endpoint`, `Enabled`). If
Ollama isn't running, Sift falls back to its built-in rules and says so on every receipt.

Then open <http://127.0.0.1:5080>. The app listens on the loopback interface only.

Receipts are stored in `%LOCALAPPDATA%\Sift`. To use another folder, set `Sift:StoreDirectory`
(for example the `Sift__StoreDirectory` environment variable).

```powershell
dotnet test
```

## Demo walkthrough

1. Import `samples/dev/02-contoso-electronics-plain.eml`. The review page shows every value with
   where it came from (the source line), how it was found and how confident the parser is.
2. Change the merchant and choose **Re-check**. The field is now marked *edited by you* at 100%.
3. **Save to this computer**, then look in `%LOCALAPPDATA%\Sift`: only ciphertext.
4. Import the same email again and you'll get a `DUPLICATE_IMPORT` warning.
5. Import `08-arithmetic-mismatch.eml`. The total doesn't add up, and Sift says so instead of
   "fixing" it.
6. Import `11-ambiguous-dollar.eml`. A bare `$` from an unknown country leaves the currency empty,
   with a warning.
7. Import `10-shipping-notice.eml`. It's rejected with the signals behind the decision, and you can
   parse it anyway.
8. Export a receipt with a passphrase. The `.sift` file opens anywhere with the passphrase and
   nowhere without it.
9. On the import page, use **Open an encrypted export**: pick the `.sift` file and enter the
   passphrase. The receipt opens for review, and you can save it to this computer from there. A
   wrong passphrase or a modified file is refused.

## How it works

```text
IEmailSource (.eml today; Gmail/Outlook later)
  → EmailParser           MIME via MimeKit; forwarded emails unwrapped; HTML → text via AngleSharp, no resource loading
  → IReceiptExtractor     best available wins:
        ModelReceiptExtractor   local model via Ollama; decides receipt or not, extracts, then every value is grounded
        merchant rules / generic rules   fallback when the model is off or unavailable (gated by ReceiptClassifier)
  → ReceiptValidator      arithmetic, missing fields, low confidence; re-runs after every edit
  → LocalReceiptStore / ReceiptExporter
```

| Project | Contents |
|---|---|
| `src/Sift.Core` | Parsing, extraction, validation, editing, encryption, storage. No web dependencies. |
| `src/Sift.Web` | Razor Pages UI. |
| `tests/Sift.Tests` | Unit tests plus golden-file tests over `samples/`. |
| `tools/Sift.Eval` | Scores rules or a model on `samples/dev` or `samples/holdout`. |

### How the model is kept honest

A small model is good at reading unfamiliar layouts but can produce plausible values that aren't
in the email. Sift accepts a model answer only after checking it against the email:

- **Grounding.** The model returns each value with the exact text it came from. A value that doesn't
  appear in the email is discarded and flagged (`UNGROUNDED_VALUE`).
- **The model never does arithmetic or number formatting.** It copies amounts as printed
  (`"18,90 €"`), and the same money parser used by the rules converts them.
- **Currency and dates are re-derived.** Currency comes from the currency detector. Dates are
  re-read from the quoted text, and the email's sent date is never used as a fallback.
- **Quantities must be written in the item's line**, or they're dropped.
- **The validator still checks the arithmetic**, which catches a real amount assigned to the wrong
  field.

Output is constrained to a JSON schema (Ollama structured outputs), and decoding is deterministic
(temperature 0, fixed seed).

**Every value carries provenance.** Each field is an `Extracted<T>` (value, method, confidence,
source snippet). A field that wasn't found is `null`, never `0` or `""`. A missing tax and a
zero tax are different things.

**The parser prefers flagging to guessing.** Examples:

- `"1,250"` with an unknown currency could be 1250 or 1.25, so it's left out and flagged.
- `03/04/2026` could be day/month or month/day, so it's flagged.
- A bare `$` could be several dollar currencies, so the currency is flagged.

Currency is detected first because it determines how many decimal places an amount can have
(JPY 0, USD 2, KWD 3). Arithmetic checks allow one smallest currency unit per rounded term.

**Adding a merchant** means adding a `MerchantProfile` (name, sender domains, currency) to
`MerchantProfiles.Default`. **Adding an input source** means implementing `IEmailSource`;
nothing downstream changes.

## Security and privacy

### Threat model

**What it protects against**

- Someone who gets an export file without its passphrase.
- Tampering with exports or stored receipts. Any change, including to headers, fails authentication.
- Swapping or renaming stored files.
- Other Windows users on the same machine reading the store.

**What it doesn't protect against**

- Malware running as your user.
- Your email provider, which already has the email.
- Inspection of the app's memory while it runs.

**Key loss**

- If you forget an export's passphrase, that export is gone. There is deliberately no recovery.
- If the Windows profile is lost, the local store can't be read. Export anything you need to keep.

**What leaves the device:** nothing, except export files you choose to save. Email text is sent
only to the local model service. `OllamaClient` refuses any endpoint that isn't a loopback address,
so a configuration change can't send receipts to another machine.

### Mechanisms

**Local store**

- One file per receipt, plus an index.
- Each file is sealed with AES-256-GCM under a random data key. The key is wrapped with DPAPI
  (`CurrentUser`).
- Each receipt file authenticates its own id, so files can't be swapped between ids.

**Export format**

```text
"SIFT" | version | KDF id | iterations | salt (16) | nonce (12) | tag (16) | ciphertext
```

- The key is derived with PBKDF2-SHA256: 600,000 iterations and a fresh salt per export.
- The header and nonce are authenticated as associated data.
- Iteration counts outside 100k–10M are rejected before any key derivation work.
- Export file names contain only a timestamp, because names sit outside the ciphertext.

**Web UI**

- Listens on loopback only, and accepts only `127.0.0.1`/`localhost` as the `Host` header, which blocks
  DNS-rebinding attacks from web pages. Antiforgery tokens on every form.
- Uploads over 5 MB are rejected before the body is read.
- The original email HTML is never rendered. Only extracted text is shown, HTML-encoded.
- A strict Content-Security-Policy (`default-src 'self'`, no remote images) is set.

**Logging:** only a short content hash, the outcome and warning codes. Never names, amounts,
email text or passphrases.

## Measuring accuracy

`samples/dev` holds the fixtures the parsers are tuned on, and every field there must match exactly.
`samples/holdout` is for measuring accuracy and must not be looked at while tuning.

```powershell
dotnet run --project tools/Sift.Eval -- --rules --set dev --details
dotnet run --project tools/Sift.Eval -- --model qwen3:4b-instruct-2507-q4_K_M --set holdout
```

Leave out `--details` for the holdout set, since it prints the expected values. Each field is
scored as one of:

- **correct**
- **wrong**: present but incorrect, the worst case
- **flagged**: missing, with a warning
- **silent miss**

The headline number is a low *wrong* rate.

## Limitations / next steps

- No OCR and no PDF receipts (detected and reported).
- A 4B model on a 6 GB GPU takes seconds per receipt, not milliseconds.
- The rules fallback understands English labels only. The model handles other languages.
- One merchant profile so far (`Northwind Coffee`); it's only used by the rules fallback.
- The local store requires Windows (DPAPI).
- The dev samples were written alongside the parser, so they show it works but not how well it
  generalizes. That's the job of the holdout set.