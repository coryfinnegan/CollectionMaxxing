using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CollectionMaxxing.Api.Tests.Security;

/// <summary>
/// Makes bearer tokens the way an attacker would try to: from a real token the test app issued, changing one thing at a time, and from nothing
/// at all. Where it uses the app's own signing and encryption keys (read from the test app's secrets folder) it is playing the strongest
/// attacker there is, one who can mint anything the server would accept cryptographically, so the only thing left to refuse the token is
/// the server's own rules (issuer, expiry, scope, and the token's entry in the database).
/// </summary>
public sealed class TokenForge : IDisposable
{
    private static readonly Dictionary<string, object> Header = new() { ["typ"] = "at+jwt" };
    private static readonly Dictionary<string, object> SealedHeader = new() { ["typ"] = "at+jwt", ["cty"] = "JWT" };

    private readonly X509Certificate2 _signing;
    private readonly X509Certificate2 _encryption;
    private readonly JsonWebTokenHandler _handler = new();

    public TokenForge(string secretsPath)
    {
        _signing = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(secretsPath, "openiddict-signing.pfx"), null);
        _encryption = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(secretsPath, "openiddict-encryption.pfx"), null);
    }

    private SigningCredentials Signs => new(new X509SecurityKey(_signing), SecurityAlgorithms.RsaSha256);

    private EncryptingCredentials Encrypts => new(new X509SecurityKey(_encryption), SecurityAlgorithms.RsaOAEP, SecurityAlgorithms.Aes256CbcHmacSha512);

    /// <summary>The claims of a real access token, as a JSON object you can change.</summary>
    public async Task<JsonObject> OpenAsync(string accessToken)
    {
        var result = await _handler.ValidateTokenAsync(accessToken, new TokenValidationParameters
        {
            TokenDecryptionKey = new X509SecurityKey(_encryption),
            IssuerSigningKey = new X509SecurityKey(_signing),
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = false,
            ValidTypes = ["at+jwt"],
        });
        result.IsValid.ShouldBeTrue(result.Exception?.ToString());
        var outer = (JsonWebToken)result.SecurityToken;
        var token = outer.InnerToken ?? outer;
        return JsonNode.Parse(Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(token.EncodedPayload)))!.AsObject();
    }

    private string Seal(string signedToken) => _handler.EncryptToken(signedToken, Encrypts, SealedHeader);

    private string Sign(JsonObject payload, SigningCredentials credentials) => _handler.CreateToken(payload.ToJsonString(), credentials, Header);

    /// <summary>Signs with the app's key and encrypts with the app's key: what the server itself would produce for these claims.</summary>
    public string Mint(JsonObject payload) => Seal(Sign(payload, Signs));

    /// <summary>Signed by a key the server has never heard of (encrypted with the app's own, the best case for the attacker).</summary>
    public string MintWithOtherSigningKey(JsonObject payload)
    {
        using var rsa = RSA.Create(2048);
        return Seal(Sign(payload, new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = "attacker" }, SecurityAlgorithms.RsaSha256)));
    }

    /// <summary>Signed and encrypted with keys the server has never heard of.</summary>
    public string MintWithEverythingOther(JsonObject payload)
    {
        using var signing = RSA.Create(2048);
        using var encryption = RSA.Create(2048);
        var signed = Sign(payload, new SigningCredentials(new RsaSecurityKey(signing), SecurityAlgorithms.RsaSha256));
        return _handler.EncryptToken(
            signed,
            new EncryptingCredentials(new RsaSecurityKey(encryption), SecurityAlgorithms.RsaOAEP, SecurityAlgorithms.Aes256CbcHmacSha512),
            SealedHeader);
    }

    /// <summary>A JWT whose header says <c>alg: none</c>: no signature at all, in the clear.</summary>
    public static string MintUnsigned(JsonObject payload) =>
        $"{Base64UrlEncoder.Encode("""{"alg":"none","typ":"at+jwt"}""")}.{Base64UrlEncoder.Encode(payload.ToJsonString())}.";

    /// <summary>An <c>alg: none</c> JWT sealed in the app's own encryption, so only the signature rule stands in the way.</summary>
    public string MintUnsignedButEncrypted(JsonObject payload) => Seal(MintUnsigned(payload));

    /// <summary>HS256 with the signing certificate's public bytes as the secret: the classic algorithm-confusion attack.</summary>
    public string MintHmacWithPublicKey(JsonObject payload) =>
        Seal(Sign(payload, new SigningCredentials(new SymmetricSecurityKey(_signing.GetPublicKey()), SecurityAlgorithms.HmacSha256)));

    public void Dispose()
    {
        _signing.Dispose();
        _encryption.Dispose();
    }
}
