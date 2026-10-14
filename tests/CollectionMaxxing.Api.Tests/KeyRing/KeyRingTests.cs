using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Auth;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Accounts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using WebClient = CollectionMaxxing.Api.Tests.Auth.WebClient;

namespace CollectionMaxxing.Api.Tests.KeyRing;

// LOCKED acceptance tests of ID-2b (issue #63): the Data Protection key ring is persisted to a configured path, so a restart or
// a redeploy does not sign everybody out or break a passkey ceremony in flight. Do not edit, weaken, skip or delete: add new tests instead.
public class KeyRingTests(ApiFixture api) : ApiTest(api)
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cm-keyring-" + Guid.NewGuid().ToString("N"));

    private string KeyPath => Path.Combine(_root, "keys");

    public override async ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_cookie_issued_before_a_restart_is_still_accepted_after_it()
    {
        string sessionCookie;
        await using (var before = NewHost(KeyPath))
        {
            var browser = before.NewBrowser();
            await browser.SetUpOwnerAsync(await before.IssueCodeAsync(SetupCodePurpose.Setup));
            (await browser.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
            sessionCookie = CookieHeader(browser, WebClient.SessionCookie);
        }

        await using var after = NewHost(KeyPath);
        var restarted = after.NewBrowser();
        var response = await restarted.SendAsync(HttpMethod.Get, "/api/v1/me", null, tweak: r => r.Headers.Add("Cookie", sessionCookie));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_passkey_ceremony_started_before_a_restart_can_still_complete_after_it()
    {
        string ceremonyCookie;
        string options;
        SoftwareAuthenticator authenticator;
        await using (var before = NewHost(KeyPath))
        {
            var owner = before.NewBrowser();
            await owner.SetUpOwnerAsync(await before.IssueCodeAsync(SetupCodePurpose.Setup));
            authenticator = owner.Authenticator;

            var visitor = before.NewBrowser();
            var started = await visitor.PostAsync("/account/passkey/request-options");
            started.StatusCode.ShouldBe(HttpStatusCode.OK);
            options = (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("options").GetRawText();
            ceremonyCookie = CookieHeader(visitor, CeremonyCookieName);
        }

        await using var after = NewHost(KeyPath);
        var restarted = after.NewBrowser();
        var credential = JsonDocument.Parse(authenticator.Sign(options)).RootElement.Clone();
        var finished = await restarted.SendAsync(
            HttpMethod.Post, "/account/login", new { credential }, tweak: r => r.Headers.Add("Cookie", ceremonyCookie));

        finished.StatusCode.ShouldBe(HttpStatusCode.OK, await finished.Content.ReadAsStringAsync());
        restarted.HasSessionCookie.ShouldBeTrue();
    }

    [Fact]
    public async Task The_keys_are_written_under_the_configured_path()
    {
        await using var host = NewHost(KeyPath);
        var browser = host.NewBrowser();
        await browser.SetUpOwnerAsync(await host.IssueCodeAsync(SetupCodePurpose.Setup));

        Directory.Exists(KeyPath).ShouldBeTrue();
        Directory.GetFiles(KeyPath, "key-*.xml").ShouldNotBeEmpty();
    }

    [Fact]
    public async Task A_different_key_ring_does_not_accept_the_cookie()
    {
        string sessionCookie;
        await using (var before = NewHost(KeyPath))
        {
            var browser = before.NewBrowser();
            await browser.SetUpOwnerAsync(await before.IssueCodeAsync(SetupCodePurpose.Setup));
            sessionCookie = CookieHeader(browser, WebClient.SessionCookie);
        }

        await using var elsewhere = NewHost(Path.Combine(_root, "other-keys"));
        var response = await elsewhere.NewBrowser().SendAsync(HttpMethod.Get, "/api/v1/me", null, tweak: r => r.Headers.Add("Cookie", sessionCookie));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public void Starting_in_production_with_no_key_path_fails_loudly()
    {
        using var factory = ProductionFactory(keyPath: null);

        var failure = Should.Throw<Exception>(() => factory.CreateClient());

        failure.ToString().ShouldContain("Auth:KeyRing:Path");
    }

    [Fact]
    public void Starting_in_production_with_a_blank_key_path_fails_loudly()
    {
        using var factory = ProductionFactory(keyPath: "  ");

        var failure = Should.Throw<Exception>(() => factory.CreateClient());

        failure.ToString().ShouldContain("Auth:KeyRing:Path");
    }

    [Fact]
    public void Starting_in_production_with_a_key_path_works()
    {
        using var factory = ProductionFactory(KeyPath);

        using var client = factory.CreateClient();

        Directory.Exists(KeyPath).ShouldBeTrue();
    }

    private const string CeremonyCookieName = "__Host-cm-ceremony";

    private AuthApp NewHost(string keyPath) => new(
        Db.ConnectionString,
        new Dictionary<string, string?> { ["Auth:SecretsPath"] = Path.Combine(_root, "secrets"), ["Auth:KeyRing:Path"] = keyPath });

    private WebApplicationFactory<Program> ProductionFactory(string? keyPath) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["ConnectionStrings:collectionmaxxing"] = Db.ConnectionString,
                    ["Auth:SecretsPath"] = Path.Combine(_root, "secrets"),
                    ["Auth:Setup:IssueOnStartup"] = "false",
                };
                if (keyPath is not null)
                {
                    values["Auth:KeyRing:Path"] = keyPath;
                }

                configuration.AddInMemoryCollection(values);
            });
        });

    /// <summary>The newest value of the cookie called <paramref name="name"/> that the server set, as a <c>Cookie</c> request header.</summary>
    private static string CookieHeader(WebClient browser, string name) =>
        $"{name}={browser.SetCookies.Last(c => c.Name.Value == name && !string.IsNullOrEmpty(c.Value.Value)).Value.Value}";
}
