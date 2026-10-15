using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Localization;
using Sift.Core.Crypto;
using Sift.Core.Extraction.LocalModel;
using Sift.Core.Ingestion;
using Sift.Core.Pipeline;
using Sift.Core.Storage;
using Sift.Core.Validation;
using Sift.Web;

var builder = WebApplication.CreateBuilder(args);

// Loopback only: the app is never reachable from another machine.
var port = builder.Configuration.GetValue("Sift:Port", 5080);
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Listen(IPAddress.Loopback, port);
    // Reject oversized uploads before the body is read (small allowance for multipart framing).
    kestrel.Limits.MaxRequestBodySize = EmailParser.DefaultMaxBytes + 64 * 1024;
});
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = EmailParser.DefaultMaxBytes);

builder.Services.AddRazorPages();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<PendingImports>();
builder.Services.AddSingleton<ReceiptValidator>();
builder.Services.AddSingleton(_ => new LocalReceiptStore(
    builder.Configuration["Sift:StoreDirectory"] ?? LocalReceiptStore.DefaultDirectory,
    KeyProtectors.ForCurrentPlatform()));
// The local model reads receipts when enabled; OllamaClient refuses any endpoint that isn't on this machine.
var model = builder.Configuration.GetSection("Sift:Model");
if (model.GetValue("Enabled", true))
{
    builder.Services.AddSingleton<ILocalModelClient>(_ => new OllamaClient(new OllamaOptions
    {
        Endpoint = new Uri(model["Endpoint"] ?? "http://127.0.0.1:11434"),
        Model = model["Name"] ?? OllamaOptions.DefaultModel,
    }));
}
builder.Services.AddSingleton(sp => ReceiptPipeline.CreateDefault(
    sp.GetRequiredService<LocalReceiptStore>(),
    sp.GetService<ILocalModelClient>()));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    // Nothing remote can load, and nothing inline can run: email content is only ever shown as encoded text.
    headers.ContentSecurityPolicy =
        "default-src 'self'; img-src 'self'; style-src 'self'; script-src 'self'; object-src 'none'; " +
        "base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    headers.XContentTypeOptions = "nosniff";
    headers["Referrer-Policy"] = "no-referrer";
    // No Cache-Control here: antiforgery already marks every page with a form (i.e. every page showing receipt data) no-store.
    await next();
});

// Amounts and dates are posted in invariant format regardless of the machine's regional settings.
app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture(CultureInfo.InvariantCulture),
    SupportedCultures = [CultureInfo.InvariantCulture],
    SupportedUICultures = [CultureInfo.InvariantCulture],
    RequestCultureProviders = [],
});

app.UseRouting();
app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();

app.Run();
