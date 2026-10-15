using Sift.Core.Crypto;

namespace Sift.Tests.Support;

/// <summary>Stands in for DPAPI in tests. Two instances with different secrets can't read each other's keys.</summary>
public sealed class FakeKeyProtector(byte secret = 0x5A) : IKeyProtector
{
    public byte[] Protect(byte[] key) => [secret, .. key.Select(b => (byte)(b ^ secret))];

    public byte[] Unprotect(byte[] protectedKey) =>
        protectedKey.Length > 0 && protectedKey[0] == secret
            ? protectedKey[1..].Select(b => (byte)(b ^ secret)).ToArray()
            : throw new SiftCryptoException("Wrong key protector.");
}
