using System.Net;
using System.Net.Http.Json;
using CollectionMaxxing.Api.Tests.RateLimiting;

namespace CollectionMaxxing.Api.Tests.Hardening;

/// <summary>
/// Every row of the "Rate limits" table in docs/08 that is not the code-guessing limiter (that one has its own tests in Auth/ and RateLimiting/):
/// at its limit the next request is a 429 with Retry-After, just under it the request goes through to whatever the endpoint does (here 401 or
/// 500, never 429). The limits are lowered with settings so a test needs a handful of requests, not hundreds; the table's real numbers are
/// the defaults, checked in <see cref="The_defaults_are_the_numbers_in_the_security_doc"/>.
/// </summary>
public sealed class RateLimitGroupTests : IDisposable
{
    private static readonly Guid Session = Guid.NewGuid();

    private readonly ProductionHost _host = new(new Dictionary<string, string?>
    {
        ["Security:Limits:Default:Permits"] = "4",
        ["Security:Limits:Search:Permits"] = "3",
        ["Security:Limits:Barcodes:Permits"] = "2",
        ["Security:Limits:Identify:Permits"] = "5",
        ["Security:Limits:Export:Permits"] = "1",
        ["Security:Limits:AuthHourly:Permits"] = "2",
    });

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Catalogue_search_allows_its_limit_and_then_answers_429_with_retry_after()
    {
        using var client = _host.Client(Guid.NewGuid());

        var statuses = await SendAsync(client, 4, () => new HttpRequestMessage(HttpMethod.Get, "/api/v1/catalog/search?q=zelda"));

        statuses.Take(3).ShouldAllBe(s => s != HttpStatusCode.TooManyRequests);
        statuses[3].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task A_refusal_says_when_to_come_back_and_is_problem_json()
    {
        using var client = _host.Client(Guid.NewGuid());
        await SendAsync(client, 3, () => new HttpRequestMessage(HttpMethod.Get, "/api/v1/catalog/search?q=a"));

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
        using var client = _host.Client(Guid.NewGuid());

        var statuses = await SendAsync(client, 3, () => new HttpRequestMessage(HttpMethod.Post, $"/api/v1/scan-sessions/{Session}/barcodes") { Content = JsonContent.Create(new { }) });

        statuses.Take(2).ShouldAllBe(s => s != HttpStatusCode.TooManyRequests);
        statuses[2].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Identify_is_limited_by_the_items_it_carries_not_by_the_calls()
    {
        using var client = _host.Client(Guid.NewGuid());
        var url = $"/api/v1/scan-sessions/{Session}/identify";

        var first = await client.PostAsJsonAsync(url, new { items = new object[] { new { }, new { }, new { } } }, TestContext.Current.CancellationToken); // 3 of 5
        var second = await client.PostAsJsonAsync(url, new { items = new object[] { new { }, new { } } }, TestContext.Current.CancellationToken); // 5 of 5
        var third = await client.PostAsJsonAsync(url, new { items = new object[] { new { } } }, TestContext.Current.CancellationToken); // 6th item

        first.StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
        second.StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
        third.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task An_identify_call_with_more_items_than_the_hour_allows_is_refused_outright()
    {
        using var client = _host.Client(Guid.NewGuid());

        var response = await client.PostAsJsonAsync($"/api/v1/scan-sessions/{Session}/identify", new { items = Enumerable.Range(0, 6).Select(_ => new { }).ToArray() }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Export_may_be_started_once_per_window()
    {
        using var client = _host.Client(Guid.NewGuid());

        var statuses = await SendAsync(client, 2, () => new HttpRequestMessage(HttpMethod.Get, "/api/v1/me/export"));

        statuses[0].ShouldNotBe(HttpStatusCode.TooManyRequests);
        statuses[1].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Looking_at_an_export_already_started_does_not_use_up_the_export_limit()
    {
        using var client = _host.Client(Guid.NewGuid());

        var statuses = await SendAsync(client, 3, () => new HttpRequestMessage(HttpMethod.Get, $"/api/v1/me/export/{Guid.NewGuid()}"));

        statuses.ShouldAllBe(s => s != HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Everything_else_shares_one_bucket_per_user()
    {
        using var client = _host.Client(Guid.NewGuid());

        var statuses = await SendAsync(client, 5, () => new HttpRequestMessage(HttpMethod.Get, "/api/v1/libraries"));

        statuses.Take(4).ShouldAllBe(s => s != HttpStatusCode.TooManyRequests);
        statuses[4].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task One_users_requests_never_use_up_another_users_bucket()
    {
        using var busy = _host.Client(Guid.NewGuid());
        using var quiet = _host.Client(Guid.NewGuid());
        await SendAsync(busy, 6, () => new HttpRequestMessage(HttpMethod.Get, "/api/v1/libraries"));

        var statuses = await SendAsync(quiet, 2, () => new HttpRequestMessage(HttpMethod.Get, "/api/v1/libraries"));

        statuses.ShouldAllBe(s => s != HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Callers_who_are_not_signed_in_are_judged_by_address_not_all_together()
    {
        using var one = _host.Client();
        using var two = _host.Client();
        one.DefaultRequestHeaders.Add(PeerFilter.Header, "198.51.100.1");
        two.DefaultRequestHeaders.Add(PeerFilter.Header, "198.51.100.2");
        await SendAsync(one, 6, () => new HttpRequestMessage(HttpMethod.Get, "/api/v1/libraries"));

        var statuses = await SendAsync(two, 1, () => new HttpRequestMessage(HttpMethod.Get, "/api/v1/libraries"));

        statuses[0].ShouldNotBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task The_code_and_passkey_endpoints_also_have_an_hourly_limit()
    {
        using var client = _host.Client();

        var statuses = await SendAsync(client, 3, () => new HttpRequestMessage(HttpMethod.Post, "/account/recover") { Content = JsonContent.Create(new { code = "x" }) });

        statuses.Take(2).ShouldAllBe(s => s != HttpStatusCode.TooManyRequests);
        statuses[2].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task The_health_checks_are_never_limited()
    {
        using var client = _host.Client();

        var statuses = await SendAsync(client, 8, () => new HttpRequestMessage(HttpMethod.Get, "/health/live"));

        statuses.ShouldAllBe(s => s != HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public void The_defaults_are_the_numbers_in_the_security_doc()
    {
        var defaults = new CollectionMaxxing.Api.Security.SecurityOptions.LimitOptions();

        defaults.Default.ShouldBe(new(600, 60));
        defaults.Search.ShouldBe(new(60, 60));
        defaults.Barcodes.ShouldBe(new(120, 60));
        defaults.Identify.ShouldBe(new(1000, 3600));
        defaults.Export.ShouldBe(new(1, 600));
        defaults.AuthHourly.ShouldBe(new(30, 3600));
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
