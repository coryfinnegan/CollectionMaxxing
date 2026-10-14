using System.Net;
using CollectionMaxxing.Api.Tests.Support;

namespace CollectionMaxxing.Api.Tests.Auth;

/// <summary>
/// Follow-up from the ID-1 verifier (issue #21): the CSRF header rule is tested on the sign-out and passkey endpoints, not only on
/// <c>/locations</c>. A request that carries the session cookie but not <c>X-CSRF: 1</c> is refused before anything happens.
/// </summary>
public class CsrfCoverageTests(ApiFixture api) : AuthTest(api)
{
    public static TheoryData<string> CookieWrites =>
    [
        "/account/logout",
        "/account/passkey/creation-options",
        "/account/passkey/request-options",
        "/account/passkey/register",
        "/account/login",
        "/account/recover",
    ];

    [Theory]
    [MemberData(nameof(CookieWrites))]
    public async Task A_cookie_authenticated_post_without_the_csrf_header_is_403(string url)
    {
        var (browser, _) = await SetUpOwnerAsync();

        var response = await browser.PostAsync(url, new { }, csrf: false);

        await ShouldBeProblemAsync(response, HttpStatusCode.Forbidden);
        (await ReadAsync(response)).GetProperty("code").GetString().ShouldBe("csrf_required");
        browser.HasSessionCookie.ShouldBeTrue("a refused request must not sign anyone out");
    }

    [Fact]
    public async Task Logout_without_the_csrf_header_leaves_the_session_working_and_with_it_ends_it()
    {
        var (browser, _) = await SetUpOwnerAsync();

        (await browser.PostAsync("/account/logout", csrf: false)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await browser.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await browser.PostAsync("/account/logout")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await browser.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_wrong_header_value_on_a_passkey_endpoint_is_403_too()
    {
        var (browser, _) = await SetUpOwnerAsync();

        var response = await browser.SendAsync(
            HttpMethod.Post,
            "/account/passkey/creation-options",
            new { },
            csrf: false,
            tweak: request => request.Headers.Add("X-CSRF", "0"));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
