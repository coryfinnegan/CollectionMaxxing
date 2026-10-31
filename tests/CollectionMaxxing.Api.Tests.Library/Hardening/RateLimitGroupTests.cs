using System.Net;
using System.Net.Http.Json;
using CollectionMaxxing.Api.Tests.RateLimiting;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Hardening;

/// <summary>
/// Every row of the "Rate limits" table in docs/08 that is not the code-guessing limiter (that one has its own tests in Auth/ and RateLimiting/):
/// at its limit the next request is a 429 with Retry-After, just under it the request goes through to whatever the endpoint does (never 429).
/// The limits are lowered with settings so a test needs a handful of requests, not hundreds; the table's real numbers are the defaults,
/// checked in <see cref="The_defaults_are_the_numbers_in_the_security_doc"/>.
/// </summary>
public sealed class RateLimitGroupTests(ApiFixture api) : ApiTest(api), IDisposable
{
    private static readonly Guid Session = Guid.NewGuid();

    private readonly List<WebApplicationFactory<Program>> _factories = [];

    void IDisposable.Dispose()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }
    }

    [Fact]
    public async Task Catalogue_search_allows_its_limit_and_then_answers_429_with_retry_after()
    {
        var host = Host(("Search", 3));
        using var client = await UserAsync(host);

        var statuses = await SendAsync(client, 4, () => new HttpRequestMessage(HttpMethod.Get, "/api/v1/catalog/search?q=zelda"));

        statuses.Take(3).ShouldAllBe(s => s != HttpStatusCode.TooManyRequests);
        statuses[3].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Theory]
    [InlineData("/api/v1/CATALOG/Search?q=zelda")]
    [InlineData("/api/v1/catalog/search/?q=zelda")]
    [InlineData("/API/V1/Catalog/Lookup/?code=123")]
    public async Task Catalogue_search_is_limited_whatever_the_case_or_trailing_slash_of_the_path(string url)
    {
        var host = Host(("Search", 3));
        using var client = await UserAsync(host);

        var statuses = await SendAsync(client, 4, () => new HttpRequestMessage(HttpMethod.Get, url));

        statuses.Take(3).ShouldAllBe(s => s != HttpStatusCode.TooManyRequests);
        statuses[3].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Theory]
    [InlineData("/API/v1/Scan-Sessions/{0}/BARCODES")]
    [InlineData("/api/v1/scan-sessions/{0}/barcodes/")]
    public async Task Barcode_batches_are_limited_whatever_the_case_or_trailing_slash_of_the_path(string template)
    {
        var host = Host(("Barcodes", 2));
        using var client = await UserAsync(host);
        var url = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, Session);

        var statuses = await SendAsync(client, 3, () => new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(new { }) });

        statuses.Take(2).ShouldAllBe(s => s != HttpStatusCode.TooManyRequests);
        statuses[2].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Theory]
    [InlineData("/API/v1/Scan-Sessions/{0}/IDENTIFY")]
    [InlineData("/api/v1/scan-sessions/{0}/identify/")]
    public async Task Identify_is_limited_whatever_the_case_or_trailing_slash_of_the_path(string template)
    {
        var host = Host(("Identify", 5));
        using var client = await UserAsync(host);

        var response = await client.PostAsJsonAsync(
            string.Format(System.Globalization.CultureInfo.InvariantCulture, template, Session),
            new { items = Enumerable.Range(0, 6).Select(_ => new { }).ToArray() },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Theory]
    [InlineData("/API/v1/Me/EXPORT")]
    [InlineData("/api/v1/me/export/")]
    public async Task Export_is_limited_whatever_the_case_or_trailing_slash_of_the_path(string url)
    {
        var host = Host(("Export", 1));
        using var client = await UserAsync(host);

        var statuses = await SendAsync(client, 2, () => new HttpRequestMessage(HttpMethod.Get, url));

        statuses[0].ShouldNotBe(HttpStatusCode.TooManyRequests);
        statuses[1].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task A_refusal_says_when_to_come_back_and_is_problem_json_with_the_security_headers()
    {
        var host = Host(("Search", 2));
        using var client = await UserAsync(host);
        await SendAsync(client, 2, () => new HttpRequestMessage(HttpMethod.Get, "/api/v1/catalog/search?q=a"));

        using var response = await client.GetAsync("/api/v1/catalog/search?q=a", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter.ShouldNotBeNull();
        response.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ShouldBeGreaterThan(0);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        response.Headers.Contains("Content-Security-Policy").ShouldBeTrue();
    }

    [Fact]
    public async Task Barcode_batches_are_limited_per_user()
    {
        var host = Host(("Barcodes", 2));
        using var client = await UserAsync(host);

        var statuses = await SendAsync(client, 3, () => new HttpRequestMessage(HttpMethod.Post, $"/api/v1/scan-sessions/{Session}/barcodes") { Content = JsonContent.Create(new { }) });

        statuses.Take(2).ShouldAllBe(s => s != HttpStatusCode.TooManyRequests);
        statuses[2].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Identify_is_limited_by_the_items_it_carries_not_by_the_calls()
    {
        var host = Host(("Identify", 5));
        using var client = await UserAsync(host);
        var url = $"/api/v1/scan-sessions/{Session}/identify";

        var first = await client.PostAsJsonAsync(url, new { items = new object[] { new { }, new { }, new { } } }, TestContext.Current.CancellationToken); // 3 of 5
        var second = await client.PostAsJsonAsync(url, new { items = new object[] { new { }, new { } } }, TestContext.Current.CancellationToken); // 5 of 5
        var third = await client.PostAsJsonAsync(url, new { items = new object[] { new { } } }, TestContext.Current.CancellationToken); // the 6th item

        first.StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
        second.StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
        third.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task An_identify_call_with_more_items_than_the_hour_allows_is_refused_outright()
    {
        var host = Host(("Identify", 5));
        using var client = await UserAsync(host);

        var response = await client.PostAsJsonAsync($"/api/v1/scan-sessions/{Session}/identify", new { items = Enumerable.Range(0, 6).Select(_ => new { }).ToArray() }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Export_may_be_started_once_per_window()
    {
        var host = Host(("Export", 1));
        using var client = await UserAsync(host);

        var statuses = await SendAsync(client, 2, () => new HttpRequestMessage(HttpMethod.Get, "/api/v1/me/export"));

        statuses[0].ShouldNotBe(HttpStatusCode.TooManyRequests);
        statuses[1].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Looking_at_an_export_already_started_does_not_use_up_the_export_limit()
    {
        var host = Host(("Export", 1));
        using var client = await UserAsync(host);

        var statuses = await SendAsync(client, 3, () => new HttpRequestMessage(HttpMethod.Get, $"/api/v1/me/export/{Guid.NewGuid()}"));

        statuses.ShouldAllBe(s => s != HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Everything_else_shares_one_bucket_per_user()
    {
        var host = Host(("Default", 4));
        using var client = await UserAsync(host);

        var statuses = await SendAsync(client, 5, () => new HttpRequestMessage(HttpMethod.Get, "/api/v1/libraries"));

        statuses.Take(4).ShouldAllBe(s => s == HttpStatusCode.OK);
        statuses[4].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task One_users_requests_never_use_up_another_users_bucket()
    {
        var host = Host(("Default", 4));
        var library = await CreateLibraryAsync();
        using var busy = UserClient(host, library.UserWithRole(LibraryRole.Owner));
        using var quiet = UserClient(host, library.UserWithRole(LibraryRole.Viewer));
        var busyStatuses = await SendAsync(busy, 6, () => new HttpRequestMessage(HttpMethod.Get, "/api/v1/libraries"));

        var statuses = await SendAsync(quiet, 4, () => new HttpRequestMessage(HttpMethod.Get, "/api/v1/libraries"));

        busyStatuses[^1].ShouldBe(HttpStatusCode.TooManyRequests);
        statuses.ShouldAllBe(s => s == HttpStatusCode.OK);
    }

    [Fact]
    public async Task Callers_who_are_not_signed_in_are_judged_by_address_not_all_together()
    {
        var host = Host(("Default", 3));
        using var one = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var two = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        one.DefaultRequestHeaders.Add(PeerFilter.Header, "198.51.100.1");
        two.DefaultRequestHeaders.Add(PeerFilter.Header, "198.51.100.2");

        var first = await SendAsync(one, 4, () => new HttpRequestMessage(HttpMethod.Get, "/account/login"));
        var second = await SendAsync(two, 3, () => new HttpRequestMessage(HttpMethod.Get, "/account/login"));

        first[3].ShouldBe(HttpStatusCode.TooManyRequests);
        second.ShouldAllBe(s => s == HttpStatusCode.Found); // off to Auth0
    }

    [Fact]
    public async Task The_code_endpoints_also_have_an_hourly_limit()
    {
        var host = Host(("AuthHourly", 2));
        using var client = host.CreateClient();

        var statuses = await SendAsync(client, 3, () => new HttpRequestMessage(HttpMethod.Post, "/account/recover") { Content = JsonContent.Create(new { code = "x" }) });

        statuses.Take(2).ShouldAllBe(s => s != HttpStatusCode.TooManyRequests);
        statuses[2].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Recovery_attempts_use_the_hourly_auth_limit_too()
    {
        // ONB-1 removed /setup; the recovery code is the code a stranger could still guess.
        var host = Host(("AuthHourly", 2));
        using var client = host.CreateClient();

        var statuses = await SendAsync(client, 3, () => new HttpRequestMessage(HttpMethod.Post, "/account/recover") { Content = JsonContent.Create(new { code = "x" }) });

        statuses.Take(2).ShouldAllBe(s => s != HttpStatusCode.TooManyRequests);
        statuses[2].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Signing_out_never_counts_against_the_hourly_auth_limit()
    {
        var host = Host(("AuthHourly", 2));
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-CSRF", "1");

        var statuses = await SendAsync(client, 5, () => new HttpRequestMessage(HttpMethod.Post, "/account/logout"));
        var recover = await SendAsync(client, 2, () => new HttpRequestMessage(HttpMethod.Post, "/account/recover") { Content = JsonContent.Create(new { code = "x" }) });

        statuses.ShouldAllBe(s => s == HttpStatusCode.OK);
        recover.ShouldAllBe(s => s != HttpStatusCode.TooManyRequests);
    }

    [Theory]
    [InlineData("Items")]
    [InlineData("ITEMS")]
    public async Task Identify_counts_the_items_whatever_the_case_of_the_property_name(string property)
    {
        var host = Host(("Identify", 5));
        using var client = await UserAsync(host);
        var body = $$"""{"{{property}}":[{},{},{},{},{},{}]}""";
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        var response = await client.PostAsync($"/api/v1/scan-sessions/{Session}/identify", content, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task A_busy_five_minute_shelf_pass_is_never_limited_and_a_refusal_never_asks_for_more_than_a_minute()
    {
        // 150 snaps of 10 items over five minutes is 300 items a minute at its busiest; the limit has to sit well above that.
        var host = Host();
        using var client = await UserAsync(host);
        var limits = new CollectionMaxxing.Api.Security.SecurityOptions.LimitOptions();
        var url = $"/api/v1/scan-sessions/{Session}/identify";

        var statuses = new List<HttpStatusCode>();
        for (var snap = 0; snap < 40; snap++) // 400 items in a burst: more than a real minute of it
        {
            using var response = await client.PostAsJsonAsync(url, new { items = Enumerable.Range(0, 10).Select(_ => new { }).ToArray() }, TestContext.Current.CancellationToken);
            statuses.Add(response.StatusCode);
        }

        statuses.ShouldAllBe(s => s != HttpStatusCode.TooManyRequests);
        limits.Identify.WindowSeconds.ShouldBeLessThanOrEqualTo(60);
        limits.Identify.Permits.ShouldBeGreaterThanOrEqualTo(450);
    }

    [Fact]
    public async Task The_health_checks_are_never_limited()
    {
        var host = Host(("Default", 2));
        using var client = host.CreateClient();

        var statuses = await SendAsync(client, 6, () => new HttpRequestMessage(HttpMethod.Get, "/health/live"));

        statuses.ShouldAllBe(s => s == HttpStatusCode.OK);
    }

    [Fact]
    public void The_defaults_are_the_numbers_in_the_security_doc()
    {
        var defaults = new CollectionMaxxing.Api.Security.SecurityOptions.LimitOptions();

        defaults.Default.ShouldBe(new(600, 60));
        defaults.Search.ShouldBe(new(60, 60));
        defaults.Barcodes.ShouldBe(new(120, 60));
        defaults.Identify.ShouldBe(new(500, 60));
        defaults.Export.ShouldBe(new(1, 600));
        defaults.AuthHourly.ShouldBe(new(30, 3600));
    }

    /// <summary>The API with the given groups lowered to the given permits (a window of a minute, or the hour the table says).</summary>
    private WebApplicationFactory<Program> Host(params (string Group, int Permits)[] limits)
    {
        var factory = Api.Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                limits.ToDictionary(l => $"Security:Limits:{l.Group}:Permits", l => (string?)l.Permits.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter, PeerFilter>());
        });
        _factories.Add(factory);
        return factory;
    }

    private async Task<HttpClient> UserAsync(WebApplicationFactory<Program> host)
    {
        var library = await CreateLibraryAsync();
        return UserClient(host, library.UserWithRole(LibraryRole.Owner));
    }

    private static HttpClient UserClient(WebApplicationFactory<Program> host, Guid userId)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, userId.ToString());
        return client;
    }

    private static async Task<List<HttpStatusCode>> SendAsync(HttpClient client, int count, Func<HttpRequestMessage> request)
    {
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < count; i++)
        {
            using var response = await client.SendAsync(request(), TestContext.Current.CancellationToken);
            statuses.Add(response.StatusCode);
        }

        return statuses;
    }
}
