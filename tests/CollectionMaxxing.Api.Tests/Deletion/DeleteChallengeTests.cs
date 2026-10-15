using System.Net;
using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Tests.Auth;
using CollectionMaxxing.Api.Tests.Support;

using WebClient = CollectionMaxxing.Api.Tests.Auth.WebClient;

namespace CollectionMaxxing.Api.Tests.Deletion;

// LIB-2d (issue #99), items 1: the challenge a delete answers is single-use on the server, not only by the passkey's signature counter.
public class DeleteChallengeTests(ApiFixture api) : DeletionTest(api)
{
    private const string ReauthType = "https://collectionmaxxing.app/problems/reauth_required";

    /// <summary>An owner whose passkey, like an iCloud one, always reports a signature counter of 0.</summary>
    private async Task<(WebClient Browser, WebClient.SignedIn Owner)> SetUpOwnerWithZeroCounterAsync()
    {
        var browser = App.NewBrowser();
        browser.Authenticator = new SoftwareAuthenticator { CounterStaysZero = true };
        var owner = await browser.SetUpOwnerAsync(await App.IssueCodeAsync(CollectionMaxxing.Domain.Accounts.SetupCodePurpose.Setup));
        return (browser, owner);
    }

    private static string LastCookie(WebClient browser, string name) =>
        browser.SetCookies.Last(c => c.Name.Value == name).Value.Value!;

    /// <summary>The delete as an attacker replays it: the old assertion, with the old ceremony cookie put back by hand.</summary>
    private static Task<HttpResponseMessage> ReplayAsync(WebClient browser, System.Text.Json.JsonElement credential, string sessionCookie, string ceremonyCookie) =>
        browser.SendAsync(HttpMethod.Delete, "/api/v1/me", new { credential }, tweak: request =>
        {
            request.Headers.Remove("Cookie");
            request.Headers.Add("Cookie", $"{WebClient.SessionCookie}={sessionCookie}; {CeremonyCookie.Name}={ceremonyCookie}");
        });

    [Fact]
    public async Task Replaying_a_used_assertion_with_its_old_ceremony_cookie_is_refused_even_when_the_passkey_counter_stays_at_zero()
    {
        var (browser, owner) = await SetUpOwnerWithZeroCounterAsync();
        var libraryId = owner.LibraryId!.Value;
        var (_, sam) = await AddMemberAsync(browser, libraryId, "editor", "Sam");

        var credential = await AssertAsync(browser);
        var ceremony = LastCookie(browser, CeremonyCookie.Name);
        var session = LastCookie(browser, WebClient.SessionCookie);
        (await DeleteWithAsync(browser, credential)).StatusCode.ShouldBe(HttpStatusCode.Conflict); // the owner still has a member
        (await browser.SendAsync(HttpMethod.Delete, $"{MembersUrl(libraryId)}/{sam.UserId}", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var replay = await ReplayAsync(browser, credential, session, ceremony);

        await ShouldBeProblemAsync(replay, HttpStatusCode.Unauthorized);
        (await ReadAsync(replay)).GetProperty("type").GetString().ShouldBe(ReauthType);
        (await UserRowAsync(owner.UserId)).DeletedAt.ShouldBeNull("the replay must not delete the account");
        (await browser.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PurgeJobsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Every_delete_attempt_clears_the_ceremony_cookie_whatever_the_outcome()
    {
        var (browser, owner) = await SetUpOwnerWithZeroCounterAsync();
        await AddMemberAsync(browser, owner.LibraryId!.Value, "viewer", "Sam");
        var credential = await AssertAsync(browser);
        LastCookie(browser, CeremonyCookie.Name).ShouldNotBeNullOrEmpty();

        (await DeleteWithAsync(browser, credential)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var cleared = browser.SetCookies.Last(c => c.Name.Value == CeremonyCookie.Name);
        cleared.Value.Value.ShouldBeNullOrEmpty("the 409 must hand back a cleared ceremony cookie");
        cleared.Expires.ShouldNotBeNull();
        cleared.Expires!.Value.ShouldBeLessThan(App.Time.GetUtcNow().AddYears(-1));
    }
}
