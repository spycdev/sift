using System.Text;
using System.Text.RegularExpressions;

namespace Sift.Core.Ingestion;

/// <summary>
/// Produces the text shape every later stage relies on: '\n' between lines, '\t' between cells,
/// single spaces inside cells, no empty lines. Runs of 2+ spaces count as a cell break, which is
/// how plain-text receipts align their columns.
/// </summary>
public static partial class TextNormalizer
{
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var cells = CellBreak().Split(line)
                .Select(c => InlineSpace().Replace(c, " ").Trim())
                .Where(c => c.Length > 0);
            var joined = string.Join('\t', cells);
            if (joined.Length == 0) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(joined);
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"\t| {2,}|[  ]{2,}")]
    private static partial Regex CellBreak();

    [GeneratedRegex(@"[   \f\v]+")]
    private static partial Regex InlineSpace();
}
