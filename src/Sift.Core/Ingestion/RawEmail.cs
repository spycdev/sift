using System.Runtime.CompilerServices;

namespace Sift.Core.Ingestion;

/// <summary>The unparsed bytes of one email, as handed over by a source adapter.</summary>
public sealed record RawEmail(string SourceName, byte[] Content)
{
    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> + 1 bytes, so an oversized input is detected by the parser
    /// without ever buffering the whole thing.
    /// </summary>
    public static async Task<RawEmail> ReadAsync(Stream stream, string sourceName, int maxBytes, CancellationToken ct = default)
    {
        var limit = maxBytes + 1L;
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while (buffer.Length < limit && (read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, (int)Math.Min(read, limit - buffer.Length));
        }
        return new RawEmail(sourceName, buffer.ToArray());
    }
}

/// <summary>Where emails come from. Source adapters know nothing about receipts.</summary>
public interface IEmailSource
{
    IAsyncEnumerable<RawEmail> ReadAsync(CancellationToken ct = default);
}

/// <summary>Reads a single .eml file, or every .eml file in a directory.</summary>
public sealed class EmlFileSource(string path, int maxBytes = EmailParser.DefaultMaxBytes) : IEmailSource
{
    public async IAsyncEnumerable<RawEmail> ReadAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        IEnumerable<string> files = Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*.eml").Order(StringComparer.OrdinalIgnoreCase)
            : [path];

        foreach (var file in files)
        {
            await using var stream = File.OpenRead(file);
            yield return await RawEmail.ReadAsync(stream, Path.GetFileName(file), maxBytes, ct);
        }
    }
}
