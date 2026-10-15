namespace Sift.Core.Ingestion;

public enum ForwardKind { None, Attached, Inline }

public sealed record AttachmentInfo(string? FileName, string ContentType)
{
    public bool IsPdf =>
        ContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)
        || (FileName?.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ?? false);
}

/// <summary>
/// An email reduced to what extraction needs. Bodies are normalized text: one line per '\n',
/// table cells separated by '\t', no empty lines. For forwarded emails, sender, subject, date
/// and bodies are those of the original (inner) message.
/// </summary>
public sealed record ParsedEmail(
    string? FromName,
    string? FromAddress,
    string? Subject,
    DateTimeOffset? Date,
    string? MessageId,
    string? PlainText,
    string? HtmlText,
    IReadOnlyList<AttachmentInfo> Attachments,
    ForwardKind Forwarded)
{
    /// <summary>HTML-derived text when present (tables survive better), otherwise the plain-text part.</summary>
    public string PrimaryText => HtmlText ?? PlainText ?? string.Empty;

    public string? SenderDomain
    {
        get
        {
            var at = FromAddress?.LastIndexOf('@') ?? -1;
            return at < 0 ? null : FromAddress![(at + 1)..].ToLowerInvariant();
        }
    }
}
