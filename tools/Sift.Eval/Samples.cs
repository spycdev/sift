using System.Text.Json;
using Sift.Core.Ingestion;
using Sift.Core.Model;

namespace Sift.Eval;

public sealed record ExpectedItem(string Description, decimal? Quantity, decimal? UnitPrice, decimal? LineTotal);

public sealed record ExpectedAdjustment(AdjustmentKind Kind, decimal Amount);

/// <summary>The contents of a fixture's .expected.json. A null field means "should not be extracted".</summary>
public sealed record ExpectedReceipt(
    string Status,
    string? Merchant,
    DateOnly? PurchasedOn,
    string? Currency,
    string? OrderNumber,
    List<ExpectedItem>? Items,
    List<ExpectedAdjustment>? Adjustments,
    decimal? Subtotal,
    decimal? Tax,
    decimal? Total,
    List<string>? Warnings);

/// <summary>Fixture sets under a samples root: {root}/{set}/name.eml + name.expected.json.</summary>
public sealed class Samples(string root)
{
    public string Root => root;

    public IReadOnlyList<string> Names(string set)
    {
        var dir = Path.Combine(root, set);
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.eml").Select(f => Path.GetFileNameWithoutExtension(f)).Order().ToList()
            : [];
    }

    public (RawEmail Raw, ExpectedReceipt Expected) Load(string set, string name)
    {
        var dir = Path.Combine(root, set);
        var raw = new RawEmail(name + ".eml", File.ReadAllBytes(Path.Combine(dir, name + ".eml")));
        var expected = JsonSerializer.Deserialize<ExpectedReceipt>(
            File.ReadAllBytes(Path.Combine(dir, name + ".expected.json")), ReceiptJson.Options)
            ?? throw new InvalidDataException($"{name}.expected.json is empty.");
        return (raw, expected);
    }

    /// <summary>Walks up from <paramref name="start"/> to the first directory containing samples/dev.</summary>
    public static Samples? Find(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "samples");
            if (Directory.Exists(Path.Combine(candidate, "dev"))) return new Samples(candidate);
        }
        return null;
    }
}
