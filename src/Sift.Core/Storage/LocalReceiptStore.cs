using System.Security.Cryptography;
using System.Text.Json;
using Sift.Core.Crypto;
using Sift.Core.Model;

namespace Sift.Core.Storage;

public interface IImportIndex
{
    /// <returns>The id of an already-saved receipt from the same email, or null.</returns>
    string? FindDuplicate(ImportId importId);
}

public sealed record StoredReceiptSummary(
    string Id,
    string? Merchant,
    DateTimeOffset? PurchasedAt,
    decimal? Total,
    string? Currency,
    DateTimeOffset SavedAt,
    ImportId ImportId);

/// <summary>
/// Encrypted receipts at rest under one directory:
/// <code>
/// store.key              random 256-bit data key, wrapped by an <see cref="IKeyProtector"/> (DPAPI)
/// index.sift             encrypted list of saved receipts and their import ids
/// receipts/{guid}.sift   one encrypted receipt per file
/// </code>
/// Each receipt file authenticates its own id in the header, so files can't be swapped or renamed undetected.
/// </summary>
public sealed class LocalReceiptStore : IImportIndex, IDisposable
{
    private const byte Version = 1;
    private static readonly byte[] ReceiptMagic = "SFTR"u8.ToArray();
    private static readonly byte[] IndexMagic = "SFTI"u8.ToArray();
    private const string Tampered = "A stored file failed its integrity check and may have been modified.";

    private readonly string _root;
    private readonly string _receiptsDir;
    private readonly string _indexPath;
    private readonly byte[] _key;
    private readonly Lock _lock = new();

    public LocalReceiptStore(string rootDirectory, IKeyProtector protector)
    {
        _root = Path.GetFullPath(rootDirectory);
        _receiptsDir = Path.Combine(_root, "receipts");
        _indexPath = Path.Combine(_root, "index.sift");
        Directory.CreateDirectory(_receiptsDir);
        _key = LoadOrCreateKey(Path.Combine(_root, "store.key"), protector);
    }

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sift");

    public string Save(Receipt receipt)
    {
        var id = Guid.NewGuid();
        lock (_lock)
        {
            WriteAtomic(PathFor(id), AeadBox.Seal(_key, ReceiptHeader(id), ReceiptJson.Serialize(receipt)));

            var index = ReadIndex();
            index.Add(new StoredReceiptSummary(
                id.ToString("N"), receipt.Merchant?.Value, receipt.PurchasedAt?.Value, receipt.Total?.Value,
                receipt.Currency?.Value, DateTimeOffset.UtcNow, receipt.ImportId));
            WriteIndex(index);
        }
        return id.ToString("N");
    }

    public Receipt? Load(string id)
    {
        if (!Guid.TryParseExact(id, "N", out var guid)) return null;   // also rules out path traversal
        var path = PathFor(guid);
        lock (_lock)
        {
            if (!File.Exists(path)) return null;
            var plaintext = AeadBox.Open(_key, File.ReadAllBytes(path), ReceiptHeader(guid), Tampered);
            try
            {
                return ReceiptJson.Deserialize(plaintext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    public IReadOnlyList<StoredReceiptSummary> List()
    {
        lock (_lock) return ReadIndex().OrderByDescending(s => s.SavedAt).ToList();
    }

    public bool Delete(string id)
    {
        if (!Guid.TryParseExact(id, "N", out var guid)) return false;
        lock (_lock)
        {
            var index = ReadIndex();
            var removed = index.RemoveAll(s => s.Id == id) > 0;
            if (removed) WriteIndex(index);
            File.Delete(PathFor(guid));
            return removed;
        }
    }

    public string? FindDuplicate(ImportId importId)
    {
        lock (_lock)
        {
            var index = ReadIndex();
            var match = importId.MessageIdHash is { } messageId
                ? index.FirstOrDefault(s => s.ImportId.MessageIdHash == messageId)
                : null;
            return (match ?? index.FirstOrDefault(s => s.ImportId.ContentHash == importId.ContentHash))?.Id;
        }
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(_key);

    private string PathFor(Guid id) => Path.Combine(_receiptsDir, id.ToString("N") + ExportCrypto.FileExtension);

    private static byte[] ReceiptHeader(Guid id) => [.. ReceiptMagic, Version, .. id.ToByteArray()];

    private static byte[] IndexHeader => [.. IndexMagic, Version];

    private List<StoredReceiptSummary> ReadIndex()
    {
        if (!File.Exists(_indexPath)) return [];
        var plaintext = AeadBox.Open(_key, File.ReadAllBytes(_indexPath), IndexHeader, Tampered);
        try
        {
            return JsonSerializer.Deserialize<List<StoredReceiptSummary>>(plaintext, ReceiptJson.Options) ?? [];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private void WriteIndex(List<StoredReceiptSummary> index) =>
        WriteAtomic(_indexPath, AeadBox.Seal(_key, IndexHeader, JsonSerializer.SerializeToUtf8Bytes(index, ReceiptJson.Options)));

    private static void WriteAtomic(string path, byte[] content)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    private static byte[] LoadOrCreateKey(string keyPath, IKeyProtector protector)
    {
        if (File.Exists(keyPath)) return protector.Unprotect(File.ReadAllBytes(keyPath));

        var key = RandomNumberGenerator.GetBytes(AeadBox.KeySize);
        WriteAtomic(keyPath, protector.Protect(key));
        return key;
    }
}
