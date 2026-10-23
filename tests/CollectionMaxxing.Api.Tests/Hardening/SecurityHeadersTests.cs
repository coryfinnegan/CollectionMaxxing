using System.Net;
using CollectionMaxxing.Api.Security;

namespace CollectionMaxxing.Api.Tests.Hardening;

public sealed class SecurityHeadersTests : IDisposable
{
    private readonly ProductionHost _host = new();

    public void Dispose() => _host.Dispose();

    public static TheoryData<string, string, bool, HttpStatusCode> Requests => new()
    {
        { "a page that answers 200 (the not-linked page)", "/account/not-linked", false, HttpStatusCode.OK },
        { "a 401 (not signed in)", "/api/v1/libraries", false, HttpStatusCode.Unauthorized },
        { "a 500 (the database is down)", "/api/v1/libraries", true, HttpStatusCode.InternalServerError },
        { "a web file (the app shell)", "/join/some-token", false, HttpStatusCode.OK },
    };

    [Theory]
    [MemberData(nameof(Requests))]
    public async Task Every_kind_of_response_carries_the_security_headers(string what, string path, bool signedIn, HttpStatusCode expected)
    {
        using var client = _host.Client(signedIn ? Guid.NewGuid() : null);

        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(expected, what);
        Header(response, "Strict-Transport-Security").ShouldBe("max-age=31536000");
        Header(response, "X-Content-Type-Options").ShouldBe("nosniff");
        Header(response, "Referrer-Policy").ShouldBe("strict-origin-when-cross-origin");
        Header(response, "Permissions-Policy").ShouldBe("camera=(self), geolocation=()");
        Header(response, "Cross-Origin-Opener-Policy").ShouldBe("same-origin");
        Header(response, "Content-Security-Policy").ShouldBe(SecurityHeaders.ContentSecurityPolicy, what);
    }

    [Fact]
    public void The_policy_is_the_one_in_the_security_doc_with_two_named_exceptions_and_no_unsafe_script()
    {
        // docs/08 "HTTP hardening", plus: styles may be inline (react-native-web writes style rules and per-element styles at run time), and
        // the one inline script Expo's web export puts in index.html (the router hydrate flag) is allowed by its hash, not by 'unsafe-inline'.
        SecurityHeaders.ContentSecurityPolicy.ShouldBe(
            "default-src 'self'; script-src 'self' 'sha256-67fhrP0+BkBqmgGGXTtgiVO/9EQs3QruYNU/7fnRkI8='; img-src 'self' blob: data:; connect-src 'self'; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'");
        var scripts = SecurityHeaders.ContentSecurityPolicy.Split("; ").Single(d => d.StartsWith("script-src", StringComparison.Ordinal));
        scripts.ShouldNotContain("unsafe-inline");
        scripts.ShouldNotContain("unsafe-eval");
        Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData("globalThis.__EXPO_ROUTER_HYDRATE__=true;"u8))
            .ShouldBe("67fhrP0+BkBqmgGGXTtgiVO/9EQs3QruYNU/7fnRkI8=");
    }

    [Fact]
    public void The_web_apps_e2e_server_sends_the_same_policy_so_the_specs_run_under_it()
    {
        var serve = File.ReadAllText(Support.RepositoryFiles.Find("app", "playwright", "serve.mjs"));

        serve.ShouldContain("'Content-Security-Policy': \"" + SecurityHeaders.ContentSecurityPolicy + "\"");
        serve.ShouldContain("'Permissions-Policy': '" + SecurityHeaders.PermissionsPolicy + "'");
    }

    [Fact]
    public async Task An_api_answer_is_never_cached()
    {
        using var failing = _host.Client(Guid.NewGuid());
        using var anonymous = _host.Client();

        var failure = await failing.GetAsync("/api/v1/libraries", TestContext.Current.CancellationToken);
        var refused = await anonymous.GetAsync("/api/v1/me", TestContext.Current.CancellationToken);

        Header(failure, "Cache-Control")!.ShouldContain("no-store");
        Header(refused, "Cache-Control")!.ShouldContain("no-store");
    }

    [Fact]
    public async Task An_unhandled_exception_is_problem_json_with_no_detail_in_production()
    {
        using var client = _host.Client(Guid.NewGuid());

        var response = await client.GetAsync("/api/v1/libraries", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        body.ShouldNotContain("Npgsql");
        body.ShouldNotContain("Exception");
        body.ShouldNotContain("   at ");
        body.ShouldNotContain("127.0.0.1");
    }

    [Theory]
    [InlineData("/openapi/v1.json")]
    [InlineData("/scalar")]
    [InlineData("/scalar/v1")]
    [InlineData("/swagger")]
    public async Task Production_serves_no_api_document_or_explorer(string path)
    {
        using var client = _host.Client();

        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldNotContain("\"openapi\"");
    }

    [Fact]
    public async Task A_cross_origin_preflight_gets_no_cors_headers()
    {
        using var client = _host.Client();
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/v1/libraries");
        preflight.Headers.Add("Origin", "https://evil.example");
        preflight.Headers.Add("Access-Control-Request-Method", "GET");

        var response = await client.SendAsync(preflight, TestContext.Current.CancellationToken);

        response.Headers.Concat(response.Content.Headers).Select(h => h.Key)
            .ShouldNotContain(name => name.StartsWith("Access-Control-Allow", StringComparison.OrdinalIgnoreCase));
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(", ", values) : null;
}
