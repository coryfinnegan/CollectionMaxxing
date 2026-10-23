using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Auth;
using CollectionMaxxing.Api.Tests.Invites;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.TestSupport;
using Microsoft.IdentityModel.Tokens;
using WebClient = CollectionMaxxing.Api.Tests.Auth.WebClient;

namespace CollectionMaxxing.Api.Tests.Security;

/// <summary>
/// What a hostile tailnet member does with bearer tokens (OWASP API2), now that Auth0 issues them (ADR-025): expired, wrong issuer, wrong
/// audience, <c>alg: none</c>, HS256, someone else's key, a removed member's token. Each is tried against one route from every route group,
/// and each is refused with 401. The "control" token is minted by the same machinery with nothing changed, so a 401 can only come from the
/// one thing that was changed.
/// </summary>
public class BearerTokenAbuseTests(ApiFixture api) : InviteTest(api)
{
    private sealed record Route(string Method, string Url, string? Body, params int[] Control)
    {
        public override string ToString() => $"{Method} {Url}";
    }

    /// <summary>One route from every group. <c>Control</c> is what a good token gets (the owner's phone: not a platform admin).</summary>
    private static List<Route> RoutesFor(Guid libraryId, Guid sessionId) =>
    [
        new("GET", "/api/v1/me", null, 200),
        new("GET", "/api/v1/me/sign-in-methods", null, 200),
        new("GET", $"/api/v1/me/export/{Guid.NewGuid()}", null, 404),
        new("GET", "/api/v1/libraries", null, 200),
        new("GET", $"/api/v1/libraries/{libraryId}/items", null, 200),
        new("GET", $"/api/v1/libraries/{libraryId}/members", null, 200),
        new("POST", $"/api/v1/libraries/{libraryId}/tags", """{ "name": "From a token" }""", 201),
        new("GET", $"/api/v1/scan-sessions/{sessionId}", null, 200),
        new("GET", "/api/v1/catalog/platforms", null, 200),
        new("GET", "/api/v1/recognition/config", null, 200),
        new("GET", $"/media/covers/{Guid.NewGuid()}", null, 404),
        new("GET", "/api/admin/jobs", null, 403),
    ];

    private async Task<(WebClient Owner, Guid LibraryId, List<Route> Routes, string Subject)> ArrangeAsync()
    {
        var (owner, account) = await SetUpOwnerAsync();
        var libraryId = account.LibraryId!.Value;
        var library = new TestLibrary(libraryId, account.UserId, account.UserId, account.UserId, account.UserId);
        var sessionId = await TestData.CreateScanSessionAsync(Db, library, account.UserId);
        return (owner, libraryId, RoutesFor(libraryId, sessionId), owner.Identity.Subject);
    }

    private HttpClient? _phone;

    private HttpClient Phone => _phone ??= App.NewBrowser().Http;

