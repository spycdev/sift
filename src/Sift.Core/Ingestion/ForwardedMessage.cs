using System.Globalization;
using System.Text.RegularExpressions;
using MimeKit;
using MimeKit.Utils;

namespace Sift.Core.Ingestion;

public sealed record InlineForward(string? FromName, string? FromAddress, string? Subject, DateTimeOffset? Date, string Body);

/// <summary>
/// Recognizes a forward pasted into the body ("---------- Forwarded message ---------" and similar),
/// recovers the original sender, subject and date from the quoted header block, and returns the
/// original body.
/// </summary>
public static partial class ForwardedMessage
{
    private static readonly string[] DateFormats =
    [
        "ddd, MMM d, yyyy h:mm tt", "ddd, MMM d, yyyy H:mm", "dddd, MMMM d, yyyy h:mm tt", "dddd, MMMM d, yyyy H:mm",
        "MMMM d, yyyy h:mm:ss tt", "MMMM d, yyyy h:mm tt", "MMM d, yyyy h:mm tt", "d MMMM yyyy H:mm", "d MMM yyyy H:mm",
    ];

    public static InlineForward? TryUnwrap(string normalizedText)
    {
        var lines = normalizedText.Split('\n');
        var marker = Array.FindIndex(lines, l => Marker().IsMatch(l));
        if (marker < 0) return null;

        string? fromName = null, fromAddress = null, subject = null;
        DateTimeOffset? date = null;

        var i = marker + 1;
        for (; i < lines.Length; i++)
        {
            var header = HeaderLine().Match(lines[i]);
            if (!header.Success) break;

            var value = header.Groups["value"].Value.Replace('\t', ' ').Trim();
            switch (header.Groups["name"].Value.ToLowerInvariant())
            {
                case "from" when MailboxAddress.TryParse(value, out var mailbox):
                    fromName = string.IsNullOrWhiteSpace(mailbox.Name) ? null : mailbox.Name;
                    fromAddress = mailbox.Address;
                    break;
                case "date" or "sent":
                    date = ParseDate(value);
                    break;
                case "subject":
                    subject = value.Length == 0 ? null : value;
                    break;
            }
        }

        return new InlineForward(fromName, fromAddress, subject, date, string.Join('\n', lines.Skip(i)));
    }

    private static DateTimeOffset? ParseDate(string value)
    {
        if (DateUtils.TryParse(value, out var rfc822)) return rfc822;

        var cleaned = value.Replace(" at ", " ");
        if (DateTimeOffset.TryParseExact(cleaned, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var exact))
            return exact;
        return DateTimeOffset.TryParse(cleaned, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var loose)
            ? loose
            : null;
    }

    [GeneratedRegex(@"^-{2,}\s*(?:Forwarded message|Original Message)\s*-{2,}$|^Begin forwarded message:$", RegexOptions.IgnoreCase)]
    private static partial Regex Marker();

    [GeneratedRegex(@"^(?<name>From|Date|Sent|Subject|To|Cc):\s*(?<value>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex HeaderLine();
}
