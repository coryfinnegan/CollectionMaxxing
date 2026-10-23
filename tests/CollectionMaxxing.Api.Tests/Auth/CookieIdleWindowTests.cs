using System.Net;
using CollectionMaxxing.Api.Tests.Support;

namespace CollectionMaxxing.Api.Tests.Auth;

// AUTH-2 (issue #229), lead addition 2: the web cookie keeps a true 30-day idle window (D-2). ASP.NET's own sliding expiration renews only
// after half the lifetime, so the real limit used to be 15 to 30 days; the cookie is renewed once the ticket is about a day old.
public class CookieIdleWindowTests(ApiFixture api) : AuthTest(api)
{
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    private async Task<WebClient> SignedInAndUsedOnDay14Async()
    {
        var (browser, _) = await SetUpOwnerAsync();
        App.Time.Advance(14 * Day);
        (await browser.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK, "used on day 14");
        return browser;
    }

    [Fact]
    public async Task Signed_in_on_day_0_and_used_on_day_14_the_session_is_still_good_on_day_43_after_29_idle_days()
    {
        var browser = await SignedInAndUsedOnDay14Async();

        App.Time.Advance(29 * Day);

        (await browser.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Signed_in_on_day_0_and_used_on_day_14_the_session_is_over_on_day_45_after_31_idle_days()
    {
        var browser = await SignedInAndUsedOnDay14Async();

        App.Time.Advance(31 * Day);

        (await browser.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Daily_use_never_gets_past_the_one_year_cap()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var start = App.Time.GetUtcNow();

        for (var day = 1; day <= 364; day++)
        {
            App.Time.Advance(Day);
            (await browser.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK, $"day {day}");
        }

        App.Time.Advance(start + TimeSpan.FromDays(365) + TimeSpan.FromSeconds(1) - App.Time.GetUtcNow());
        (await browser.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "a year is the limit, however often it is used");
    }
}
