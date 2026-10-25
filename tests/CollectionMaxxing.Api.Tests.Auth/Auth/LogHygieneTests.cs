using System.Net;
using System.Net.Http.Json;
using CollectionMaxxing.Api.Tests.Support;

namespace CollectionMaxxing.Api.Tests.Auth;

// LOCKED acceptance test of ID-1 (issue #14): no code, token or credential appears in logs. Do not edit or weaken.
// AUTH-1 (#228): the credentials are now Auth0's (the authorization code, the ID token, the access token, the subject).
// ONB-1 (#249): there is no setup code any more; invite codes and the first bearer call (Auth0's /userinfo) are covered instead.
public class LogHygieneTests(ApiFixture api) : AuthTest(api)
{
    [Fact]
    public async Task No_recovery_code_invite_token_or_credential_appears_in_the_logs()
    {
        var browser = App.NewBrowser(TestIdentity.Google("Owner"));
        var owner = await browser.SetUpOwnerAsync();
        var recoveryCode = await App.IssueRecoveryCodeAsync(browser.Identity.Email!);
        var otherDevice = App.NewBrowser();
        (await otherDevice.ContinueAsync(await otherDevice.StartRecoveryAsync(recoveryCode))).Headers.Location?.ToString().ShouldBe("/");
        (await otherDevice.LoginAsync()).Headers.Location?.ToString().ShouldBe("/");
        var accessToken = App.Auth0.AccessToken(browser.Identity.Subject);
        (await App.NewPhoneWithToken(accessToken).GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // A first bearer call: the account is made from Auth0's /userinfo, asked with the caller's own token.
        var newcomer = TestIdentity.Apple("Newcomer");
        App.Auth0.SetProfile(newcomer);
        var newcomerToken = App.Auth0.AccessToken(newcomer.Subject);
        (await App.NewPhoneWithToken(newcomerToken).GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Invites: the link's token and the short code, used and guessed, in URLs and bodies.
        var invite = await browser.PostAsync($"/api/v1/libraries/{owner.LibraryId}/invites", new { name = "Sam", role = "editor" });
        var created = await invite.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var link = created.GetProperty("link").GetString()!;
        var inviteToken = link[(link.LastIndexOf('/') + 1)..];
        var inviteCode = created.GetProperty("code").GetString()!;
        var guest = App.NewBrowser();
        await guest.GetAsync($"/join/{inviteToken}");
        await guest.GetAsync($"/api/v1/invites/{inviteToken}");
        await guest.SignInAsync("/account/login?returnUrl=" + Uri.EscapeDataString("/join/" + inviteToken));
        await guest.PostAsync("/api/v1/invites:preview", new { invite = inviteCode });
        (await guest.PostAsync("/api/v1/invites:accept", new { invite = inviteCode })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await guest.PostAsync("/api/v1/invites:accept", new { invite = "ZZZZ-ZZZZ-ZZ" });
        await guest.GetAsync("/signin-oidc?code=not-a-real-code-" + inviteToken + "&state=garbage");
        await guest.PostAsync("/account/recover", new { code = "WRONG-WRONG-WRONG-WRONG" });

        var secrets = new List<string>
        {
            recoveryCode,
            inviteToken,
            inviteCode,
            inviteCode.Replace("-", string.Empty, StringComparison.Ordinal),
            accessToken,
            newcomerToken,
            browser.Identity.Subject,
            otherDevice.Identity.Subject,
            guest.Identity.Subject,
            newcomer.Subject,
            browser.LastChallenge!.Query,
        };
        secrets.AddRange(browser.SetCookies.Select(c => c.Value.Value!).Where(v => v.Length > 8));
        secrets.AddRange(guest.SetCookies.Select(c => c.Value.Value!).Where(v => v.Length > 8));
        owner.UserId.ShouldNotBe(Guid.Empty);

        App.Logs.Lines.Count.ShouldBeGreaterThan(0, "the capture should see the app's log lines");
        foreach (var secret in secrets)
        {
            App.Logs.Lines.Where(line => line.Contains(secret, StringComparison.Ordinal)).ShouldBeEmpty($"a log line contains '{secret[..6]}…'");
        }
    }
}
