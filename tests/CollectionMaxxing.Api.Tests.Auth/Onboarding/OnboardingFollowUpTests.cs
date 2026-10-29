using System.Net;
using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Tests.Auth;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using WebClient = CollectionMaxxing.Api.Tests.Auth.WebClient;

namespace CollectionMaxxing.Api.Tests.Onboarding;

// ONB-1b (issue #256): follow-ups to ONB-1 found by its verifier. New tests, written before the changes they ask for.

/// <summary>(b) <c>cm-admin recovery-code</c> names one account. When two accounts share the email, it issues nothing.</summary>
public class RecoveryWithSharedEmailTests(ApiFixture api) : OnboardingTest(api)
{
    [Fact]
    public async Task Two_accounts_with_the_same_email_get_no_recovery_code_and_it_says_why()
    {
        var (_, ana) = await NewAccountAsync("Ana");
        var anaEmail = (await UserAsync(ana.UserId)).Email;
        var apple = App.NewBrowser(new TestIdentity("apple|" + Guid.NewGuid().ToString("N"), anaEmail, "Ana"));
        (await apple.SignInAsync()).Headers.Location?.ToString().ShouldBe("/");
        (await CountUsersAsync()).ShouldBe(2);

        var (exit, code, error) = await RecoveryCodeForAsync(anaEmail!);

        exit.ShouldNotBe(0);
        code.ShouldBeNull();
        error.ShouldContain("More than one account");
        (await CountRecoveryCodesAsync()).ShouldBe(0);
    }
}

/// <summary>
/// (d) A new-account permit is for an account that is really about to be made: a call that fails before that (Auth0 can't say who the
/// caller is) or that only joins the account another call is making costs nothing.
/// </summary>
public class NewAccountPermitTests(ApiFixture api) : OnboardingTest(api)
{
    protected override Dictionary<string, string?>? MoreSettings => new() { ["Auth:RateLimit:NewAccountsPerHour"] = "2" };

    [Fact]
    public async Task Retries_while_auth0_cannot_say_who_the_caller_is_do_not_use_up_the_hourly_budget()
    {
        // Four different phones, each refused with a 503: twice the budget. (At once, because every refusal waits out Auth0's retries.)
        App.Auth0.UserInfoDown = true;
        var refused = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => App.NewPhone(TestIdentity.Apple().Subject).GetAsync("/api/v1/me")));
        refused.Select(r => r.StatusCode).ShouldAllBe(status => status == HttpStatusCode.ServiceUnavailable);
        App.Auth0.UserInfoDown = false;

        // Nothing was made and nothing was used: the two permits of the hour are still there.
        (await CountUsersAsync()).ShouldBe(0);
        (await App.NewPhone(TestIdentity.Google("One").Subject).GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await App.NewPhone(TestIdentity.Google("Two").Subject).GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await App.NewPhone(TestIdentity.Google("Three").Subject).GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await CountUsersAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task Parallel_first_calls_of_one_identity_make_one_account_and_use_one_permit()
    {
        var who = TestIdentity.Apple("Sam");
        App.Auth0.SetProfile(who);
        var phone = App.NewPhone(who.Subject);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => phone.GetAsync("/api/v1/me")));

        responses.Select(r => r.StatusCode).ShouldAllBe(status => status == HttpStatusCode.OK);
        (await CountUsersAsync()).ShouldBe(1);

        // One of the two permits is left for somebody else; the second account after that is refused.
        (await App.NewPhone(TestIdentity.Google("Two").Subject).GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await App.NewPhone(TestIdentity.Google("Three").Subject).GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await CountUsersAsync()).ShouldBe(2);
    }
}

/// <summary>
/// The developer-tools role (ADR-027 follow-up): the platform <c>admin</c> role that <c>/api/admin/*</c> needs (the iPhone scanner evals and
/// the job list) is given and taken away on the host, by <c>--dev-tools grant|revoke &lt;email&gt;</c> (<c>cm-admin dev-tools ...</c>).
/// Nothing in the app grants it, and it opens no library.
/// </summary>
public class DevToolsRoleTests(ApiFixture api) : OnboardingTest(api)
{
    private const string AdminUrl = "/api/admin/jobs";