    private async Task<HttpResponseMessage> CallAsync(Route route, string? bearer, Action<HttpRequestMessage>? tweak = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(route.Method), route.Url);
        if (route.Body is not null)
        {
            request.Content = new StringContent(route.Body, Encoding.UTF8, "application/json");
        }

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        tweak?.Invoke(request);
        return await Phone.SendAsync(request);
    }

    private async Task ShouldBeRefusedEverywhereAsync(IEnumerable<Route> routes, string? bearer, string because, Action<HttpRequestMessage>? tweak = null)
    {
        var failures = new List<string>();
        foreach (var route in routes)
        {
            using var response = await CallAsync(route, bearer, tweak);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                failures.Add($"{route}: {(int)response.StatusCode}");
            }
        }

        failures.ShouldBeEmpty(because);
    }

    [Fact]
    public async Task A_real_token_minted_by_the_same_machinery_is_accepted_on_every_route_group()
    {
        var (_, _, routes, subject) = await ArrangeAsync();
        var minted = App.Auth0.AccessToken(subject);

        foreach (var route in routes)
        {
            using var response = await CallAsync(route, minted);
            route.Control.ShouldContain((int)response.StatusCode, $"{route} with an unchanged token");
        }
    }

    public static TheoryData<string> Forgeries =>
    [
        "another issuer",
        "no issuer",
        "another audience",
        "no audience",
        "the web app's ID token",
        "signed by another key",
        "alg none, in the clear",
        "HS256 keyed with the public key",
        "expired",
        "not valid yet",
        "no expiry",
        "tampered payload",
        "no sub",
        "truncated",
        "random text",
        "empty",
    ];

    [Theory]
    [MemberData(nameof(Forgeries))]
    public async Task A_forged_or_wrong_token_is_401_on_every_route_group(string forgery)
    {
        var (_, _, routes, subject) = await ArrangeAsync();
        var auth0 = App.Auth0;
        var good = auth0.AccessToken(subject);
        var now = DateTime.UtcNow;

        var token = forgery switch
        {
            "another issuer" => auth0.AccessToken(subject, c => c["iss"] = "https://evil.test/"),
            "no issuer" => auth0.AccessToken(subject, c => c.Remove("iss")),
            "another audience" => auth0.AccessToken(subject, c => c["aud"] = "someone-else"),
            "no audience" => auth0.AccessToken(subject, c => c.Remove("aud")),
            "the web app's ID token" => auth0.Mint(FakeAuth0.WebClientId, new() { ["sub"] = subject }),
            "signed by another key" => auth0.Mint(FakeAuth0.Audience, new() { ["sub"] = subject }, new SigningCredentials(new RsaSecurityKey(TestSigningKey.Other) { KeyId = TestSigningKey.KeyId }, SecurityAlgorithms.RsaSha256)),
            "alg none, in the clear" => Unsigned(subject),
            "HS256 keyed with the public key" => auth0.Mint(FakeAuth0.Audience, new() { ["sub"] = subject }, new SigningCredentials(new SymmetricSecurityKey(TestSigningKey.Rsa.ExportSubjectPublicKeyInfo()), SecurityAlgorithms.HmacSha256)),
            "expired" => auth0.AccessToken(subject, c =>
            {
                c["iat"] = EpochTime.GetIntDate(now.AddHours(-2));
                c["nbf"] = EpochTime.GetIntDate(now.AddHours(-2));
                c["exp"] = EpochTime.GetIntDate(now.AddMinutes(-5));
            }),
            "not valid yet" => auth0.AccessToken(subject, c =>
            {
                c["nbf"] = EpochTime.GetIntDate(now.AddHours(1));
                c["exp"] = EpochTime.GetIntDate(now.AddHours(2));
            }),
            "no expiry" => auth0.AccessToken(subject, c => c.Remove("exp")),
            "tampered payload" => Tamper(good),
            "truncated" => good[..(good.Length / 2)],
            "no sub" => auth0.AccessToken(subject, c => c.Remove("sub")), // signed, right issuer and audience, about nobody: 401, not 403 not_linked
            "random text" => "not-a-token",
            _ => string.Empty,
        };

        if (forgery == "empty")
        {
            await ShouldBeRefusedEverywhereAsync(routes, null, forgery, request => request.Headers.TryAddWithoutValidation("Authorization", "Bearer "));
        }
        else
        {
            await ShouldBeRefusedEverywhereAsync(routes, token, forgery);
        }
    }

    private static string Unsigned(string subject)
    {
        static string Part(object value) => Base64UrlEncoder.Encode(JsonSerializer.Serialize(value));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return Part(new { alg = "none", typ = "JWT" }) + "."
            + Part(new { iss = FakeAuth0.Issuer, aud = FakeAuth0.Audience, sub = subject, iat = now, exp = now + 3600 }) + ".";
    }

    /// <summary>Changes the payload (makes the token someone else's) and keeps the original signature.</summary>
    private static string Tamper(string token)
    {
        var parts = token.Split('.');
        var payload = Base64UrlEncoder.Decode(parts[1]).Replace("google-oauth2|", "google-oauth2|x", StringComparison.Ordinal);
        parts[1] = Base64UrlEncoder.Encode(payload);
        return string.Join('.', parts);
    }

    [Fact]
    public async Task A_removed_members_token_is_401_on_every_route_group_at_once()
    {
        var (owner, libraryId, _, _) = await ArrangeAsync();
        var (member, memberAccount) = await AddMemberAsync(owner, libraryId, "editor");
        var memberToken = App.Auth0.AccessToken(member.Identity.Subject);
        var memberLibrary = new TestLibrary(libraryId, memberAccount.UserId, memberAccount.UserId, memberAccount.UserId, memberAccount.UserId);
        var routes = RoutesFor(libraryId, await TestData.CreateScanSessionAsync(Db, memberLibrary, memberAccount.UserId));
        (await CallAsync(routes[0], memberToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await owner.SendAsync(HttpMethod.Delete, $"{MembersUrl(libraryId)}/{memberAccount.UserId}", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await ShouldBeRefusedEverywhereAsync(routes, memberToken, "removed member");
        await ShouldBeRefusedEverywhereAsync(routes, App.Auth0.AccessToken(member.Identity.Subject), "removed member, a fresh token");
    }

    [Fact]
    public async Task A_valid_token_is_honoured_only_in_the_authorization_header()
    {
        var (_, _, routes, subject) = await ArrangeAsync();
        var token = App.Auth0.AccessToken(subject);
        var me = routes[0];
        var asQuery = new Route("GET", $"{me.Url}?access_token={token}", null, 401);
        var asCookie = me;

        (await CallAsync(asQuery, null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await CallAsync(asCookie, null, r => r.Headers.TryAddWithoutValidation("Cookie", $"access_token={token}; __Host-cm={token}"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await CallAsync(asCookie, null, r => r.Headers.TryAddWithoutValidation("X-Access-Token", token))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await CallAsync(asCookie, null, r => r.Headers.TryAddWithoutValidation("Authorization", $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("a:" + token))}"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await CallAsync(asCookie, null, r => r.Headers.TryAddWithoutValidation("Authorization", token))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await CallAsync(asCookie, token)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_good_bearer_next_to_a_bad_session_cookie_is_judged_by_the_bearer_alone_and_a_bad_bearer_is_not_rescued_by_a_good_cookie()
    {
        var (owner, _, routes, subject) = await ArrangeAsync();
        var me = routes[0];

        (await CallAsync(me, App.Auth0.AccessToken(subject), r => r.Headers.TryAddWithoutValidation("Cookie", "__Host-cm=garbage"))).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The browser is signed in, but this request says it is a bearer call, and the bearer is wrong.
        var response = await owner.SendAsync(HttpMethod.Get, me.Url, null, tweak: r => r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token"));
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
