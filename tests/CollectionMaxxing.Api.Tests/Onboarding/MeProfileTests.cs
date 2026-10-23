using System.Net;
using CollectionMaxxing.Api.Tests.Auth;
using CollectionMaxxing.Api.Tests.Support;

namespace CollectionMaxxing.Api.Tests.Onboarding;

/// <summary>
/// ONB-1 contract addendum (#249): <c>GET /api/v1/me</c> also says who the account is, its <c>displayName</c> and <c>email</c>, taken from
/// the account (so the Welcome page can say "Welcome, Ana. You're signed in as ana@example.com").
/// </summary>
public class MeProfileTests(ApiFixture api) : OnboardingTest(api)
{
    [Fact]
    public async Task Me_has_the_display_name_and_email_of_an_account_made_on_the_web()
    {
        var who = new TestIdentity("google-oauth2|" + Guid.NewGuid().ToString("N"), "ana@example.com", "Ana");
        var browser = App.NewBrowser(who);
        await browser.SignInAsync();

        var me = await ReadAsync(await browser.GetAsync("/api/v1/me"));

        me.GetProperty("displayName").GetString().ShouldBe("Ana");
        me.GetProperty("email").GetString().ShouldBe("ana@example.com");
        me.GetProperty("memberships").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Me_has_the_display_name_and_email_from_userinfo_for_an_account_made_by_a_bearer_call()
    {
        var who = new TestIdentity("apple|" + Guid.NewGuid().ToString("N"), "sam@example.com", "Sam");
        App.Auth0.SetProfile(who);

        var response = await App.NewPhone(who.Subject).GetAsync("/api/v1/me");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var me = await ReadAsync(response);
        me.GetProperty("displayName").GetString().ShouldBe("Sam");
        me.GetProperty("email").GetString().ShouldBe("sam@example.com");
    }

    [Fact]
    public async Task An_account_with_no_email_has_a_null_email_and_a_fallback_name()
    {
        var who = new TestIdentity("apple|" + Guid.NewGuid().ToString("N"));
        var browser = App.NewBrowser(who);
        await browser.SignInAsync();

        var me = await ReadAsync(await browser.GetAsync("/api/v1/me"));

        me.GetProperty("displayName").GetString().ShouldBe("Collector");
        me.GetProperty("email").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
    }
}
