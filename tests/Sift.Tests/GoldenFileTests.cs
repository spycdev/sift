using Sift.Eval;
using Sift.Tests.Support;

namespace Sift.Tests;

/// <summary>
/// Rules-only golden tests over samples/dev. Model accuracy, and anything on samples/holdout,
/// is measured with tools/Sift.Eval instead.
/// </summary>
public sealed class GoldenFileTests
{
    public static TheoryData<string> DevSamples() => new(Fixtures.Names("dev"));

    [Fact]
    public void Dev_set_is_present()
    {
        Assert.NotEmpty(Fixtures.Names("dev"));
    }

    /// <summary>The dev set is what the parsers are tuned against, so every field must be exact.</summary>
    [Theory]
    [MemberData(nameof(DevSamples))]
    public async Task Dev_sample_matches_expected(string name)
    {
        var (raw, expected) = Fixtures.Load("dev", name);

        var result = await Fixtures.Rules(raw);

        Assert.Equal(expected.Status, result.Status.ToString());
        if (result.Receipt is not { } receipt) return;

        var failures = Scoring.Score(expected, result)
            .Where(s => s.Outcome != Outcome.Correct)
            .Select(s => $"{s.Field} [{s.Outcome}]\n  expected: {s.Expected}\n  actual:   {s.Actual}")
            .ToList();
        Assert.True(failures.Count == 0, string.Join("\n", failures));

        var actualCodes = receipt.Warnings.Select(w => w.Code).Distinct().Order();
        Assert.Equal((expected.Warnings ?? []).Order(), actualCodes);
    }
}
