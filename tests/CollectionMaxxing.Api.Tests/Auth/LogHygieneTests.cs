using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Accounts;

namespace CollectionMaxxing.Api.Tests.Auth;

// LOCKED acceptance test of ID-1 (issue #14): no setup code, token or credential appears in logs. Do not edit or weaken.
// AUTH-1 (#228): the credentials are now Auth0's (the authorization code, the ID token, the access token, the subject).
public class LogHygieneTests(ApiFixture api) : AuthTest(api)
{
    [Fact]
    public async Task No_setup_code_token_or_credential_appears_in_the_logs()
    {
        var setupCode = await App.IssueCodeAsync(SetupCodePurpose.Setup);
        var browser = App.NewBrowser();
        var owner = await browser.SetUpOwnerAsync(setupCode);
        var recoveryCode = await App.IssueCodeAsync(SetupCodePurpose.Recovery);
        var otherDevice = App.NewBrowser();
        (await otherDevice.ContinueAsync(await otherDevice.StartRecoveryAsync(recoveryCode))).Headers.Location?.ToString().ShouldBe("/");
        (await otherDevice.LoginAsync()).Headers.Location?.ToString().ShouldBe("/");
        var accessToken = App.Auth0.AccessToken(browser.Identity.Subject);
        (await App.NewPhoneWithToken(accessToken).GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var unlinkedToken = App.Auth0.AccessToken("apple|unlinked-" + Guid.NewGuid().ToString("N"));
        (await App.NewPhoneWithToken(unlinkedToken).GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await browser.PostAsync("/setup", new { code = "WRONG-WRONG-WRONG-WRONG" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // A fresh server with no owner yet: wrong codes, an invite-looking token in the URL, a join link.
        await using var fresh = new AuthApp(Db.ConnectionString);
        await Db.ResetAsync();
        var secretInvite = "invite-" + Guid.NewGuid().ToString("N");
        var freshCode = await fresh.IssueCodeAsync(SetupCodePurpose.Setup);
        var guest = fresh.NewBrowser();
        await guest.StartSetupAsync("WRONG-WRONG-WRONG-WRONG");
        await guest.GetAsync($"/join/{secretInvite}");
        await guest.SignInAsync($"/account/login?invite={secretInvite}");
        await guest.GetAsync("/signin-oidc?code=not-a-real-code-" + secretInvite + "&state=garbage");
        await guest.SetUpOwnerAsync(freshCode);

        var secrets = new List<string>
        {
            setupCode,
            recoveryCode,
            freshCode,
            secretInvite,
            accessToken,
            unlinkedToken,
            browser.Identity.Subject,
            otherDevice.Identity.Subject,
            guest.Identity.Subject,
            browser.LastChallenge!.Query,
        };
        secrets.AddRange(browser.SetCookies.Select(c => c.Value.Value!).Where(v => v.Length > 8));
        secrets.AddRange(guest.SetCookies.Select(c => c.Value.Value!).Where(v => v.Length > 8));
        owner.UserId.ShouldNotBe(Guid.Empty);

        foreach (var logs in new[] { App.Logs, fresh.Logs })
        {
            logs.Lines.Count.ShouldBeGreaterThan(0, "the capture should see the app's log lines");
            foreach (var secret in secrets)
            {
                logs.Lines.Where(line => line.Contains(secret, StringComparison.Ordinal)).ShouldBeEmpty($"a log line contains '{secret[..6]}…'");
            }
        }
    }
}
