using MimeKit;

namespace Sift.Core.Ingestion;

public sealed record EmailParseResult(ParsedEmail? Email, string? Error)
{
    public bool Success => Email is not null;
    public static EmailParseResult Ok(ParsedEmail email) => new(email, null);
    public static EmailParseResult Fail(string error) => new(null, error);
}

/// <summary>Turns raw .eml bytes into a <see cref="ParsedEmail"/>. Never throws for bad input.</summary>
public sealed class EmailParser(int maxBytes = EmailParser.DefaultMaxBytes)
{
    public const int DefaultMaxBytes = 5 * 1024 * 1024;

    public int MaxBytes => maxBytes;

    public EmailParseResult Parse(RawEmail raw)
    {
        if (raw.Content.Length == 0) return EmailParseResult.Fail("The file is empty.");
        if (raw.Content.Length > maxBytes)
            return EmailParseResult.Fail($"The file is larger than the {maxBytes / (1024 * 1024)} MB limit.");

        MimeMessage outer;
        try
        {
            using var stream = new MemoryStream(raw.Content, writable: false);
            outer = MimeMessage.Load(stream);
        }
        catch (FormatException)
        {
            return EmailParseResult.Fail("The file is not a readable email message.");
        }

        // A receipt forwarded "as attachment" arrives as a message/rfc822 part; the receipt is the inner message.
        var attached = outer.BodyParts.OfType<MessagePart>().Select(p => p.Message).FirstOrDefault(m => m is not null);
        var message = attached ?? outer;
        var forwarded = attached is null ? ForwardKind.None : ForwardKind.Attached;

        var plain = NullIfEmpty(message.TextBody is { } t ? TextNormalizer.Normalize(t) : null);
        var html = NullIfEmpty(message.HtmlBody is { } h ? HtmlToText.Convert(h) : null);

        var sender = message.From.Mailboxes.FirstOrDefault();
        var fromName = NullIfEmpty(sender?.Name);
        var fromAddress = NullIfEmpty(sender?.Address);
        var subject = NullIfEmpty(message.Subject);
        DateTimeOffset? date = message.Date == DateTimeOffset.MinValue ? null : message.Date;

        if (forwarded == ForwardKind.None)
        {
            var plainForward = plain is null ? null : ForwardedMessage.TryUnwrap(plain);
            var htmlForward = html is null ? null : ForwardedMessage.TryUnwrap(html);
            if ((plainForward ?? htmlForward) is { } inline)
            {
                forwarded = ForwardKind.Inline;
                plain = plainForward is null ? plain : NullIfEmpty(plainForward.Body);
                html = htmlForward is null ? html : NullIfEmpty(htmlForward.Body);
                if (inline.FromAddress is not null)
                {
                    fromName = inline.FromName;
                    fromAddress = inline.FromAddress;
                }
                subject = inline.Subject ?? subject;
                date = inline.Date ?? date;
            }
        }

        if (fromAddress is null && subject is null && plain is null && html is null)
            return EmailParseResult.Fail("The file does not contain any email headers or body text.");

        var attachments = outer.Attachments
            .Concat(attached?.Attachments ?? [])
            .Select(a => new AttachmentInfo(
                (a as MimePart)?.FileName ?? a.ContentDisposition?.FileName,
                a.ContentType.MimeType))
            .ToList();

        return EmailParseResult.Ok(new ParsedEmail(
            fromName, fromAddress, subject, date, NullIfEmpty(message.MessageId),
            plain, html, attachments, forwarded));
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
