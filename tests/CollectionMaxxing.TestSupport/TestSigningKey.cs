using System.Security.Cryptography;

namespace CollectionMaxxing.TestSupport;

/// <summary>
/// The RSA key the fake Auth0 signs ID and access tokens with, made once per test run (never a real key, never on disk). Tests that
/// play an attacker use <see cref="Other"/>: a key of the right kind that Auth0 never published.
/// </summary>
/// <remarks>
/// The key is generated once and kept as plain parameters; every read of <see cref="Rsa"/> or <see cref="Other"/> hands out a new
/// <see cref="RSA"/> with the key already imported. One <see cref="RSA"/> instance shared by tests that run in parallel is not safe: a key
/// made with <c>RSA.Create(2048)</c> is only generated when first used, and many test classes starting at once all used it for the first time
/// together, so some failed with an ObjectDisposedException from the key handle.
/// </remarks>
public static class TestSigningKey
{
    private static readonly Lazy<RSAParameters> Key = new(NewKey);
    private static readonly Lazy<RSAParameters> OtherKey = new(NewKey);

    /// <summary>The key id the fake Auth0 publishes in its JWKS.</summary>
    public const string KeyId = "cm-test-key-1";

    /// <summary>The signing key's material (its private half included).</summary>
    public static RSAParameters Parameters => Key.Value;

    /// <summary>A new instance of the signing key, for the caller to use and keep.</summary>
    public static RSA Rsa => From(Key.Value);

    /// <summary>A new instance of a key nobody trusts, with the same key id: a forged signature.</summary>
    public static RSA Other => From(OtherKey.Value);

    private static RSAParameters NewKey()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportParameters(includePrivateParameters: true);
    }

    private static RSA From(RSAParameters parameters)
    {
        var rsa = RSA.Create();
        rsa.ImportParameters(parameters);
        return rsa;
    }
}
