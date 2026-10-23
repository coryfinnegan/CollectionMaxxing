using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Domain.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Auth;

// LOCKED acceptance tests of ID-1 (issue #14), setup half. Do not edit or weaken: add new tests instead.
// AUTH-1 (#228): the passkey made after the code is now an Auth0 sign-in; the name comes from the Auth0 profile.
public class SetupTests(ApiFixture api) : AuthTest(api)
{
    [Fact]
    public async Task Setup_works_exactly_once_then_post_setup_and_get_setup_are_404()
    {
        var code = await App.IssueCodeAsync(SetupCodePurpose.Setup);
        var first = App.NewBrowser();
        (await first.GetAsync("/setup")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var owner = await first.SetUpOwnerAsync(code);

        owner.LibraryId.ShouldNotBeNull();
        (await CountOwnersAsync()).ShouldBe(1);
        var later = App.NewBrowser();
        (await later.PostAsync("/setup", new { code })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await later.GetAsync("/setup")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await later.PostAsync("/setup", new { code = "anything" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CountOwnersAsync()).ShouldBe(1);

        // And no new setup code can be made once an owner exists.
        using var scope = App.Services.CreateScope();
        var issued = await scope.ServiceProvider.GetRequiredService<IssueCode.Handler>().HandleAsync(SetupCodePurpose.Setup, CancellationToken.None);
        issued.ShouldBeNull();
    }

    [Fact]
    public async Task The_setup_page_is_served_while_there_is_no_owner()
    {
        var browser = App.NewBrowser();

        var response = await browser.GetAsync("/setup");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/html");
    }

    [Fact]
    public async Task A_wrong_setup_code_never_creates_an_owner()
    {
        await App.IssueCodeAsync(SetupCodePurpose.Setup);
        var browser = App.NewBrowser();

        var start = await browser.StartSetupAsync("AAAA-AAAA-AAAA-AAAA");
        await ShouldBeProblemAsync(start, HttpStatusCode.BadRequest);

        // Even an attempt to finish the sign-in anyway creates nothing.
        (await browser.SignInAsync("/account/login?intent=setup")).Headers.Location?.ToString().ShouldBe("/account/not-linked");
        (await CountOwnersAsync()).ShouldBe(0);
        (await CountUsersAsync()).ShouldBe(0);
        browser.HasSessionCookie.ShouldBeFalse();
    }

    [Fact]
    public async Task An_expired_setup_code_24_hours_and_one_second_old_never_creates_an_owner()
    {
        var code = await App.IssueCodeAsync(SetupCodePurpose.Setup);
        App.Time.Advance(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1));
        var browser = App.NewBrowser();

        var start = await browser.StartSetupAsync(code);

        await ShouldBeProblemAsync(start, HttpStatusCode.Gone);
        (await CountOwnersAsync()).ShouldBe(0);
        (await CountUsersAsync()).ShouldBe(0);
        browser.HasSessionCookie.ShouldBeFalse();
    }

    [Fact]
    public async Task A_setup_code_that_is_still_in_date_by_a_second_works()
    {
        var code = await App.IssueCodeAsync(SetupCodePurpose.Setup);
        App.Time.Advance(TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1));
        var browser = App.NewBrowser();

        var owner = await browser.SetUpOwnerAsync(code);

        owner.LibraryId.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_setup_code_that_expires_while_the_person_is_at_Auth0_never_creates_an_owner()
    {
        var code = await App.IssueCodeAsync(SetupCodePurpose.Setup);
        var browser = App.NewBrowser();
        App.Time.Advance(TimeSpan.FromHours(23));
        var options = await browser.StartSetupAsync(code);
        options.StatusCode.ShouldBe(HttpStatusCode.OK);

        App.Time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        var register = await browser.ContinueAsync(options);

        register.Headers.Location?.ToString().ShouldBeOneOf("/login?error=signin_failed", "/account/not-linked"); // the code, or the intent it sealed, has run out
        browser.HasSessionCookie.ShouldBeFalse();
        (await CountOwnersAsync()).ShouldBe(0);
        (await CountUsersAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task An_already_used_setup_code_never_creates_a_second_owner()
    {
        var code = await App.IssueCodeAsync(SetupCodePurpose.Setup);
        var slow = App.NewBrowser();
        var fast = App.NewBrowser();
        var slowOptions = await slow.StartSetupAsync(code);
        var fastOptions = await fast.StartSetupAsync(code);

        var fastResult = await fast.ContinueAsync(fastOptions);
        fastResult.Headers.Location?.ToString().ShouldBe("/");
        var slowResult = await slow.ContinueAsync(slowOptions);

        slowResult.Headers.Location?.ToString().ShouldBe("/login?error=setup_closed");
        slow.HasSessionCookie.ShouldBeFalse();
        (await CountOwnersAsync()).ShouldBe(1);
        (await CountUsersAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Setup_creates_the_owner_a_personal_library_and_uses_up_the_code()
    {
        var code = await App.IssueCodeAsync(SetupCodePurpose.Setup);
        var browser = App.NewBrowser(TestIdentity.Google("Cory"));

        var owner = await browser.SetUpOwnerAsync(code);

        await using (var users = Db.CreateContext())
        {
            (await users.Users.SingleAsync()).DisplayName.ShouldBe("Cory");
        }

        var me = await ReadAsync(await browser.GetAsync("/api/v1/me"));
        me.GetProperty("id").GetGuid().ShouldBe(owner.UserId);
        var membership = me.GetProperty("memberships").EnumerateArray().Single();
        membership.GetProperty("libraryId").GetGuid().ShouldBe(owner.LibraryId!.Value);
        membership.GetProperty("role").GetString().ShouldBe("owner");
        await using var context = Db.CreateContext();
        (await context.SetupCodes.SingleAsync()).UsedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task The_setup_code_is_stored_only_as_a_hash()
    {
        var code = await App.IssueCodeAsync(SetupCodePurpose.Setup);

        await using var context = Db.CreateContext();
        var stored = await context.SetupCodes.SingleAsync();
        stored.CodeHash.ShouldNotContain(code);
        stored.CodeHash.ShouldNotBe(code);
        stored.ExpiresAt.ShouldBe(App.Time.GetUtcNow() + TimeSpan.FromHours(24), TimeSpan.FromMilliseconds(1)); // Postgres keeps microseconds
    }

    [Fact]
    public async Task Setup_without_a_name_from_Auth0_calls_the_owner_Owner()
    {
        var code = await App.IssueCodeAsync(SetupCodePurpose.Setup);
        var browser = App.NewBrowser(new TestIdentity("apple|" + Guid.NewGuid().ToString("N")));

        await browser.SetUpOwnerAsync(code);

        await using var context = Db.CreateContext();
        (await context.Users.SingleAsync()).DisplayName.ShouldBe("Owner");
    }
}
