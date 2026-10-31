using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Tests.Support;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Security;

/// <summary>
/// CSRF (docs/07): a request that carries the session cookie must also carry <c>X-CSRF: 1</c> to write. This sweeps EVERY write route in the
/// routing table (not a hand-made list), so a route added tomorrow is covered without anyone remembering it. A bearer call is not affected:
/// it carries no ambient credential for a hostile page to ride on.
/// </summary>
public partial class CsrfEveryWriteRouteTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static readonly HashSet<string> Safe = new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS", "TRACE" };

    private List<(string Method, string Url)> WriteRoutes()
    {
        var id = Guid.NewGuid().ToString();
        return [.. EndpointFacts.Read(api.Factory.Services.GetRequiredService<EndpointDataSource>())
            .Where(e => !Safe.Contains(e.Method) && e.Method != "*")
            .Select(e => (e.Method, Url: RouteParameter().Replace(e.Route, m => m.Groups["name"].Value == "token" ? "a-token" : id)))];
    }

    [GeneratedRegex(@"\{(?<name>\w+)(:[^}]+)?\}")]
    private static partial Regex RouteParameter();

    private async Task<HttpResponseMessage> SendAsync(string method, string url, Action<HttpRequestMessage> tweak)
    {
        using var client = api.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), url) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        tweak(request);
        return await client.SendAsync(request);
    }

    [Fact]
    public void The_sweep_sees_the_write_routes_of_every_group()
    {
        var routes = WriteRoutes();

        routes.Count.ShouldBeGreaterThan(35); // AUTH-1 removed the passkey, pairing and token routes
        routes.ShouldContain(r => r.Url.StartsWith("/api/v1/libraries/", StringComparison.Ordinal));
        routes.ShouldContain(r => r.Url.StartsWith("/api/v1/scan-sessions/", StringComparison.Ordinal));
        routes.ShouldContain(r => r.Url.StartsWith("/api/v1/me", StringComparison.Ordinal));
        routes.ShouldContain(r => r.Url.StartsWith("/api/admin/", StringComparison.Ordinal));
        routes.ShouldContain(r => r.Url.StartsWith("/account/", StringComparison.Ordinal));
        new[] { "POST", "PUT", "PATCH", "DELETE" }.ShouldBeSubsetOf(routes.Select(r => r.Method).Distinct());
    }

    [Fact]
    public async Task Every_write_route_refuses_a_cookie_request_with_no_csrf_header()
    {
        var failures = new List<string>();
        foreach (var (method, url) in WriteRoutes())
        {
            using var response = await SendAsync(method, url, r => r.Headers.Add("Cookie", $"{AuthCookie.Name}=whatever"));
            var body = await response.Content.ReadAsStringAsync();
            if (response.StatusCode != HttpStatusCode.Forbidden || !body.Contains("csrf_required", StringComparison.Ordinal))
            {
                failures.Add($"{method} {url}: {(int)response.StatusCode}");
            }
        }

        failures.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("")]
    [InlineData("2")]
    [InlineData("1, 1")]
    [InlineData("yes")]
    public async Task A_wrong_csrf_header_value_is_no_better_than_none_on_every_write_route(string value)
    {
        var failures = new List<string>();
        foreach (var (method, url) in WriteRoutes())
        {
            using var response = await SendAsync(method, url, r =>
            {
                r.Headers.Add("Cookie", $"{AuthCookie.Name}=whatever");
                r.Headers.TryAddWithoutValidation("X-CSRF", value);
            });
            if (response.StatusCode != HttpStatusCode.Forbidden)
            {
                failures.Add($"{method} {url}: {(int)response.StatusCode}");
            }
        }

        failures.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_cookie_on_a_different_name_is_not_the_session_cookie_and_needs_no_csrf_header()
    {
        // The rule is about the session cookie only: a request without it has no ambient credential to abuse.
        using var response = await SendAsync("POST", $"/api/v1/libraries/{Guid.NewGuid()}/tags", r => r.Headers.Add("Cookie", "other=1"));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_bearer_write_is_not_asked_for_the_csrf_header_but_still_has_to_authenticate()
    {
        var failures = new List<string>();
        foreach (var (method, url) in WriteRoutes().Where(r => !r.Url.StartsWith("/account/", StringComparison.Ordinal) && r.Url != "/setup"))
        {
            using var response = await SendAsync(method, url, r => r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token"));
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                failures.Add($"{method} {url}: {(int)response.StatusCode}");
            }
        }

        failures.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_bearer_header_does_not_let_a_cookie_request_skip_the_csrf_header_when_the_bearer_is_garbage()
    {
        // Documented behaviour: the check is skipped for bearer calls, so a hostile page that can set an Authorization header can't ride the cookie
        // either, because the bearer is what is judged (a bad one is a 401, and the cookie is never consulted).
        using var response = await SendAsync("POST", $"/api/v1/libraries/{Guid.NewGuid()}/tags", r =>
        {
            r.Headers.Add("Cookie", $"{AuthCookie.Name}=whatever");
            r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "garbage");
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("X-HTTP-Method-Override")]
    [InlineData("X-HTTP-Method")]
    [InlineData("X-Method-Override")]
    public async Task A_method_override_header_cannot_turn_a_get_into_a_write_or_hide_one_from_the_csrf_rule(string header)
    {
        // A GET with an override header stays a GET: nothing is deleted. And a POST that says it is a GET is still checked as a POST.
        var id = Guid.NewGuid();
        using var asGet = await SendAsync("GET", $"/api/v1/libraries/{id}/tags/{id}", r => r.Headers.TryAddWithoutValidation(header, "DELETE"));
        asGet.StatusCode.ShouldBeOneOf(HttpStatusCode.MethodNotAllowed, HttpStatusCode.Unauthorized, HttpStatusCode.NotFound);

        using var asPost = await SendAsync("POST", $"/api/v1/libraries/{id}/tags", r =>
        {
            r.Headers.Add("Cookie", $"{AuthCookie.Name}=whatever");
            r.Headers.TryAddWithoutValidation(header, "GET");
        });
        asPost.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
