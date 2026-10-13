using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Accounts;

namespace CollectionMaxxing.Api.Tests.Auth;

// LOCKED acceptance test of ID-1 (issue #14): no setup code, token or credential appears in logs. Do not edit or weaken.
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
        (await otherDevice.RegisterAsync(await otherDevice.StartRecoveryAsync(recoveryCode))).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await otherDevice.LoginAsync()).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await browser.PostAsync("/setup", new { code = "WRONG-WRONG-WRONG-WRONG", displayName = "x" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // A fresh server with no owner yet: wrong codes, an invite-looking token in the URL, a join link.
        await using var fresh = new AuthApp(Db.ConnectionString);
        await Db.ResetAsync();
        var secretInvite = "invite-" + Guid.NewGuid().ToString("N");
        var freshCode = await fresh.IssueCodeAsync(SetupCodePurpose.Setup);
        var guest = fresh.NewBrowser();
        await guest.StartSetupAsync("WRONG-WRONG-WRONG-WRONG");
        await guest.RegisterUnpromptedAsync(invite: secretInvite);
        await guest.GetAsync($"/join/{secretInvite}");
        await guest.PostAsync("/account/passkey/creation-options", new { inviteToken = secretInvite, displayName = "Sam" });
        await guest.SetUpOwnerAsync(freshCode);

        var secrets = new List<string>
        {
            setupCode,
            recoveryCode,
            freshCode,
            secretInvite,
            browser.Authenticator.CredentialIdText,
            otherDevice.Authenticator.CredentialIdText,
            guest.Authenticator.CredentialIdText,
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