    private async Task<(int ExitCode, string Output, string Error)> RunAsync(string? action, string? email)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await AdminCommands.RunDevToolsAsync(App.Services, action, email, output, error, CancellationToken.None);
        return (exit, output.ToString(), error.ToString());
    }

    private async Task<int> CountRoleGrantsAsync()
    {
        await using var context = Db.CreateContext();
        return await context.UserRoles.CountAsync();
    }

    private static async Task<HttpStatusCode> AdminStatusAsync(WebClient browser) => (await browser.GetAsync(AdminUrl)).StatusCode;

    private static async Task SignInAgainAsync(WebClient browser)
    {
        browser.ClearCookies();
        (await browser.SignInAsync()).Headers.Location?.ToString().ShouldBe("/");
    }

    [Fact]
    public async Task A_new_account_has_no_dev_tools_and_signing_in_does_not_give_them()
    {
        var (ana, _) = await NewAccountAsync("Ana");

        (await AdminStatusAsync(ana)).ShouldBe(HttpStatusCode.Forbidden);
        await SignInAgainAsync(ana);
        (await AdminStatusAsync(ana)).ShouldBe(HttpStatusCode.Forbidden);
        (await CountRoleGrantsAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Grant_gives_the_account_the_admin_routes_at_its_next_sign_in_and_only_that_account()
    {
        var (ana, account) = await NewAccountAsync("Ana");
        var (sam, _) = await NewAccountAsync("Sam");

        var (exit, output, error) = await RunAsync("grant", ana.Identity.Email!.ToUpperInvariant());

        exit.ShouldBe(0, error);
        output.ShouldContain("Ana");
        await SignInAgainAsync(ana);
        (await AdminStatusAsync(ana)).ShouldBe(HttpStatusCode.OK);
        await SignInAgainAsync(sam);
        (await AdminStatusAsync(sam)).ShouldBe(HttpStatusCode.Forbidden);
        await using var context = Db.CreateContext();
        (await context.UserRoles.SingleAsync()).UserId.ShouldBe(account.UserId);
    }

    [Fact]
    public async Task Grant_shows_on_the_phone_at_once_and_revoke_is_gone_on_its_next_call()
    {
        var (ana, _) = await NewAccountAsync("Ana");
        var (sam, _) = await NewAccountAsync("Sam");
        var phone = App.NewPhone(ana.Identity.Subject); // the token is minted before the grant: no re-sign-in
        var samsPhone = App.NewPhone(sam.Identity.Subject);

        (await phone.GetAsync(AdminUrl)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await RunAsync("grant", ana.Identity.Email)).ExitCode.ShouldBe(0);

        (await phone.GetAsync(AdminUrl)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await samsPhone.GetAsync(AdminUrl)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await RunAsync("revoke", ana.Identity.Email)).ExitCode.ShouldBe(0);

        (await phone.GetAsync(AdminUrl)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await phone.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Granting_twice_is_fine_and_gives_one_grant()
    {
        var (ana, _) = await NewAccountAsync("Ana");

        (await RunAsync("grant", ana.Identity.Email)).ExitCode.ShouldBe(0);
        (await RunAsync("grant", ana.Identity.Email)).ExitCode.ShouldBe(0);

        (await CountRoleGrantsAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Revoke_takes_the_role_away_and_ends_the_sessions_that_carry_it()
    {
        var (ana, _) = await NewAccountAsync("Ana");
        (await RunAsync("grant", ana.Identity.Email)).ExitCode.ShouldBe(0);
        await SignInAgainAsync(ana);
        (await AdminStatusAsync(ana)).ShouldBe(HttpStatusCode.OK);

        var (exit, _, error) = await RunAsync("revoke", ana.Identity.Email);

        exit.ShouldBe(0, error);
        (await CountRoleGrantsAsync()).ShouldBe(0);
        (await AdminStatusAsync(ana)).ShouldBe(HttpStatusCode.Unauthorized); // the browser that held the role is signed out
        (await App.NewPhone(ana.Identity.Subject).GetAsync(AdminUrl)).StatusCode.ShouldBe(HttpStatusCode.Forbidden); // and the phone loses it on its next call
        await SignInAgainAsync(ana);
        (await AdminStatusAsync(ana)).ShouldBe(HttpStatusCode.Forbidden);
        (await ana.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Revoking_from_an_account_that_never_had_it_is_fine()
    {
        var (ana, _) = await NewAccountAsync("Ana");

        (await RunAsync("revoke", ana.Identity.Email)).ExitCode.ShouldBe(0);

        (await ana.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("revoke")]
    public async Task An_unknown_email_changes_nothing_and_fails(string action)
    {
        var (ana, _) = await NewAccountAsync("Ana");
        (await RunAsync("grant", ana.Identity.Email)).ExitCode.ShouldBe(0);

        var (exit, _, error) = await RunAsync(action, "nobody@example.com");

        exit.ShouldNotBe(0);
        error.ShouldNotBeEmpty();
        (await CountRoleGrantsAsync()).ShouldBe(1);
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("revoke")]
    public async Task An_email_two_accounts_share_changes_nothing_and_fails(string action)
    {
        var (ana, anaAccount) = await NewAccountAsync("Ana");
        var apple = App.NewBrowser(new TestIdentity("apple|" + Guid.NewGuid().ToString("N"), ana.Identity.Email, "Ana"));
        (await apple.SignInAsync()).Headers.Location?.ToString().ShouldBe("/");
        (await CountUsersAsync()).ShouldBe(2);
        await GiveRoleWithoutTheCommandAsync(anaAccount.UserId); // so that "revoke" has something it must leave alone

        var (exit, _, error) = await RunAsync(action, ana.Identity.Email);

        exit.ShouldNotBe(0);
        error.ShouldContain("More than one account");
        (await CountRoleGrantsAsync()).ShouldBe(1);
    }

    [Theory]
    [InlineData(null, "ana@example.com")]
    [InlineData("", "ana@example.com")]
    [InlineData("promote", "ana@example.com")]
    [InlineData("grant", null)]
    [InlineData("grant", "  ")]
    public async Task A_command_that_is_not_grant_or_revoke_with_an_email_changes_nothing_and_fails(string? action, string? email)
    {
        await NewAccountAsync("Ana");

        var (exit, _, error) = await RunAsync(action, email);

        exit.ShouldNotBe(0);
        error.ShouldContain("Usage");
        (await CountRoleGrantsAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task A_deleted_account_cannot_be_given_dev_tools()
    {
        var (ana, _) = await NewAccountAsync("Ana");
        (await ana.SendAsync(HttpMethod.Delete, "/api/v1/me", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var (exit, _, _) = await RunAsync("grant", ana.Identity.Email);

        exit.ShouldNotBe(0);
        (await CountRoleGrantsAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Dev_tools_open_no_library_that_is_not_the_accounts_own()
    {
        var (owner, _) = await NewAccountAsync("Owner");
        var otherLibrary = await CreatedLibraryAsync(owner, "Not theirs");
        var (dev, _) = await NewAccountAsync("Dev");
        (await RunAsync("grant", dev.Identity.Email)).ExitCode.ShouldBe(0);
        await SignInAgainAsync(dev);
        (await AdminStatusAsync(dev)).ShouldBe(HttpStatusCode.OK);

        foreach (var url in new[] { LibraryUrl(otherLibrary), LibraryUrl(otherLibrary) + "/items", LibraryUrl(otherLibrary) + "/members" })
        {
            (await dev.GetAsync(url)).StatusCode.ShouldBe(HttpStatusCode.NotFound, url);
        }

        (await dev.SendAsync(HttpMethod.Delete, MembershipUrl(otherLibrary), null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await LibraryIdsAsync(dev)).ShouldBeEmpty();
        (await dev.PostAsync($"{LibraryUrl(otherLibrary)}/items", new { })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private async Task GiveRoleWithoutTheCommandAsync(Guid userId)
    {
        using var scope = App.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        (await roles.CreateAsync(new IdentityRole<Guid>("admin"))).Succeeded.ShouldBeTrue();
        (await users.AddToRoleAsync((await users.FindByIdAsync(userId.ToString()))!, "admin")).Succeeded.ShouldBeTrue();
    }
}
