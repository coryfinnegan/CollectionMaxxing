using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Pairing;
using CollectionMaxxing.Api.Tests.Support;
using WebClient = CollectionMaxxing.Api.Tests.Auth.WebClient;

namespace CollectionMaxxing.Api.Tests.Security;

/// <summary>
/// The sign-in surface a hostile tailnet member would probe (OWASP API2): this server has NO authorization-code flow. The web signs in with a
/// passkey and a cookie, the phone pairs with a one-time token, and /connect/token speaks only those two grants. So there is no authorize
/// endpoint, no <c>state</c>/<c>nonce</c>/PKCE/<c>redirect_uri</c> to get wrong, and nothing that can redirect after sign-in. These tests
/// pin that: if someone adds the flow later they must add its abuse tests, because these will fail.
/// </summary>
public class TokenServerSurfaceTests(ApiFixture api) : PairingTest(api)
{
    private static readonly string[] RedirectUriVariants =
    [
        "https://pc.tailnet.ts.net/callback",
        "https://pc.tailnet.ts.net",
        "https://pc.tailnet.ts.net.evil.test/callback",
        "https://pc.tailnet.ts.net/callback/../../evil",
        "https://pc.tailnet.ts.net/callback/extra",
        "https://pc.tailnet.ts.net@evil.test/callback",
        "https://evil.test/callback",
        "http://pc.tailnet.ts.net/callback",
        "javascript:alert(1)",
        "cmphone://callback",
        "//evil.test",
        "/callback",
        "",
    ];

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task There_is_no_authorize_endpoint_for_any_redirect_uri_state_nonce_or_pkce_challenge(string method)
    {
        await SetUpOwnerAsync();
        var failures = new List<string>();
        foreach (var redirect in RedirectUriVariants)
        {
            var query = $"response_type=code&client_id=cm&redirect_uri={Uri.EscapeDataString(redirect)}&state=abc&nonce=xyz&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM&code_challenge_method=S256&scope=api";
            using var request = new HttpRequestMessage(new HttpMethod(method), method == "GET" ? $"/connect/authorize?{query}" : "/connect/authorize");
            if (method == "POST")
            {
                request.Content = new StringContent(query, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");
            }

            using var response = await Phone.SendAsync(request);
            if ((int)response.StatusCode is < 400 or >= 500 || response.Headers.Location is not null)
            {
                failures.Add($"{method} redirect_uri='{redirect}': {(int)response.StatusCode} Location={response.Headers.Location}");
            }
        }

        failures.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/connect/userinfo")]
    [InlineData("/connect/introspect")]
    [InlineData("/connect/device")]
    [InlineData("/connect/logout")]
    [InlineData("/connect/verification")]
    [InlineData("/connect/authorize")]
    [InlineData("/connect/par")]
    public async Task The_other_openid_connect_endpoints_do_not_exist(string path)
    {
        await SetUpOwnerAsync();

        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post })
        {
            using var response = await Phone.SendAsync(new HttpRequestMessage(method, path));
            ((int)response.StatusCode).ShouldBeInRange(400, 499, $"{method} {path}");
            response.StatusCode.ShouldNotBe(HttpStatusCode.OK);
            response.Headers.Location.ShouldBeNull();
        }
    }

    [Fact]
    public async Task The_discovery_document_if_there_is_one_advertises_only_what_exists()
    {
        await SetUpOwnerAsync();

        using var response = await Phone.GetAsync("/.well-known/openid-configuration");

        if (response.StatusCode == HttpStatusCode.OK)
        {
            var document = await ReadAsync(response);
            document.TryGetProperty("authorization_endpoint", out _).ShouldBeFalse("there is no authorization endpoint");
            document.TryGetProperty("userinfo_endpoint", out _).ShouldBeFalse();
            document.TryGetProperty("device_authorization_endpoint", out _).ShouldBeFalse();
            document.TryGetProperty("introspection_endpoint", out _).ShouldBeFalse();
            foreach (var grant in document.GetProperty("grant_types_supported").EnumerateArray().Select(g => g.GetString()))
            {
                grant.ShouldBeOneOf("urn:cm:device_pairing", "refresh_token");
            }
        }
        else
        {
            ((int)response.StatusCode).ShouldBeInRange(400, 499);
        }
    }

    [Fact]
    public async Task The_published_signing_keys_are_public_keys_only()
    {
        // OpenIddict serves the discovery document and the key set from middleware, so they are not in the routing table the endpoint audit reads.
        // They are anonymous on purpose (a client needs the public key); what must never be in them is anything private.
        await SetUpOwnerAsync();

        using var response = await Phone.GetAsync("/.well-known/jwks");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var keys = (await ReadAsync(response)).GetProperty("keys").EnumerateArray().ToList();
        keys.ShouldNotBeEmpty();
        foreach (var key in keys)
        {
            key.GetProperty("use").GetString().ShouldBe("sig", "the encryption key is private to the server and is never published");
            foreach (var secret in new[] { "d", "p", "q", "dp", "dq", "qi", "k", "oth" })
            {
                key.TryGetProperty(secret, out _).ShouldBeFalse($"the key set must not carry '{secret}'");
            }
        }
    }

    public static TheoryData<string> OtherGrants =>
    [
        "authorization_code",
        "client_credentials",
        "password",
        "implicit",
        "urn:ietf:params:oauth:grant-type:device_code",
        "urn:ietf:params:oauth:grant-type:token-exchange",
        "urn:ietf:params:oauth:grant-type:jwt-bearer",
        "urn:cm:device_pairing ",
        "URN:CM:DEVICE_PAIRING",
        "",
    ];

