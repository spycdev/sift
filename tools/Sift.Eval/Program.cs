// Scores an extractor against a fixture set and reports per-field accuracy and latency.
//
//   dotnet run --project tools/Sift.Eval -- --rules --set dev --details
//   dotnet run --project tools/Sift.Eval -- --model qwen3:4b-instruct-2507-q4_K_M --set holdout
//
// For the holdout set, leave out --details: it prints expected values, and looking at them while
// tuning turns the holdout set into a second dev set.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Sift.Core.Extraction.LocalModel;
using Sift.Core.Pipeline;
using Sift.Eval;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

string? model = null, samplesDir = null, outPath = null;
var set = "dev";
var details = false;
var trace = false;   // dev only: prints the model's raw answer and the warnings for emails that aren't fully correct
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--model": model = args[++i]; break;
        case "--rules": model = null; break;
        case "--set": set = args[++i]; break;
        case "--details": details = true; break;
        case "--trace": trace = true; break;
        case "--samples": samplesDir = args[++i]; break;
        case "--out": outPath = args[++i]; break;
        default:
            Console.Error.WriteLine($"Unknown argument {args[i]}. Use --rules | --model <name>, --set dev|holdout, --details, --samples <dir>, --out <file>.");
            return 2;
    }
}

var samples = samplesDir is not null ? new Samples(samplesDir) : Samples.Find(Directory.GetCurrentDirectory());
if (samples is null || samples.Names(set).Count == 0)
{
    Console.Error.WriteLine($"No samples found for set '{set}'. Pass --samples <dir>.");
    return 2;
}

if (trace && set != "dev")
{
    Console.Error.WriteLine("--trace prints email content and is only allowed on the dev set.");
    return 2;
}

using var ollama = model is null ? null : new OllamaClient(new OllamaOptions { Model = model });
if (ollama is not null && !await ollama.IsReadyAsync())
{
    Console.Error.WriteLine($"Model '{model}' isn't available. Is Ollama running? Try: ollama pull {model}");
    return 1;
}
var client = ollama is null ? null : new RecordingClient(ollama);
var pipeline = ReceiptPipeline.CreateDefault(model: client);

var rows = new List<(string Name, double Seconds, string Extractor, IReadOnlyList<FieldScore> Scores)>();
foreach (var name in samples.Names(set))
{
    var (raw, expected) = samples.Load(set, name);
    var timer = Stopwatch.StartNew();
    var result = await pipeline.ProcessAsync(raw);
    timer.Stop();
    var scores = Scoring.Score(expected, result);
    rows.Add((name, timer.Elapsed.TotalSeconds, result.ExtractorName ?? result.Status.ToString(), scores));
    Console.Error.WriteLine($"  {name}: {timer.Elapsed.TotalSeconds:0.0}s, {scores.Count(s => s.Outcome == Outcome.Correct)}/{scores.Count} correct");
    if (trace && scores.Any(s => s.Outcome != Outcome.Correct))
    {
        Console.Error.WriteLine($"    model answer: {client?.LastAnswer}");
        foreach (var w in result.Receipt?.Warnings ?? []) Console.Error.WriteLine($"    warning {w.Code} {w.Field}: {w.Message}");
    }
}

var all = rows.SelectMany(r => r.Scores).ToList();
string Pct(Outcome o) => $"{100.0 * all.Count(s => s.Outcome == o) / all.Count:0}%";
var seconds = rows.Select(r => r.Seconds).ToList();

var report = new StringBuilder()
    .AppendLine($"# {(model is null ? "Rules" : model)} on {set} ({rows.Count} emails)")
    .AppendLine()
    .AppendLine($"**Correct {Pct(Outcome.Correct)} · Wrong {Pct(Outcome.Wrong)} · Flagged {Pct(Outcome.Flagged)} · Silent miss {Pct(Outcome.SilentMiss)}** over {all.Count} fields")
    .AppendLine()
    .AppendLine($"Time per email: mean {seconds.Average():0.0}s, max {seconds.Max():0.0}s")
    .AppendLine()
    .AppendLine("| Field | Correct | Wrong | Flagged | Silent miss |")
    .AppendLine("|---|---:|---:|---:|---:|");
foreach (var field in Scoring.Fields)
{
    var outcomes = all.Where(s => s.Field == field).Select(s => s.Outcome).ToList();
    report.AppendLine($"| {field} | {outcomes.Count(o => o == Outcome.Correct)} | {outcomes.Count(o => o == Outcome.Wrong)} | {outcomes.Count(o => o == Outcome.Flagged)} | {outcomes.Count(o => o == Outcome.SilentMiss)} |");
}

if (details)
{
    report.AppendLine().AppendLine("## Not correct").AppendLine();
    foreach (var (name, _, extractor, scores) in rows)
    {
        foreach (var s in scores.Where(s => s.Outcome != Outcome.Correct))
            report.AppendLine($"- `{name}` ({extractor}) {s.Field} [{s.Outcome}]: expected `{s.Expected}`, got `{s.Actual}`");
    }
}

Console.WriteLine(report);
if (outPath is not null) File.WriteAllText(outPath, report.ToString());
return 0;

/// <summary>Remembers the model's last raw answer for --trace.</summary>
sealed class RecordingClient(ILocalModelClient inner) : ILocalModelClient
{
    public string? LastAnswer { get; private set; }
    public string ModelName => inner.ModelName;
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => inner.IsReadyAsync(ct);

    public async Task<string> CompleteJsonAsync(string systemPrompt, string userPrompt, System.Text.Json.Nodes.JsonObject schema, CancellationToken ct = default)
    {
        LastAnswer = null;
        return LastAnswer = await inner.CompleteJsonAsync(systemPrompt, userPrompt, schema, ct);
    }
}
