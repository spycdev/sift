using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Sift.Core.Crypto;

/// <summary>Wraps the local store's data key so it isn't kept in the clear on disk.</summary>
public interface IKeyProtector
{
    byte[] Protect(byte[] key);
    byte[] Unprotect(byte[] protectedKey);
}

/// <summary>
/// DPAPI with <see cref="DataProtectionScope.CurrentUser"/>: only this Windows user account can
/// unwrap the key. It does not protect against malware running as that same user.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiKeyProtector : IKeyProtector
{
    private static readonly byte[] Entropy = "Sift.LocalStore.v1"u8.ToArray();

    public byte[] Protect(byte[] key) => ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] protectedKey)
    {
        try
        {
            return ProtectedData.Unprotect(protectedKey, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            throw new SiftCryptoException("The local store key can't be unlocked by this Windows user.");
        }
    }
}

public static class KeyProtectors
{
    public static IKeyProtector ForCurrentPlatform() => OperatingSystem.IsWindows()
        ? new DpapiKeyProtector()
        : throw new PlatformNotSupportedException("The local receipt store currently requires Windows (DPAPI).");
}
