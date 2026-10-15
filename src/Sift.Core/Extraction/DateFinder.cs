using System.Text.RegularExpressions;

namespace Sift.Core.Extraction;

public sealed record DateDetection(DateOnly? Date, string? Line, bool Ambiguous);

/// <summary>
/// Finds a purchase date on a labeled line ("Order date: ..."). Numeric day/month orders that
/// could go either way (03/04/2026) are reported as ambiguous instead of guessed.
/// </summary>
public static partial class DateFinder
{
    private static readonly string[] Months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    public static DateDetection FindLabeled(string text)
    {
        DateDetection? ambiguous = null;
        foreach (var line in text.Split('\n'))
        {
            if (!DateLabel().IsMatch(line)) continue;
            var found = Parse(line);
            if (found.Date is not null) return found with { Line = line };
            if (found.Ambiguous) ambiguous ??= found with { Line = line };
        }
        return ambiguous ?? new DateDetection(null, null, false);
    }

    public static DateDetection Parse(string s)
    {
        if (Iso().Match(s) is { Success: true } iso
            && TryDate(Int(iso, "y"), Int(iso, "m"), Int(iso, "d")) is { } isoDate)
            return new DateDetection(isoDate, s, false);

        foreach (var pattern in new[] { MonthFirst(), DayFirst() })
        {
            if (pattern.Match(s) is { Success: true } named
                && TryDate(Int(named, "y"), MonthIndex(named.Groups["mon"].Value), Int(named, "d")) is { } namedDate)
                return new DateDetection(namedDate, s, false);
        }

        if (Numeric().Match(s) is { Success: true } numeric)
        {
            int a = Int(numeric, "a"), b = Int(numeric, "b"), y = Int(numeric, "y");
            if (a > 12 && TryDate(y, b, a) is { } dayFirst) return new DateDetection(dayFirst, s, false);
            if (b > 12 && TryDate(y, a, b) is { } monthFirst) return new DateDetection(monthFirst, s, false);
            if (a == b && TryDate(y, a, b) is { } same) return new DateDetection(same, s, false);
            return new DateDetection(null, s, true);
        }

        return new DateDetection(null, null, false);
    }

    private static int Int(Match m, string group) => int.Parse(m.Groups[group].Value, System.Globalization.CultureInfo.InvariantCulture);

    private static int MonthIndex(string name) => Array.IndexOf(Months, name[..3].ToLowerInvariant()) + 1;

    private static DateOnly? TryDate(int year, int month, int day) =>
        year is >= 1990 and <= 2100 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month)
            ? new DateOnly(year, month, day)
            : null;

    [GeneratedRegex(@"^(?:(?:order|purchase|transaction|invoice|receipt|billing|payment)\s+)?date\b|^(?:ordered|purchased|placed|paid)\s+on\b", RegexOptions.IgnoreCase)]
    private static partial Regex DateLabel();

    [GeneratedRegex(@"\b(?<y>\d{4})[-/.](?<m>\d{1,2})[-/.](?<d>\d{1,2})\b")]
    private static partial Regex Iso();

    [GeneratedRegex(@"\b(?<mon>jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?\s+(?<d>\d{1,2})(?:st|nd|rd|th)?,?\s+(?<y>\d{4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex MonthFirst();

    [GeneratedRegex(@"\b(?<d>\d{1,2})(?:st|nd|rd|th)?\s+(?<mon>jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?,?\s+(?<y>\d{4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex DayFirst();

    [GeneratedRegex(@"\b(?<a>\d{1,2})[/.](?<b>\d{1,2})[/.](?<y>\d{4})\b")]
    private static partial Regex Numeric();
}

public static partial class OrderNumbers
{
    public static (string Value, string Line)? Find(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            foreach (Match m in Pattern().Matches(line))
            {
                var value = m.Groups["id"].Value.TrimEnd('-');
                if (value.Any(char.IsDigit)) return (value, line);
            }
        }
        return null;
    }

    [GeneratedRegex(@"\b(?:order|invoice|receipt|confirmation|transaction)[ \t]*(?:number|num\.?|no\.?|#|id)?[ \t]*[:#]?[ \t]*(?<id>[A-Z0-9][A-Z0-9-]{3,})", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();
}
