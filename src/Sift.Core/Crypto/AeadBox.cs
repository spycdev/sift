using System.Security.Cryptography;

namespace Sift.Core.Crypto;

public sealed class SiftCryptoException(string message) : Exception(message);

/// <summary>
/// AES-256-GCM envelope: <c>header | nonce (12) | tag (16) | ciphertext</c>.
/// Everything before the tag (header and nonce) is authenticated as associated data, so a
/// modified header fails exactly like modified ciphertext.
/// </summary>
internal static class AeadBox
{
    public const int KeySize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;

    public static byte[] Seal(ReadOnlySpan<byte> key, ReadOnlySpan<byte> header, ReadOnlySpan<byte> plaintext)
    {
        var output = new byte[header.Length + NonceSize + TagSize + plaintext.Length];
        header.CopyTo(output);

        var nonce = output.AsSpan(header.Length, NonceSize);
        RandomNumberGenerator.Fill(nonce);   // fresh random nonce for every encryption
        var associatedData = output.AsSpan(0, header.Length + NonceSize);
        var tag = output.AsSpan(header.Length + NonceSize, TagSize);
        var ciphertext = output.AsSpan(header.Length + NonceSize + TagSize);

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        return output;
    }

    /// <summary>
    /// Opens data whose header must equal <paramref name="expectedHeader"/>. Use this when the reader already
    /// knows what the header should say (e.g. which record it asked for), so a validly sealed file
    /// belonging to something else is rejected.
    /// </summary>
    public static byte[] Open(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data, ReadOnlySpan<byte> expectedHeader, string failureMessage)
    {
        if (data.Length < expectedHeader.Length || !data[..expectedHeader.Length].SequenceEqual(expectedHeader))
            throw new SiftCryptoException(failureMessage);
        return Open(key, data, expectedHeader.Length, failureMessage);
    }

    public static byte[] Open(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data, int headerLength, string failureMessage)
    {
        if (data.Length < headerLength + NonceSize + TagSize) throw new SiftCryptoException("The file is truncated.");

        var associatedData = data[..(headerLength + NonceSize)];
        var nonce = data.Slice(headerLength, NonceSize);
        var tag = data.Slice(headerLength + NonceSize, TagSize);
        var ciphertext = data[(headerLength + NonceSize + TagSize)..];
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, TagSize);
        try
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
        }
        catch (AuthenticationTagMismatchException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new SiftCryptoException(failureMessage);
        }
        return plaintext;
    }
}
