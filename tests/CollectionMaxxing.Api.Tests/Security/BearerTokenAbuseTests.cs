using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using CollectionMaxxing.Api.Tests.Invites;
using CollectionMaxxing.Api.Tests.Pairing;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.TestSupport;
using WebClient = CollectionMaxxing.Api.Tests.Auth.WebClient;

namespace CollectionMaxxing.Api.Tests.Security;

/// <summary>
/// What a hostile tailnet member does with bearer tokens (docs/07 layers 1 and 3, OWASP API2): expired, wrong issuer, wrong audience, no scope,
/// <c>alg: none</c>, someone else's key, a revoked device's token, a removed member's token. Each is tried against one route from every route
/// group, and each is refused with 401. The "control" token is a real one that passes through the same forging machinery untouched, so a 401
/// can only come from the one thing that was changed.
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
        new("GET", "/api/v1/me/sessions", null, 200),
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

    private async Task<(WebClient Owner, Guid LibraryId, List<Route> Routes, PairedDevice Phone, TokenForge Forge)> ArrangeAsync()
    {
        var (owner, account) = await SetUpOwnerAsync();
        var libraryId = account.LibraryId!.Value;
        var library = new TestLibrary(libraryId, account.UserId, account.UserId, account.UserId, account.UserId);
        var sessionId = await TestData.CreateScanSessionAsync(Db, library, account.UserId);
        var phone = await PairAsync(owner);
        return (owner, libraryId, RoutesFor(libraryId, sessionId), phone, new TokenForge(App.SecretsPath));
    }

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
    public async Task A_real_token_passed_through_the_forgery_machinery_untouched_is_accepted_on_every_route_group()
    {
        var (_, _, routes, phone, forge) = await ArrangeAsync();
        using var _ = forge;
        var payload = await forge.OpenAsync(phone.AccessToken);
        var minted = forge.Mint(payload);

        foreach (var route in routes)
        {
            using var response = await CallAsync(route, minted);
            route.Control.ShouldContain((int)response.StatusCode, $"{route} with a re-minted but unchanged token");
        }
    }

    public static TheoryData<string> Forgeries =>
    [
        "another issuer",
        "another audience",
        "no audience",
        "missing the api scope",
        "no scope claim at all",
        "signed by another key",
        "signed and sealed with other keys",
        "alg none, in the clear",
        "alg none, sealed with the server's key",
        "HS256 keyed with the public key",
        "a token id the database has never seen",
        "an expired token with an expiry claim extended by the forger",
        "tampered ciphertext",
        "truncated",
        "random text",
        "empty",
    ];

    [Theory]
    [MemberData(nameof(Forgeries))]
    public async Task A_forged_or_wrong_token_is_401_on_every_route_group(string forgery)
    {
        var (_, _, routes, phone, forge) = await ArrangeAsync();
        using var _ = forge;
        var payload = await forge.OpenAsync(phone.AccessToken);

        JsonObject Changed(Action<JsonObject> change)
        {
            var copy = (JsonObject)payload.DeepClone();
            change(copy);
            return copy;
        }

        var token = forgery switch
        {
            "another issuer" => forge.Mint(Changed(p => p["iss"] = "https://evil.test/")),
            "another audience" => forge.Mint(Changed(p => p["aud"] = "someone-else")),
            "no audience" => forge.Mint(Changed(p => p.Remove("aud"))),
            "missing the api scope" => forge.Mint(Changed(p => p["scope"] = "offline_access")),
            "no scope claim at all" => forge.Mint(Changed(p => p.Remove("scope"))),
            "signed by another key" => forge.MintWithOtherSigningKey(payload),
            "signed and sealed with other keys" => forge.MintWithEverythingOther(payload),
            "alg none, in the clear" => TokenForge.MintUnsigned(payload),
            "alg none, sealed with the server's key" => forge.MintUnsignedButEncrypted(payload),
            "HS256 keyed with the public key" => forge.MintHmacWithPublicKey(payload),
            "a token id the database has never seen" => forge.Mint(Changed(p => p["oi_tkn_id"] = Guid.NewGuid().ToString())),
            "an expired token with an expiry claim extended by the forger" => await ExpiredButExtendedAsync(forge, payload),
            "tampered ciphertext" => Tamper(phone.AccessToken),
            "truncated" => phone.AccessToken[..(phone.AccessToken.Length / 2)],
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

    private async Task<string> ExpiredButExtendedAsync(TokenForge forge, JsonObject payload)
    {
        App.Time.Advance(TimeSpan.FromMinutes(11));
        var extended = (JsonObject)payload.DeepClone();
        extended["exp"] = App.Time.GetUtcNow().AddYears(1).ToUnixTimeSeconds();
        return await Task.FromResult(forge.Mint(extended));
    }

    private static string Tamper(string token)
    {
        var parts = token.Split('.');
        var ciphertext = parts[3].ToCharArray();
        ciphertext[ciphertext.Length / 2] = ciphertext[ciphertext.Length / 2] == 'A' ? 'B' : 'A';
        parts[3] = new string(ciphertext);
        return string.Join('.', parts);
    }

    [Fact]
    public async Task A_token_that_has_run_out_is_401_on_every_route_group_and_the_refresh_token_still_gets_a_new_one()
    {
        var (_, _, routes, phone, forge) = await ArrangeAsync();
        using var _ = forge;
        App.Time.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(2));

        await ShouldBeRefusedEverywhereAsync(routes, phone.AccessToken, "expired");

        var renewed = await ReadDeviceAsync(await RefreshAsync(phone.RefreshToken));
        (await CallAsync(routes[0], renewed.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_revoked_devices_token_is_401_on_every_route_group_at_once()
    {
        var (owner, _, routes, phone, forge) = await ArrangeAsync();
        using var _ = forge;
        var id = (await SessionsAsync(owner)).Single().GetProperty("id").GetGuid();
        (await owner.SendAsync(HttpMethod.Delete, $"/api/v1/me/sessions/{id}", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await ShouldBeRefusedEverywhereAsync(routes, phone.AccessToken, "revoked device");

        // Re-signing the same claims with the server's own keys does not bring it back: the database says the device is revoked.
        await ShouldBeRefusedEverywhereAsync(routes, forge.Mint(await forge.OpenAsync(phone.AccessToken)), "revoked device, token re-minted");
    }

    [Fact]
    public async Task A_removed_members_token_is_401_on_every_route_group_at_once()
    {
        var (owner, libraryId, _, _, forge) = await ArrangeAsync();
        using var _ = forge;
        var (member, memberAccount) = await AddMemberAsync(owner, libraryId, "editor");
        var memberPhone = await PairAsync(member, "Member phone", App.NewBrowser().Http);
        var memberLibrary = new TestLibrary(libraryId, memberAccount.UserId, memberAccount.UserId, memberAccount.UserId, memberAccount.UserId);
        var routes = RoutesFor(libraryId, await TestData.CreateScanSessionAsync(Db, memberLibrary, memberAccount.UserId));
        (await CallAsync(routes[0], memberPhone.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await owner.SendAsync(HttpMethod.Delete, $"{MembersUrl(libraryId)}/{memberAccount.UserId}", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await ShouldBeRefusedEverywhereAsync(routes, memberPhone.AccessToken, "removed member");
        await ShouldBeRefusedEverywhereAsync(routes, forge.Mint(await forge.OpenAsync(memberPhone.AccessToken)), "removed member, token re-minted");
    }

    [Fact]
    public async Task A_valid_token_is_honoured_only_in_the_authorization_header()
    {
        var (_, _, routes, phone, forge) = await ArrangeAsync();
        using var _ = forge;
        var me = routes[0];
        var asQuery = new Route("GET", $"{me.Url}?access_token={phone.AccessToken}", null, 401);
        var asCookie = me;

        (await CallAsync(asQuery, null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await CallAsync(asCookie, null, r => r.Headers.TryAddWithoutValidation("Cookie", $"access_token={phone.AccessToken}; __Host-cm={phone.AccessToken}"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await CallAsync(asCookie, null, r => r.Headers.TryAddWithoutValidation("X-Access-Token", phone.AccessToken))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await CallAsync(asCookie, null, r => r.Headers.TryAddWithoutValidation("Authorization", $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("a:" + phone.AccessToken))}"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await CallAsync(asCookie, null, r => r.Headers.TryAddWithoutValidation("Authorization", phone.AccessToken))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await CallAsync(asCookie, phone.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_refresh_token_cannot_be_used_as_an_access_token()
    {
        var (_, _, routes, phone, forge) = await ArrangeAsync();
        using var _ = forge;

        await ShouldBeRefusedEverywhereAsync(routes, phone.RefreshToken, "refresh token as bearer");
    }

    [Fact]
    public async Task A_good_bearer_next_to_a_bad_session_cookie_is_judged_by_the_bearer_alone_and_a_bad_bearer_is_not_rescued_by_a_good_cookie()
    {
        var (owner, _, routes, phone, forge) = await ArrangeAsync();
        using var _ = forge;
        var me = routes[0];

        (await CallAsync(me, phone.AccessToken, r => r.Headers.TryAddWithoutValidation("Cookie", "__Host-cm=garbage"))).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The browser is signed in, but this request says it is a bearer call, and the bearer is wrong.
        var response = await owner.SendAsync(HttpMethod.Get, me.Url, null, tweak: r => r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token"));
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