    [Theory]
    [MemberData(nameof(OtherGrants))]
    public async Task Every_grant_but_pairing_and_refresh_is_refused_whatever_the_code_verifier_and_redirect_uri(string grant)
    {
        var (browser, _) = await SetUpOwnerAsync();
        var offer = await OfferAsync(browser);
        var failures = new List<string>();

        foreach (var redirect in RedirectUriVariants)
        {
            using var response = await FormAsync(
                Phone,
                ("grant_type", grant),
                ("code", "a-code-that-was-never-issued"),
                ("code_verifier", "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"),
                ("redirect_uri", redirect),
                ("client_id", "cm"),
                ("client_secret", "secret"),
                ("username", "owner"),
                ("password", "password"),
                ("device_code", "x"),
                ("pairing_token", offer.Token),
                ("pairing_code", "000000"));
            var error = await OAuthErrorAsync(response);
            if (response.StatusCode != HttpStatusCode.BadRequest || response.Headers.Location is not null
                || error is not ("unsupported_grant_type" or "invalid_request" or "invalid_grant" or "invalid_client"))
            {
                failures.Add($"grant='{grant}' redirect_uri='{redirect}': {(int)response.StatusCode} {error}");
            }
        }

        failures.ShouldBeEmpty();

        // None of that spent the real pairing: the owner's own phone can still use it.
        (await ReadDeviceAsync(await RedeemTokenAsync(offer.Token))).AccessToken.ShouldNotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("/connect/token", "grant_type=refresh_token%00")]
    [InlineData("/connect/token", "grant_type=urn%3Acm%3Adevice_pairing&pairing_code=12%003456")]
    [InlineData("/connect/revoke", "token=abc%00def")]
    public async Task A_form_with_an_illegal_character_is_a_400_and_not_a_server_error(string path, string form)
    {
        await SetUpOwnerAsync();
        using var content = new StringContent(form, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");

        using var response = await Phone.PostAsync(path, content);

        await ShouldBeProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_code_for_the_pairing_grant_is_not_an_authorization_code_and_a_made_up_code_never_issues_tokens()
    {
        var (browser, _) = await SetUpOwnerAsync();
        await OfferAsync(browser);

        foreach (var code in new[] { "123456", "000000", "authorization-code", string.Empty })
        {
            using var response = await FormAsync(Phone, ("grant_type", "authorization_code"), ("code", code), ("code_verifier", code), ("redirect_uri", "https://pc.tailnet.ts.net/callback"));
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await ReadAsync(response)).TryGetProperty("access_token", out _).ShouldBeFalse();
        }
    }

    [Theory]
    [InlineData("/account/login")]
    [InlineData("/account/passkey/register")]
    [InlineData("/account/passkey/request-options")]
    [InlineData("/account/passkey/creation-options")]
    [InlineData("/account/recover")]
    [InlineData("/account/logout")]
    [InlineData("/setup")]
    public async Task No_sign_in_step_ever_redirects_whatever_return_address_it_is_given(string path)
    {
        var (browser, _) = await SetUpOwnerAsync();
        var failures = new List<string>();
        foreach (var query in new[]
        {
            "?returnUrl=https://evil.test/", "?ReturnUrl=//evil.test", "?next=https%3A%2F%2Fevil.test", "?redirect_uri=https://evil.test&state=x",
            "?redirect=//evil.test", "?continue=https://evil.test", "?url=https://evil.test", "?invite=x&returnUrl=https://evil.test",
        })
        {
            foreach (var method in new[] { HttpMethod.Post, HttpMethod.Get })
            {
                var response = await browser.SendAsync(method, path + query, method == HttpMethod.Post ? new { } : null);
                var body = await response.Content.ReadAsStringAsync();
                if ((int)response.StatusCode is >= 300 and < 400 || response.Headers.Location is not null || body.Contains("evil.test", StringComparison.Ordinal))
                {
                    failures.Add($"{method} {path}{query}: {(int)response.StatusCode} Location={response.Headers.Location}");
                }
            }
        }

        failures.ShouldBeEmpty();
    }

    [Fact]
    public async Task Signing_in_and_out_for_real_with_return_addresses_attached_answers_with_json_and_no_redirect()
    {
        var (browser, owner) = await SetUpOwnerAsync();
        var anon = App.NewBrowser();
        anon.Authenticator = browser.Authenticator;

        var options = await anon.PostAsync("/account/passkey/request-options?returnUrl=https://evil.test/");
        options.StatusCode.ShouldBe(HttpStatusCode.OK);
        var login = await anon.PostAsync("/account/login?returnUrl=https://evil.test/&redirect_uri=https://evil.test", new { credential = JsonDocument.Parse("{}").RootElement });
        login.Headers.Location.ShouldBeNull();
        ((int)login.StatusCode).ShouldBeInRange(400, 499);

        var real = await anon.LoginAsync();
        real.StatusCode.ShouldBe(HttpStatusCode.OK);
        real.Headers.Location.ShouldBeNull();
        real.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        (await ReadAsync(real)).GetProperty("userId").GetGuid().ShouldBe(owner.UserId);

        var logout = await anon.PostAsync("/account/logout?returnUrl=https://evil.test/");
        logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        logout.Headers.Location.ShouldBeNull();
    }

    [Fact]
    public async Task Pairing_returns_tokens_as_json_and_never_a_redirect()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var offer = await OfferAsync(browser);

        using var response = await RedeemTokenAsync(offer.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.Location.ShouldBeNull();
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        response.Headers.CacheControl!.NoStore.ShouldBeTrue("tokens must never be cached");
    }
}
