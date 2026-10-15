using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Sift.Core.Ingestion;

/// <summary>
/// Converts an HTML email body to normalized text. Table rows become lines and cells become
/// tab-separated, so line-based extraction works the same for HTML and plain-text receipts.
/// </summary>
public static partial class HtmlToText
{
    private static readonly HashSet<string> Skipped =
        ["head", "title", "script", "style", "noscript", "template", "svg", "img", "picture", "iframe", "object", "embed", "video", "audio"];

    private static readonly HashSet<string> Blocks =
    [
        "p", "div", "tr", "li", "ul", "ol", "table", "thead", "tbody", "tfoot", "h1", "h2", "h3", "h4", "h5", "h6",
        "section", "article", "header", "footer", "main", "aside", "nav", "blockquote", "pre", "hr", "dl", "dt", "dd",
        "center", "address", "form", "fieldset",
    ];

    public static string Convert(string html)
    {
        // A bare HtmlParser only builds a DOM. There is no browsing context and no loader, so
        // remote images (tracking pixels), stylesheets and scripts are never fetched.
        var parser = new HtmlParser();
        using var document = parser.ParseDocument(html);

        var sb = new StringBuilder();
        Walk((INode?)document.Body ?? document, sb);
        return TextNormalizer.Normalize(sb.ToString());
    }

    private static void Walk(INode node, StringBuilder sb)
    {
        switch (node)
        {
            case IText text:
                sb.Append(Whitespace().Replace(text.Data, " "));
                return;

            case IElement element:
                var tag = element.LocalName;
                if (Skipped.Contains(tag) || IsHidden(element)) return;
                if (tag == "br")
                {
                    sb.Append('\n');
                    return;
                }

                var isBlock = Blocks.Contains(tag);
                if (isBlock) sb.Append('\n');
                foreach (var child in element.ChildNodes) Walk(child, sb);
                if (tag is "td" or "th") sb.Append('\t');
                if (isBlock) sb.Append('\n');
                return;

            default:
                foreach (var child in node.ChildNodes) Walk(child, sb);
                return;
        }
    }

    /// <summary>Hidden "preheader" text in marketing-style emails is not part of the receipt.</summary>
    private static bool IsHidden(IElement element)
    {
        if (element.HasAttribute("hidden")) return true;
        if (string.Equals(element.GetAttribute("aria-hidden"), "true", StringComparison.OrdinalIgnoreCase)) return true;

        var style = element.GetAttribute("style");
        if (style is null) return false;
        var compact = Whitespace().Replace(style, "").ToLowerInvariant();
        return compact.Contains("display:none")
            || compact.Contains("visibility:hidden")
            || HiddenSize().IsMatch(compact);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"(?:^|;)(?:max-height|font-size):0(?:px|em|rem|pt|%)?(?:;|!|$)")]
    private static partial Regex HiddenSize();
}
