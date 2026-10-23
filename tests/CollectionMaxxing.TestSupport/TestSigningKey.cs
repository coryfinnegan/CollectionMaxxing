using System.Security.Cryptography;

namespace CollectionMaxxing.TestSupport;

/// <summary>
/// The RSA key the fake Auth0 signs ID and access tokens with, made once per test run (never a real key, never on disk). Tests that
/// play an attacker use <see cref="Other"/>: a key of the right kind that Auth0 never published.
/// </summary>
public static class TestSigningKey
{
    private static readonly Lazy<RSA> Key = new(() => RSA.Create(2048));
    private static readonly Lazy<RSA> OtherKey = new(() => RSA.Create(2048));

    /// <summary>The key id the fake Auth0 publishes in its JWKS.</summary>
    public const string KeyId = "cm-test-key-1";

    public static RSA Rsa => Key.Value;

    /// <summary>A key nobody trusts, with the same key id: a forged signature.</summary>
    public static RSA Other => OtherKey.Value;
}
