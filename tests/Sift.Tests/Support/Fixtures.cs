using Sift.Core.Ingestion;
using Sift.Core.Pipeline;
using Sift.Eval;

namespace Sift.Tests.Support;

/// <summary>The samples copied next to the test binaries, plus helpers for ad-hoc emails.</summary>
public static class Fixtures
{
    public static readonly Samples Set = new(Path.Combine(AppContext.BaseDirectory, "samples"));

    public static IReadOnlyList<string> Names(string set) => Set.Names(set);

    public static (RawEmail Raw, ExpectedReceipt Expected) Load(string set, string name) => Set.Load(set, name);

    public static RawEmail Eml(string text) => new("test.eml", System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>Runs the rules-only pipeline (no model), which is deterministic and needs no services.</summary>
    public static Task<PipelineResult> Rules(RawEmail raw, bool skipClassification = false) =>
        ReceiptPipeline.CreateDefault().ProcessAsync(raw, skipClassification);
}
