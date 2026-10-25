using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.TestSupport;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using WireMock;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Types;
using WireMock.Util;

namespace CollectionMaxxing.Api.Tests.Auth;

/// <summary>An Auth0 identity a test signs in as. <c>sub</c> is what Auth0 would send, for example <c>google-oauth2|123</c>.</summary>
public sealed record TestIdentity(string Subject, string? Email = null, string? Name = null)
{
    public static TestIdentity Google(string? name = null) =>
        new("google-oauth2|" + Guid.NewGuid().ToString("N"), $"{name ?? "someone"}@example.com", name ?? "Someone");

    public static TestIdentity Apple(string? name = null) =>
        new("apple|" + Guid.NewGuid().ToString("N"), null, name);
}

/// <summary>
/// A fake Auth0 tenant on loopback (WireMock): the discovery document, the JWKS, <c>/oauth/token</c> and <c>/userinfo</c>. Tokens are signed with the
/// test key from TestSupport. The issuer is <c>https://tenant.auth0.test/</c>, as the API is configured to expect; only the metadata
/// address points at WireMock.
/// </summary>
public sealed class FakeAuth0 : IDisposable
{
    public const string Domain = "tenant.auth0.test";
    public const string Issuer = "https://" + Domain + "/";
    public const string Audience = "urn:collectionmaxxing:api";
    public const string WebClientId = "web-client-id";
    public const string WebClientSecret = "web-client-secret-for-tests";
    public const string PublicOrigin = "https://localhost";

    private readonly WireMockServer _server = LoopbackWireMock.Start();
    private readonly JsonWebTokenHandler _tokens = new() { SetDefaultTimesOnTokenCreation = false };
    private readonly ConcurrentDictionary<string, TestIdentity> _profiles = new();
    private readonly ConcurrentDictionary<string, int> _userInfoCalls = new();

    public FakeAuth0()
    {
        var discovery = new Dictionary<string, object>
        {
            ["issuer"] = Issuer,
            ["authorization_endpoint"] = Issuer + "authorize",
            ["token_endpoint"] = Url("/oauth/token"),
            ["jwks_uri"] = Url("/.well-known/jwks.json"),
            ["end_session_endpoint"] = Issuer + "oidc/logout",
            ["userinfo_endpoint"] = Url("/userinfo"),
            ["response_types_supported"] = new[] { "code" },
            ["subject_types_supported"] = new[] { "public" },
            ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
            ["code_challenge_methods_supported"] = new[] { "S256" },
        };
        _server.Given(Request.Create().WithPath("/.well-known/openid-configuration").UsingGet())
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody(JsonSerializer.Serialize(discovery)));

