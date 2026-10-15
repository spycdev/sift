using System.Text.RegularExpressions;
using Sift.Core.Model;

namespace Sift.Core.Extraction;

public static class Currencies
{
    // ISO 4217 minor units for the currencies the prototype recognizes.
    private static readonly Dictionary<string, int> MinorUnitsByCode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USD"] = 2, ["EUR"] = 2, ["GBP"] = 2, ["CAD"] = 2, ["AUD"] = 2, ["NZD"] = 2, ["CHF"] = 2,
        ["SEK"] = 2, ["NOK"] = 2, ["DKK"] = 2, ["INR"] = 2, ["CNY"] = 2, ["MXN"] = 2, ["SGD"] = 2, ["HKD"] = 2,
        ["JPY"] = 0, ["KRW"] = 0,
        ["KWD"] = 3, ["BHD"] = 3,
    };

    public static IEnumerable<string> Codes => MinorUnitsByCode.Keys;

    public static bool IsKnown(string? code) => code is not null && MinorUnitsByCode.ContainsKey(code);

    /// <summary>Decimal places for the currency, or null when the currency is unknown.</summary>
    public static int? MinorUnits(string? code) =>
        code is not null && MinorUnitsByCode.TryGetValue(code, out var units) ? units : null;

    /// <summary>The smallest amount of the currency (0.01 for USD, 1 for JPY). Unknown currencies use 0.01.</summary>
    public static decimal SmallestUnit(string? code) => (MinorUnits(code) ?? 2) switch
    {
        0 => 1m,
        3 => 0.001m,
        _ => 0.01m,
    };

    public static string Format(decimal amount, string? code)
    {
        var places = MinorUnits(code) ?? 2;
        var number = amount.ToString("N" + places, System.Globalization.CultureInfo.InvariantCulture);
        return code is null ? number : $"{number} {code}";
    }
}

public sealed record CurrencyDetection(string? Code, double Confidence, string? Snippet, ValidationWarning? Warning);

/// <summary>
/// Decides a receipt's currency from explicit ISO codes first, then unambiguous symbols, then
/// "$" plus the sender's country domain. A bare "$" with nothing else is reported, not guessed.
/// </summary>
public static partial class CurrencyDetector
{
    private static readonly Dictionary<string, string> SymbolCodes = new()
    {
        ["US$"] = "USD", ["CA$"] = "CAD", ["C$"] = "CAD", ["AU$"] = "AUD", ["A$"] = "AUD", ["NZ$"] = "NZD",
        ["€"] = "EUR", ["£"] = "GBP", ["¥"] = "JPY", ["￥"] = "JPY", ["₹"] = "INR",
    };

    private static readonly Dictionary<string, string> DollarTlds = new()
    {
        [".ca"] = "CAD", [".au"] = "AUD", [".nz"] = "NZD", [".us"] = "USD",
    };

    public static CurrencyDetection Detect(string text, string? senderDomain)
    {
        var lines = text.Split('\n');

        var codes = Tally(lines, IsoCode(), m => m.Value.ToUpperInvariant());
        if (codes.Count > 0) return Choose(codes, singleConfidence: 0.95);

        var symbols = Tally(lines, Symbol(), m => SymbolCodes.GetValueOrDefault(m.Value, "$"));
        var dollar = symbols.GetValueOrDefault("$");
        symbols.Remove("$");
        if (symbols.Count > 0)
        {
            if (dollar is not null) symbols["$"] = dollar;
            var choice = Choose(symbols, singleConfidence: 0.9);
            // ¥ is also used for Chinese yuan.
            return choice.Code == "JPY" && choice.Warning is null ? choice with { Confidence = 0.8 } : choice;
        }

        if (dollar is not null)
        {
            var tld = DollarTlds.Keys.FirstOrDefault(t => senderDomain?.EndsWith(t, StringComparison.OrdinalIgnoreCase) == true);
            if (tld is not null) return new CurrencyDetection(DollarTlds[tld], 0.75, $"{dollar.FirstLine} (sender domain {senderDomain})", null);

            return new CurrencyDetection(null, 0, dollar.FirstLine, new ValidationWarning(
                WarningCodes.AmbiguousCurrency, nameof(Receipt.Currency), WarningSeverity.Warning,
                "Amounts use \"$\", which could be US, Canadian, Australian or another dollar, and nothing else in the email says which."));
        }

        return new CurrencyDetection(null, 0, null, null);
    }

    private sealed record Seen(int Count, string FirstLine);

    private static Dictionary<string, Seen> Tally(string[] lines, Regex pattern, Func<Match, string> key)
    {
        var seen = new Dictionary<string, Seen>();
        foreach (var line in lines)
        {
            foreach (Match m in pattern.Matches(line))
            {
                var k = key(m);
                seen[k] = seen.TryGetValue(k, out var s) ? s with { Count = s.Count + 1 } : new Seen(1, line);
            }
        }
        return seen;
    }

    private static CurrencyDetection Choose(Dictionary<string, Seen> found, double singleConfidence)
    {
        // A bare "$" never wins: it only matters as evidence that more than one currency is present.
        var best = found.Where(kv => kv.Key != "$").MaxBy(kv => kv.Value.Count);
        if (found.Count == 1) return new CurrencyDetection(best.Key, singleConfidence, best.Value.FirstLine, null);

        var warning = new ValidationWarning(
            WarningCodes.MixedCurrency, nameof(Receipt.Currency), WarningSeverity.Warning,
            $"The email mentions several currencies ({string.Join(", ", found.Keys)}); {best.Key} appears most often.");
        return new CurrencyDetection(best.Key, 0.6, best.Value.FirstLine, warning);
    }

    [GeneratedRegex(@"\b(?:USD|EUR|GBP|CAD|AUD|NZD|CHF|SEK|NOK|DKK|INR|CNY|MXN|SGD|HKD|JPY|KRW|KWD|BHD)\b")]
    private static partial Regex IsoCode();

    [GeneratedRegex(@"(?<![A-Za-z])(?:US\$|CA\$|C\$|AU\$|A\$|NZ\$|\$|€|£|¥|￥|₹)")]
    private static partial Regex Symbol();
}
