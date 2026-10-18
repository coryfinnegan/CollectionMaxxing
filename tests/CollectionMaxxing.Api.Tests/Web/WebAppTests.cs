using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CollectionMaxxing.Api.Security;
using CollectionMaxxing.Api.Tests.Hardening;
using CollectionMaxxing.Api.Tests.Support;

namespace CollectionMaxxing.Api.Tests.Web;

public sealed partial class WebAppTests : IDisposable
{
    private const string Shell = "<!doctype html><html><head><script>globalThis.__EXPO_ROUTER_HYDRATE__=true;</script></head><body>shell</body></html>";

    private readonly string _webRoot = Path.Combine(Path.GetTempPath(), "cm-web-" + Guid.NewGuid().ToString("N"));
    private readonly ProductionHost _host;

    public WebAppTests()
    {
        Write("index.html", Shell);
        Write("library.html", "<html><body>library</body></html>");
        Write("item/[id]/index.html", "<html><body>item</body></html>");
        Write("item/[id]/edit.html", "<html><body>item edit</body></html>");
        Write("_expo/static/js/web/entry-abc123.js", "console.log('app');");
        Write("favicon.ico", "icon");
        _host = new ProductionHost(webRoot: _webRoot);
    }

    public void Dispose()
    {
        _host.Dispose();
        Directory.Delete(_webRoot, recursive: true);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/library")]
    [InlineData("/item/some-id")]
    [InlineData("/item/some-id/edit")]
    [InlineData("/nothing-like-this")]
    public async Task A_page_of_the_app_is_served_with_the_security_headers(string path)
    {
        using var client = _host.Client();

        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        response.Headers.GetValues("Content-Security-Policy").Single().ShouldBe(SecurityHeaders.ContentSecurityPolicy);
        response.Headers.GetValues("X-Content-Type-Options").Single().ShouldBe("nosniff");
        response.Headers.GetValues("Strict-Transport-Security").ShouldNotBeEmpty();
        response.Headers.CacheControl!.NoCache.ShouldBeTrue();
        body.ShouldContain("<html");
    }

    [Fact]
    public async Task Each_route_gets_its_own_exported_page_and_an_unknown_one_gets_the_shell()
    {
        using var client = _host.Client();

        (await client.GetStringAsync("/library", TestContext.Current.CancellationToken)).ShouldContain("library");
        (await client.GetStringAsync("/item/42", TestContext.Current.CancellationToken)).ShouldContain(">item<");
        (await client.GetStringAsync("/item/42/edit", TestContext.Current.CancellationToken)).ShouldContain("item edit");
        (await client.GetStringAsync("/somewhere/else", TestContext.Current.CancellationToken)).ShouldContain("shell");
    }

    [Fact]
    public async Task Hashed_assets_are_served_with_a_long_lived_cache_header_and_the_security_headers()
    {
        using var client = _host.Client();

        var response = await client.GetAsync("/_expo/static/js/web/entry-abc123.js", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/javascript");
        response.Headers.CacheControl!.MaxAge.ShouldBe(TimeSpan.FromDays(365));
        response.Headers.CacheControl.ToString().ShouldContain("immutable");
        response.Headers.GetValues("X-Content-Type-Options").Single().ShouldBe("nosniff");
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("console.log('app');");
    }

    [Theory]
    [InlineData("/_expo/static/js/web/missing.js")]
    [InlineData("/missing.png")]
    [InlineData("/library/missing.css")]
    public async Task A_missing_file_is_a_404_never_the_html_shell(string path)
    {
        using var client = _host.Client();

        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldNotContain("shell");
    }

    [Theory]
    [InlineData("/api/v1/not-a-route")]
    [InlineData("/api")]
    [InlineData("/connect/not-a-route")]
    [InlineData("/account/not-a-route")]
    [InlineData("/media/not-a-route")]
    [InlineData("/health/not-a-route")]
    [InlineData("/scan-sessions/not-a-route")]
    public async Task The_fallback_never_covers_an_api_path(string path)
    {
        using var client = _host.Client();

        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.ShouldBeFalse(); // 404, or 401 where the API asks for sign-in first, as it does today
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldNotContain("shell");
    }

    [Theory]
    [InlineData("/.well-known/openid-configuration")]
    [InlineData("/.well-known/jwks")]
    public async Task The_openid_discovery_endpoints_are_still_openiddicts_json_with_the_web_app_installed(string path)
    {
        using var client = _host.Client();

        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldNotContain("shell");
    }
    [Fact]
    public async Task Real_endpoints_are_not_shadowed_by_the_web_app()
    {
        using var client = _host.Client();

        var live = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        var api = await client.GetAsync("/api/v1/libraries", TestContext.Current.CancellationToken);
        var login = await client.GetAsync("/account/login", TestContext.Current.CancellationToken);
        var join = await client.GetAsync("/join/some-token", TestContext.Current.CancellationToken);

        live.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await live.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldNotContain("shell");
        api.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        join.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Only_a_read_is_answered_by_the_web_app()
    {
        using var client = _host.Client();

        var post = await client.PostAsync("/library", new StringContent("x"), TestContext.Current.CancellationToken);

        post.IsSuccessStatusCode.ShouldBeFalse();
    }

    [Fact]
    public async Task A_path_cannot_climb_out_of_the_web_root()
    {
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_webRoot)!, "cm-secret.txt"), "secret");
        using var client = _host.Client();

        foreach (var path in new[] { "/..%2fcm-secret.txt", "/%2e%2e/cm-secret.txt", "/..\\cm-secret.txt" })
        {
            var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
            (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldNotContain("secret");
        }

        File.Delete(Path.Combine(Path.GetDirectoryName(_webRoot)!, "cm-secret.txt"));
    }

    [Fact]
    public async Task Every_inline_script_in_the_served_index_is_allowed_by_the_policy()
    {
        using var client = _host.Client();

        var served = await client.GetStringAsync("/", TestContext.Current.CancellationToken);

        InlineScriptsAreAllowed(served);
    }

    [Fact]
    public void Every_inline_script_in_the_real_web_export_is_allowed_by_the_policy_when_it_has_been_built()
    {
        // The Playwright run (and the release build) leaves the real export in app/dist; the hash in the policy must match what Expo really emits.
        var dist = RepositoryFiles.Find("app", "dist");
        if (!Directory.Exists(dist))
        {
            return;
        }

        foreach (var page in Directory.EnumerateFiles(dist, "*.html", SearchOption.AllDirectories))
        {
            InlineScriptsAreAllowed(File.ReadAllText(page));
        }
    }

    [Fact]
    public async Task With_no_web_export_installed_the_api_behaves_as_before()
    {
        using var bare = new ProductionHost(webRoot: Path.Combine(_webRoot, "nowhere"));
        using var client = bare.Client();

        var root = await client.GetAsync("/", TestContext.Current.CancellationToken);
        var login = await client.GetStringAsync("/account/login", TestContext.Current.CancellationToken);

        root.IsSuccessStatusCode.ShouldBeFalse();
        login.ShouldContain("hasn't been installed");
    }

    private static void InlineScriptsAreAllowed(string html)
    {
        var scripts = InlineScript().Matches(html).Select(m => m.Groups[1].Value).ToList();
        foreach (var script in scripts)
        {
            var hash = "'sha256-" + Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(script))) + "'";
            SecurityHeaders.ContentSecurityPolicy.Contains(hash, StringComparison.Ordinal).ShouldBeTrue("an inline script is not covered by the Content-Security-Policy: " + script);
        }
    }

    [GeneratedRegex(@"<script(?![^>]*\bsrc=)[^>]*>(.*?)</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex InlineScript();

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_webRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
