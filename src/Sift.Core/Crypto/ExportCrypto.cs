using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Sift.Core.Model;

namespace Sift.Core.Crypto;

/// <summary>
/// Portable, passphrase-protected export files, readable on any machine with the passphrase.
/// <code>
/// "SIFT" | version (1) | KDF id (1) | iterations (4, big-endian) | salt (16) | nonce (12) | tag (16) | ciphertext
/// </code>
/// The key is PBKDF2-SHA256(passphrase, salt). Everything before the tag is authenticated, so the
/// version and KDF parameters can't be altered without detection. A forgotten passphrase is unrecoverable.
/// </summary>
public static class ExportCrypto
{
    public const byte Version = 1;
    public const byte KdfPbkdf2Sha256 = 1;
    public const int DefaultIterations = 600_000;
    public const int MinIterations = 100_000;
    public const int MaxIterations = 10_000_000;   // bounds the work a hostile file can demand
    public const int SaltSize = 16;
    public const int MinPassphraseLength = 12;
    public const string FileExtension = ".sift";

    private static ReadOnlySpan<byte> Magic => "SIFT"u8;
    private const int HeaderLength = 4 + 1 + 1 + 4 + SaltSize;
    private const string FailureMessage = "The export could not be decrypted: the passphrase is wrong or the file has been modified.";

    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, string passphrase, int iterations = DefaultIterations)
    {
        if (passphrase.Length < MinPassphraseLength)
            throw new ArgumentException($"The passphrase must be at least {MinPassphraseLength} characters.", nameof(passphrase));
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, MinIterations);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(iterations, MaxIterations);

        Span<byte> header = stackalloc byte[HeaderLength];
        Magic.CopyTo(header);
        header[4] = Version;
        header[5] = KdfPbkdf2Sha256;
        BinaryPrimitives.WriteInt32BigEndian(header[6..], iterations);
        var salt = header[10..];
        RandomNumberGenerator.Fill(salt);

        var key = DeriveKey(passphrase, salt, iterations);
        try
        {
            return AeadBox.Seal(key, header, plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static byte[] Decrypt(ReadOnlySpan<byte> data, string passphrase)
    {
        if (data.Length < HeaderLength || !data[..4].SequenceEqual(Magic))
            throw new SiftCryptoException("This is not a Sift export file.");
        if (data[4] != Version)
            throw new SiftCryptoException($"Unsupported export version {data[4]}.");
        if (data[5] != KdfPbkdf2Sha256)
            throw new SiftCryptoException($"Unsupported key derivation method {data[5]}.");

        var iterations = BinaryPrimitives.ReadInt32BigEndian(data[6..]);
        if (iterations is < MinIterations or > MaxIterations)
            throw new SiftCryptoException("The export file has invalid key derivation parameters.");

        var key = DeriveKey(passphrase, data[10..HeaderLength], iterations);
        try
        {
            return AeadBox.Open(key, data, HeaderLength, FailureMessage);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] DeriveKey(string passphrase, ReadOnlySpan<byte> salt, int iterations)
    {
        var passphraseBytes = Encoding.UTF8.GetBytes(passphrase);
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(passphraseBytes, salt, iterations, HashAlgorithmName.SHA256, AeadBox.KeySize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passphraseBytes);
        }
    }
}

public static class ReceiptExporter
{
    public static byte[] Export(Receipt receipt, string passphrase, int iterations = ExportCrypto.DefaultIterations)
    {
        var json = ReceiptJson.Serialize(receipt);
        try
        {
            return ExportCrypto.Encrypt(json, passphrase, iterations);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }

    public static Receipt Import(ReadOnlySpan<byte> data, string passphrase)
    {
        var json = ExportCrypto.Decrypt(data, passphrase);
        try
        {
            return ReceiptJson.Deserialize(json);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }

    /// <summary>
    /// Export names carry no receipt details (merchant, amount), since file names sit outside the ciphertext.
    /// </summary>
    public static string FileName(DateTimeOffset exportedAt) =>
        $"sift-receipt-{exportedAt.UtcDateTime:yyyyMMdd-HHmmss}{ExportCrypto.FileExtension}";
}