        var key = TestSigningKey.Rsa.ExportParameters(false);
        var jwks = new { keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid = TestSigningKey.KeyId, n = Base64UrlEncoder.Encode(key.Modulus), e = Base64UrlEncoder.Encode(key.Exponent) } } };
        _server.Given(Request.Create().WithPath("/.well-known/jwks.json").UsingGet())
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody(JsonSerializer.Serialize(jwks)));

        // /userinfo answers for the access token it is given: the profile set with SetProfile, or just the sub. No token, no answer.
        _server.Given(Request.Create().WithPath("/userinfo").UsingGet()).RespondWith(Response.Create().WithCallback(UserInfo));
    }

    /// <summary>When true, <c>/userinfo</c> answers 503 (Auth0 can't be reached).</summary>
    public bool UserInfoDown { get; set; }

    /// <summary>What Auth0's <c>/userinfo</c> says about <paramref name="who"/> (its name and email).</summary>
    public void SetProfile(TestIdentity who) => _profiles[who.Subject] = who;

    /// <summary>How many times <c>/userinfo</c> was asked about <paramref name="subject"/>.</summary>
    public int UserInfoCalls(string subject) => _userInfoCalls.GetValueOrDefault(subject);

    private ResponseMessage UserInfo(IRequestMessage request)
    {
        var header = request.Headers is { } headers && headers.TryGetValue("Authorization", out var values) ? values.FirstOrDefault() ?? string.Empty : string.Empty;
        var subject = header.StartsWith("Bearer ", StringComparison.Ordinal) && _tokens.CanReadToken(header[7..])
            ? _tokens.ReadJsonWebToken(header[7..]).Subject
            : null;
        if (subject is null || UserInfoDown)
        {
            return new ResponseMessage { StatusCode = subject is null ? 401 : 503 };
        }

        _userInfoCalls.AddOrUpdate(subject, 1, (_, count) => count + 1);
        var body = new Dictionary<string, object> { ["sub"] = subject };
        if (_profiles.TryGetValue(subject, out var profile))
        {
            if (profile.Name is not null)
            {
                body["name"] = profile.Name;
            }

            if (profile.Email is not null)
            {
                body["email"] = profile.Email;
            }
        }

        var message = new ResponseMessage
        {
            StatusCode = 200,
            BodyData = new BodyData { BodyAsString = JsonSerializer.Serialize(body), DetectedBodyType = BodyType.String },
        };
        message.AddHeader("Content-Type", "application/json");
        return message;
    }

    /// <summary>The API's settings for this tenant (the config contract of issue #226).</summary>
    public static Dictionary<string, string?> Settings => new()
    {
        ["Auth:Auth0:Domain"] = Domain,
        ["Auth:Auth0:Audience"] = Audience,
        ["Auth:Auth0:WebClientId"] = WebClientId,
        ["Auth:Auth0:WebClientSecret"] = WebClientSecret,
        ["Auth:PublicOrigin"] = PublicOrigin,
    };

    public static SigningCredentials Signing => new(new RsaSecurityKey(TestSigningKey.Rsa) { KeyId = TestSigningKey.KeyId }, SecurityAlgorithms.RsaSha256);

    /// <summary>Points the API's handlers at this tenant's metadata (plain http on loopback).</summary>
    public void Configure(IServiceCollection services)
    {
        var metadata = Url("/.well-known/openid-configuration");
        services.Configure<OpenIdConnectOptions>(Auth0Setup.OidcScheme, options =>
        {
            options.MetadataAddress = metadata;
            options.RequireHttpsMetadata = false;
        });
        services.Configure<JwtBearerOptions>(Auth0Setup.BearerScheme, options =>
        {
            options.MetadataAddress = metadata;
            options.RequireHttpsMetadata = false;
        });
    }

    /// <summary>
    /// What Auth0 answers when the API redeems <paramref name="code"/>: an ID token for <paramref name="who"/> with the challenge's
    /// <paramref name="nonce"/>. <paramref name="tweak"/> changes the ID token's claims (to play a bad token).
    /// </summary>
    public void ExpectCode(string code, string nonce, TestIdentity who, DateTimeOffset? authTime, Action<Dictionary<string, object>>? tweak = null)
    {
        var claims = Defaults(WebClientId);
        claims["sub"] = who.Subject;
        claims["nonce"] = nonce;
        if (who.Email is not null)
        {
            claims["email"] = who.Email;
        }

        if (who.Name is not null)
        {
            claims["name"] = who.Name;
        }

        if (authTime is { } at)
        {
            claims["auth_time"] = at.ToUnixTimeSeconds();
        }

        tweak?.Invoke(claims);
        var idToken = Sign(claims);
        var body = JsonSerializer.Serialize(new { access_token = AccessToken(who.Subject), id_token = idToken, token_type = "Bearer", expires_in = 3600 });
        _server.Given(Request.Create().WithPath("/oauth/token").UsingPost().WithBody(new RegexMatcher($"(^|&)code={code}(&|$)")))
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody(body));
    }

    /// <summary>A valid access token for the API, as the iPhone would hold it.</summary>
    public string AccessToken(string subject, Action<Dictionary<string, object>>? tweak = null)
    {
        var claims = Defaults(Audience);
        claims["sub"] = subject;
        claims["scope"] = "openid profile email offline_access";
        tweak?.Invoke(claims); // may change or remove anything, iss and exp included
        return Sign(claims);
    }

    /// <summary>Signs <paramref name="claims"/> for <paramref name="audience"/>; claims given (iss, exp…) win over the defaults.</summary>
    public string Mint(string audience, Dictionary<string, object> claims, SigningCredentials? signing = null)
    {
        var all = Defaults(audience);
        foreach (var (name, value) in claims)
        {
            all[name] = value;
        }

        return Sign(all, signing);
    }

    /// <summary>What every Auth0 token carries: issuer, audience, issued now, an hour to live.</summary>
    public static Dictionary<string, object> Defaults(string audience)
    {
        var now = DateTime.UtcNow;
        return new Dictionary<string, object>
        {
            ["iss"] = Issuer,
            ["aud"] = audience,
            ["iat"] = EpochTime.GetIntDate(now),
            ["nbf"] = EpochTime.GetIntDate(now),
            ["exp"] = EpochTime.GetIntDate(now.AddHours(1)),
        };
    }

    /// <summary>Signs exactly <paramref name="claims"/> (no times are added).</summary>
    public string Sign(Dictionary<string, object> claims, SigningCredentials? signing = null) =>
        _tokens.CreateToken(new SecurityTokenDescriptor
        {
            Claims = claims,
            Subject = new ClaimsIdentity(),
            SigningCredentials = signing ?? Signing,
        });

    private string Url(string path) => _server.Urls[0] + path;

    public void Dispose() => _server.Stop();
}
