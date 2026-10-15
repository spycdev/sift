using System.Globalization;
using System.Text.RegularExpressions;

namespace Sift.Core.Extraction;

/// <param name="Text">The full matched text, including currency marker and sign.</param>
/// <param name="Number">Just the digits and separators.</param>
/// <param name="IsMoney">Has a currency marker or a two-digit decimal part; bare integers are quantities, years, etc.</param>
public sealed record MoneyToken(string Text, string Number, string? Marker, bool Negative, int Index, bool IsMoney);

/// <param name="DecimalSeparator">'.' or ',' when the receipt shows which; null when it never uses one or the currency has no minor units.</param>
/// <param name="Ambiguous">The receipt contains amounts whose meaning depends on a separator it never disambiguates, or it contradicts itself.</param>
public sealed record NumberFormat(char? DecimalSeparator, int? MinorUnits, bool Ambiguous);

/// <summary>
/// Finds and parses money amounts without relying on the machine's culture. The decimal separator
/// is inferred once per receipt, because "1,234" means different things on different receipts.
/// </summary>
public static partial class MoneyParser
{
    private const char Unresolved = '?';
    private const char NoSeparator = '\0';

    public static List<MoneyToken> FindTokens(string text)
    {
        var tokens = new List<MoneyToken>();
        foreach (Match m in Token().Matches(text))
        {
            var number = m.Groups["num"].Value;
            var marker = m.Groups["pre"].Success ? m.Groups["pre"].Value.Trim()
                : m.Groups["suf"].Success ? m.Groups["suf"].Value
                : null;
            var negative = m.Groups["neg"].Success || m.Groups["neg2"].Success;
            var isMoney = marker is not null || TwoDigitDecimals().IsMatch(number);
            tokens.Add(new MoneyToken(m.Value, number, marker, negative, m.Index, isMoney));
        }
        return tokens;
    }

    public static NumberFormat InferFormat(IEnumerable<MoneyToken> tokens, int? minorUnits)
    {
        if (minorUnits == 0) return new NumberFormat(null, 0, false);

        int dot = 0, comma = 0;
        var unresolved = false;
        foreach (var token in tokens.Where(t => t.IsMoney))
        {
            switch (DecimalSeparatorOf(token.Number, minorUnits))
            {
                case '.': dot++; break;
                case ',': comma++; break;
                case Unresolved: unresolved = true; break;
            }
        }

        if (dot > 0 && comma > 0) return new NumberFormat(dot >= comma ? '.' : ',', minorUnits, true);
        if (dot > 0) return new NumberFormat('.', minorUnits, false);
        if (comma > 0) return new NumberFormat(',', minorUnits, false);
        return new NumberFormat(null, minorUnits, unresolved);
    }

    public static bool TryParse(MoneyToken token, NumberFormat format, out decimal value)
    {
        value = 0;
        var number = token.Number.Replace(" ", "").Replace(" ", "");

        char? decimalSeparator;
        if (format.MinorUnits == 0)
        {
            decimalSeparator = null;
        }
        else
        {
            var own = DecimalSeparatorOf(number, format.MinorUnits);
            if (own == Unresolved && format.DecimalSeparator is null) return false;
            decimalSeparator = own switch
            {
                NoSeparator => null,
                Unresolved => format.DecimalSeparator,
                _ => own,
            };
        }

        string invariant;
        if (decimalSeparator is null)
        {
            invariant = number.Replace(".", "").Replace(",", "");
        }
        else
        {
            var grouping = decimalSeparator == '.' ? "," : ".";
            var stripped = number.Replace(grouping, "");
            if (stripped.Count(c => c == decimalSeparator) > 1) return false;
            invariant = stripped.Replace(decimalSeparator.Value, '.');
        }

        if (!decimal.TryParse(invariant, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)) return false;
        if (token.Negative) value = -value;
        return true;
    }

    /// <summary>The decimal separator a single number implies on its own.</summary>
    private static char DecimalSeparatorOf(string number, int? minorUnits)
    {
        var separators = number.Where(c => c is '.' or ',').ToArray();
        if (separators.Length == 0) return NoSeparator;

        var last = number.LastIndexOfAny(['.', ',']);
        var lastSeparator = number[last];
        var other = lastSeparator == '.' ? ',' : '.';

        if (separators.Distinct().Count() > 1) return lastSeparator;   // 1.234,56 / 1,234.56
        if (separators.Length > 1) return other;                     // 1,234,567: repeated means grouping

        var digitsAfter = number.Length - last - 1;
        if (digitsAfter != 3) return lastSeparator;                  // 4.50 / 4,5

        // Exactly three digits after a single separator: grouping, unless the currency has three decimals.
        return minorUnits switch
        {
            3 => lastSeparator,
            null => Unresolved,
            _ => other,
        };
    }

    // Lookbehind keeps us from starting inside a word or another number (e.g. "x2", "NW-48213").
    [GeneratedRegex("""
        (?<![\w.,])
        (?<neg>[-−–]\s?)?
        (?<pre>US\$|CA\$|C\$|AU\$|A\$|NZ\$|[$€£¥￥₹]|(?:USD|EUR|GBP|CAD|AUD|NZD|CHF|SEK|NOK|DKK|INR|CNY|MXN|SGD|HKD|JPY|KRW|KWD|BHD)\s?)?
        (?<neg2>[-−]\s?)?
        (?<num>\d{1,3}(?:[.,  ]\d{3})+(?:[.,]\d{1,3})?|\d+(?:[.,]\d{1,3})?)
        (?!\d)
        (?:\s?(?<suf>€|£|kr|(?:USD|EUR|GBP|CAD|AUD|NZD|CHF|SEK|NOK|DKK|INR|CNY|MXN|SGD|HKD|JPY|KRW|KWD|BHD)\b))?
        """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex Token();

    [GeneratedRegex(@"[.,]\d{2}$")]
    private static partial Regex TwoDigitDecimals();
}
