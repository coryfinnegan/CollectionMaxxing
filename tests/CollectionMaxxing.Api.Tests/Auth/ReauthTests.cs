using System.Net;
using CollectionMaxxing.Api.Tests.Support;

namespace CollectionMaxxing.Api.Tests.Auth;

// LOCKED acceptance tests of ID-1 (issue #14), re-authentication. Do not edit or weaken: add new tests instead.
public class ReauthTests(ApiFixture api) : AuthTest(api)
{
    public static TheoryData<string> Endpoints =>
    [
        "DELETE /api/v1/me",
        "POST /api/v1/libraries/{lib}/invites",
        "POST /api/v1/me/devices/pair",
    ];

    private static Task<HttpResponseMessage> CallAsync(WebClient browser, WebClient.SignedIn owner, string endpoint)
    {
        var parts = endpoint.Split(' ');
        var url = parts[1].Replace("{lib}", owner.LibraryId.ToString(), StringComparison.Ordinal);
        object? body = url.EndsWith("/invites", StringComparison.Ordinal) ? new { name = "Sam", role = "viewer" } : null;
        return browser.SendAsync(new HttpMethod(parts[0]), url, body);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task An_assertion_4_minutes_59_seconds_old_is_fresh_enough(string endpoint)
    {
        var (browser, owner) = await SetUpOwnerAsync();
        App.Time.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));

        var response = await CallAsync(browser, owner, endpoint);

        // Past the re-auth gate: these endpoints are built by ID-2 and ID-3, so what answers is their 501.
        response.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task An_assertion_5_minutes_1_second_old_gets_401_reauth_required(string endpoint)
    {
        var (browser, owner) = await SetUpOwnerAsync();
        App.Time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        var response = await CallAsync(browser, owner, endpoint);

        await ShouldBeProblemAsync(response, HttpStatusCode.Unauthorized);
        var problem = await ReadAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("https://collectionmaxxing.app/problems/reauth_required");
        problem.GetProperty("reauth").GetBoolean().ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task A_fresh_login_clears_the_reauth_requirement(string endpoint)
    {
        var (browser, owner) = await SetUpOwnerAsync();
        App.Time.Advance(TimeSpan.FromMinutes(6));
        (await CallAsync(browser, owner, endpoint)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await browser.LoginAsync()).StatusCode.ShouldBe(HttpStatusCode.OK);
        var response = await CallAsync(browser, owner, endpoint);

        response.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
    }

    [Fact]
    public async Task Ordinary_endpoints_do_not_ask_for_a_recent_passkey()
    {
        var (browser, _) = await SetUpOwnerAsync();
        App.Time.Advance(TimeSpan.FromHours(2));

        (await browser.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_non_member_is_still_told_404_not_asked_to_reauthenticate()
    {
        var (browser, _) = await SetUpOwnerAsync();
        App.Time.Advance(TimeSpan.FromMinutes(10));

        var response = await browser.PostAsync($"/api/v1/libraries/{Guid.NewGuid()}/invites", new { name = "Sam", role = "viewer" });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
