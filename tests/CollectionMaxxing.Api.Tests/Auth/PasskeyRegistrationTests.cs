using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Accounts;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Auth;

// LOCKED acceptance tests of ID-1 (issue #14), passkey registration half. Do not edit or weaken: add new tests instead.
public class PasskeyRegistrationTests(ApiFixture api) : AuthTest(api)
{
    [Fact]
    public async Task Register_with_no_code_or_token_is_rejected()
    {
        await App.IssueCodeAsync(SetupCodePurpose.Setup);
        var browser = App.NewBrowser();

        var response = await browser.RegisterUnpromptedAsync();

        response.IsSuccessStatusCode.ShouldBeFalse();
        browser.HasSessionCookie.ShouldBeFalse();
        (await CountUsersAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Creation_options_with_no_code_or_token_and_no_session_are_refused()
    {
        var browser = App.NewBrowser();

        var response = await browser.PostAsync("/account/passkey/creation-options", new { displayName = "Nobody" });

        response.IsSuccessStatusCode.ShouldBeFalse();
    }

    [Fact]
    public async Task Register_with_an_unknown_invite_token_is_rejected()
    {
        var browser = App.NewBrowser();

        var response = await browser.RegisterUnpromptedAsync(invite: "not-an-invite");

        response.IsSuccessStatusCode.ShouldBeFalse();
        browser.HasSessionCookie.ShouldBeFalse();
        (await CountUsersAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task A_recovery_code_is_not_a_setup_code()
    {
        var setup = await App.IssueCodeAsync(SetupCodePurpose.Setup);
        var recovery = await App.IssueCodeAsync(SetupCodePurpose.Recovery);
        var browser = App.NewBrowser();

        (await browser.StartSetupAsync(recovery)).IsSuccessStatusCode.ShouldBeFalse();
        (await browser.PostAsync("/account/passkey/creation-options", new { setupCode = recovery, displayName = "Owner" }))
            .IsSuccessStatusCode.ShouldBeFalse();
        (await browser.RegisterUnpromptedAsync()).IsSuccessStatusCode.ShouldBeFalse();

        (await CountUsersAsync()).ShouldBe(0);
        browser.HasSessionCookie.ShouldBeFalse();
        setup.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task A_setup_or_recovery_code_is_not_an_invite_token()
    {
        var setup = await App.IssueCodeAsync(SetupCodePurpose.Setup);
        var recovery = await App.IssueCodeAsync(SetupCodePurpose.Recovery);
        var browser = App.NewBrowser();

        foreach (var wrong in new[] { setup, recovery })
        {
            (await browser.PostAsync("/account/passkey/creation-options", new { inviteToken = wrong, displayName = "Sneak" }))
                .IsSuccessStatusCode.ShouldBeFalse();
            (await browser.RegisterUnpromptedAsync(invite: wrong)).IsSuccessStatusCode.ShouldBeFalse();
        }

        (await CountUsersAsync()).ShouldBe(0);
        browser.HasSessionCookie.ShouldBeFalse();
    }

    [Fact]
    public async Task A_setup_code_is_not_a_recovery_code()
    {
        var setup = await App.IssueCodeAsync(SetupCodePurpose.Setup);
        var (_, _) = await SetUpOwnerAsync();
        var browser = App.NewBrowser();

        var response = await browser.StartRecoveryAsync(setup);

        response.IsSuccessStatusCode.ShouldBeFalse();
        browser.HasSessionCookie.ShouldBeFalse();
    }

    [Fact]
    public async Task Register_with_a_valid_setup_code_creates_the_account_and_returns_201_with_a_cookie()
    {
        var code = await App.IssueCodeAsync(SetupCodePurpose.Setup);
        var browser = App.NewBrowser();

        var response = await browser.RegisterAsync(await browser.StartSetupAsync(code));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        browser.HasSessionCookie.ShouldBeTrue();
        (await browser.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Register_with_a_valid_recovery_code_adds_a_passkey_for_the_owner_and_signs_them_in()
    {
        var (_, owner) = await SetUpOwnerAsync();
        var recovery = await App.IssueCodeAsync(SetupCodePurpose.Recovery);
        var newDevice = App.NewBrowser();

        var response = await newDevice.RegisterAsync(await newDevice.StartRecoveryAsync(recovery));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        newDevice.HasSessionCookie.ShouldBeTrue();
        var me = await ReadAsync(await newDevice.GetAsync("/api/v1/me"));
        me.GetProperty("id").GetGuid().ShouldBe(owner.UserId);
        await using var context = Db.CreateContext();
        (await context.UserPasskeys.CountAsync()).ShouldBe(2);
        (await CountUsersAsync()).ShouldBe(1);

        // The new passkey signs in later, and the recovery code is spent.
        var later = App.NewBrowser();
        later.Authenticator = newDevice.Authenticator;
        (await later.LoginAsync()).StatusCode.ShouldBe(HttpStatusCode.OK);
        await ShouldBeProblemAsync(await App.NewBrowser().StartRecoveryAsync(recovery), HttpStatusCode.Gone);
    }

    [Fact]
    public async Task An_expired_recovery_code_does_not_work()
    {
        await SetUpOwnerAsync();
        var recovery = await App.IssueCodeAsync(SetupCodePurpose.Recovery);
        App.Time.Advance(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1));

        await ShouldBeProblemAsync(await App.NewBrowser().StartRecoveryAsync(recovery), HttpStatusCode.Gone);
    }

    [Fact]
    public async Task A_signed_in_user_adds_another_passkey_after_a_recent_passkey_sign_in()
    {
        var (browser, owner) = await SetUpOwnerAsync();
        var second = new SoftwareAuthenticator();

        var options = await browser.PostAsync("/account/passkey/creation-options", new { displayName = "Laptop" });
        browser.Authenticator = second;
        var register = await browser.RegisterAsync(options);

        register.StatusCode.ShouldBe(HttpStatusCode.Created);
        await using var context = Db.CreateContext();
        (await context.UserPasskeys.CountAsync()).ShouldBe(2);
        (await context.UserPasskeys.Select(p => p.UserId).Distinct().SingleAsync()).ShouldBe(owner.UserId);
    }

    [Fact]
    public async Task Adding_a_passkey_needs_a_fresh_passkey_assertion()
    {
        var (browser, _) = await SetUpOwnerAsync();
        App.Time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        var stale = await browser.PostAsync("/account/passkey/creation-options", new { displayName = "Laptop" });

        await ShouldBeProblemAsync(stale, HttpStatusCode.Unauthorized);
        (await ReadAsync(stale)).GetProperty("reauth").GetBoolean().ShouldBeTrue();

        (await browser.LoginAsync()).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await browser.PostAsync("/account/passkey/creation-options", new { displayName = "Laptop" })).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Sign_in_with_a_passkey_works_and_logout_ends_the_session()
    {
        var (first, owner) = await SetUpOwnerAsync();
        var second = App.NewBrowser();
        second.Authenticator = first.Authenticator;

        var login = await second.LoginAsync();

        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync(login)).GetProperty("userId").GetGuid().ShouldBe(owner.UserId);
        (await second.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await second.PostAsync("/account/logout")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await second.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Sign_in_with_a_passkey_the_server_does_not_know_is_refused()
    {
        await SetUpOwnerAsync();
        var stranger = App.NewBrowser();
        stranger.Authenticator.Create("""{"challenge":"AAAA","user":{"id":"AAAA"}}""");

        var login = await stranger.LoginAsync();

        await ShouldBeProblemAsync(login, HttpStatusCode.Unauthorized);
        stranger.HasSessionCookie.ShouldBeFalse();
    }
}
