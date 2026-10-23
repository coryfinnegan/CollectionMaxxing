using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using Microsoft.Net.Http.Headers;

namespace CollectionMaxxing.Api.Tests.Auth;

// LOCKED acceptance tests of ID-1 (issue #14), cookie, CSRF and session lifetime. Do not edit or weaken: add new tests instead.
public class SessionCookieTests(ApiFixture api) : AuthTest(api)
{
    [Fact]
    public async Task The_session_cookie_is_host_prefixed_http_only_secure_strict_with_no_domain_and_path_slash()
    {
        var (browser, _) = await SetUpOwnerAsync();

        var cookie = browser.SetCookies.Last(c => c.Name == WebClient.SessionCookie);

        cookie.Name.Value.ShouldStartWith("__Host-");
        cookie.HttpOnly.ShouldBeTrue();
        cookie.Secure.ShouldBeTrue();
        cookie.SameSite.ShouldBe(SameSiteMode.Strict);
        cookie.Domain.HasValue.ShouldBeFalse();
        cookie.Path.Value.ShouldBe("/");
    }

    [Fact]
    public async Task A_login_sets_the_same_strict_cookie()
    {
        var (first, _) = await SetUpOwnerAsync();
        var again = App.NewBrowser();
        again.Identity = first.Identity;

        (await again.LoginAsync()).Headers.Location?.ToString().ShouldBe("/");

        var cookie = again.SetCookies.Last(c => c.Name == WebClient.SessionCookie);
        (cookie.HttpOnly, cookie.Secure, cookie.SameSite, cookie.Domain.HasValue, cookie.Path.Value).ShouldBe((true, true, SameSiteMode.Strict, false, "/"));
    }

    [Fact]
    public async Task Cookie_authenticated_writes_without_the_csrf_header_are_403_and_with_it_are_allowed()
    {
        var (browser, owner) = await SetUpOwnerAsync();
        var locations = $"/api/v1/libraries/{owner.LibraryId}/locations";

        // POST
        var denied = await browser.PostAsync(locations, new { name = "Shelf" }, csrf: false);
        await ShouldBeProblemAsync(denied, HttpStatusCode.Forbidden);
        var created = await browser.PostAsync(locations, new { name = "Shelf" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = (await ReadAsync(created)).GetProperty("id").GetGuid();

        // PATCH
        var patchUrl = $"{locations}/{id}";
        (await browser.SendAsync(HttpMethod.Patch, patchUrl, new { name = "Renamed" }, csrf: false)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await browser.SendAsync(HttpMethod.Patch, patchUrl, new { name = "Renamed" })).StatusCode.ShouldBe(HttpStatusCode.OK);

        // DELETE
        (await browser.SendAsync(HttpMethod.Delete, patchUrl, null, csrf: false)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await browser.SendAsync(HttpMethod.Delete, patchUrl, null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_wrong_csrf_header_value_is_403_too()
    {
        var (browser, owner) = await SetUpOwnerAsync();

        var response = await browser.SendAsync(
            HttpMethod.Post,
            $"/api/v1/libraries/{owner.LibraryId}/locations",
            new { name = "Shelf" },
            csrf: false,
            tweak: request => request.Headers.Add("X-CSRF", "0"));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Reads_do_not_need_the_csrf_header()
    {
        var (browser, owner) = await SetUpOwnerAsync();

        (await browser.SendAsync(HttpMethod.Get, "/api/v1/me", null, csrf: false)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await browser.SendAsync(HttpMethod.Get, $"/api/v1/libraries/{owner.LibraryId}/locations", null, csrf: false)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Bearer_callers_need_no_csrf_header()
    {
        var anonymous = App.NewBrowser();

        // No cookie, no header: the CSRF rule is about cookies only, so a bearer call is judged by its token alone (401 here, not 403).
        var response = await anonymous.SendAsync(
            HttpMethod.Post,
            $"/api/v1/libraries/{Guid.NewGuid()}/locations",
            new { name = "Shelf" },
            csrf: false,
            tweak: request => request.Headers.Authorization = new("Bearer", "not-a-real-token"));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_bearer_header_wins_over_a_stray_cookie_and_is_not_asked_for_the_csrf_header()
    {
        var (browser, owner) = await SetUpOwnerAsync();

        var response = await browser.SendAsync(
            HttpMethod.Post,
            $"/api/v1/libraries/{owner.LibraryId}/locations",
            new { name = "Shelf" },
            csrf: false,
            tweak: request => request.Headers.Authorization = new("Bearer", "not-a-real-token"));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task No_cors_origin_is_allowed()
    {
        var (browser, _) = await SetUpOwnerAsync();

        var response = await browser.SendAsync(
            HttpMethod.Get,
            "/api/v1/me",
            null,
            tweak: request => request.Headers.Add("Origin", "https://evil.example"));

        response.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
    }

    // AUTH-1 (#228), D-2: the lifetimes moved from 8 hours sliding / 30 days absolute to 30 days sliding / one year absolute.
    [Fact]
    public async Task The_session_ends_after_thirty_days_without_use()
    {
        var (browser, _) = await SetUpOwnerAsync();

        App.Time.Advance(TimeSpan.FromDays(30) - TimeSpan.FromSeconds(1));
        (await browser.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);

        App.Time.Advance(TimeSpan.FromDays(30) + TimeSpan.FromSeconds(1));
        (await browser.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Using_the_session_keeps_it_alive_past_thirty_days()
    {
        var (browser, _) = await SetUpOwnerAsync();

        for (var i = 0; i < 4; i++)
        {
            App.Time.Advance(TimeSpan.FromDays(20));
            (await browser.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK, $"after {(i + 1) * 20} days");
        }
    }

    [Fact]
    public async Task The_session_ends_after_a_year_even_if_it_is_always_in_use()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var start = App.Time.GetUtcNow();

        while (App.Time.GetUtcNow() - start < TimeSpan.FromDays(365) - TimeSpan.FromDays(20))
        {
            App.Time.Advance(TimeSpan.FromDays(20));
            (await browser.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK, $"{App.Time.GetUtcNow() - start} into the session");
        }

        // Still inside the year, then past it: used within the last 30 days, but a year is the limit.
        App.Time.Advance(start + TimeSpan.FromDays(365) + TimeSpan.FromSeconds(1) - App.Time.GetUtcNow());
        (await browser.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
