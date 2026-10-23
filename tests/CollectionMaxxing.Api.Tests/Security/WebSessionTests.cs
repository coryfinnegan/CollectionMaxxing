using System.Net;
using System.Security.Claims;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Invites;
using CollectionMaxxing.Api.Tests.Support;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using WebClient = CollectionMaxxing.Api.Tests.Auth.WebClient;

namespace CollectionMaxxing.Api.Tests.Security;

// LOCKED acceptance tests of the web-session fix (issue #177): a copied __Host-cm cookie must die with its session. Do not edit or weaken: add new tests instead.
// The cookie carries a session id; the server keeps a web_sessions row for it and refuses the cookie once the row is revoked.
public class WebSessionTests(ApiFixture api) : InviteTest(api)
{
    private const string SessionIdClaim = "cm_sid";

    // ---- 1. sign-out ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_copy_of_the_cookie_made_before_sign_out_is_refused_afterwards()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var copy = browser.SessionCookieValue!;
        (await browser.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await browser.PostAsync("/account/logout")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var thief = App.NewBrowser();
        thief.UseSessionCookie(copy);
        await ShouldBeProblemAsync(await thief.GetAsync("/api/v1/me"), HttpStatusCode.Unauthorized);
        (await thief.GetAsync("/api/v1/me/web-sessions")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Signing_in_again_after_sign_out_works_and_the_old_copy_stays_dead()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var copy = browser.SessionCookieValue!;
        (await browser.PostAsync("/account/logout")).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await browser.LoginAsync()).Headers.Location?.ToString().ShouldBe("/");

        (await browser.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        browser.SessionCookieValue.ShouldNotBe(copy);
        var thief = App.NewBrowser();
        thief.UseSessionCookie(copy);
        (await thief.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Signing_in_over_a_live_session_ends_the_old_one_so_a_browser_holds_one_session()
    {
        var (browser, owner) = await SetUpOwnerAsync();
        var first = browser.SessionCookieValue!;

        (await browser.LoginAsync()).Headers.Location?.ToString().ShouldBe("/");

        var old = App.NewBrowser();
        old.UseSessionCookie(first);
        (await old.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ActiveRowsAsync(owner.UserId)).Count.ShouldBe(1);
    }

    // ---- 2. sign out everywhere ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Signing_out_everywhere_ends_every_browser_of_that_person_and_nobodys_else()
    {
        var (first, owner) = await SetUpOwnerAsync();
        var second = await SecondBrowserOfAsync(first);
        var (member, _) = await AddMemberAsync(first, owner.LibraryId!.Value, "editor");
        var phone = await SignInPhoneAsync(first);

        var response = await second.SendAsync(HttpMethod.Delete, "/api/v1/me/web-sessions", null);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
        (await first.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await second.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await member.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await AsDeviceAsync(HttpMethod.Get, "/api/v1/me", phone.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK); // the phone has its own sign-in
        (await ActiveRowsAsync(owner.UserId)).ShouldBeEmpty();

        (await first.LoginAsync()).Headers.Location?.ToString().ShouldBe("/");
        (await first.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ---- 3. removing a member, deleting an account ---------------------------------------------------------------------

    [Fact]
    public async Task Removing_a_member_revokes_their_browser_sessions_and_leaves_the_owners_alone()
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var libraryId = ownerAccount.LibraryId!.Value;
        var (member, memberAccount) = await AddMemberAsync(owner, libraryId, "editor");
        var memberSecond = await SecondBrowserOfAsync(member);
        var copy = member.SessionCookieValue!;
        (await ActiveRowsAsync(memberAccount.UserId)).Count.ShouldBe(2);

        (await owner.SendAsync(HttpMethod.Delete, $"{MembersUrl(libraryId)}/{memberAccount.UserId}", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await ActiveRowsAsync(memberAccount.UserId)).ShouldBeEmpty();
        (await ActiveRowsAsync(ownerAccount.UserId)).Count.ShouldBe(1);
        (await owner.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var thief = App.NewBrowser();
        thief.UseSessionCookie(copy);
        (await thief.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await memberSecond.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Deleting_the_account_revokes_every_browser_session_of_it()
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var (member, memberAccount) = await AddMemberAsync(owner, ownerAccount.LibraryId!.Value, "viewer");
        var memberSecond = await SecondBrowserOfAsync(member);
        var copy = member.SessionCookieValue!;

        var deleted = await DeleteMemberAccountAsync(member);

        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync());
        (await ActiveRowsAsync(memberAccount.UserId)).ShouldBeEmpty();
        (await RowsAsync(memberAccount.UserId)).ShouldAllBe(r => r.Revoked);
        (await ActiveRowsAsync(ownerAccount.UserId)).Count.ShouldBe(1);
        var thief = App.NewBrowser();
        thief.UseSessionCookie(copy);
        (await thief.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await memberSecond.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ---- 4. signed-in browsers in Settings -----------------------------------------------------------------------------

    [Fact]
    public async Task Settings_lists_the_signed_in_browsers_with_this_one_marked()
    {
        var (first, _) = await SetUpOwnerAsync();
        var second = await SecondBrowserOfAsync(first);

        var seen = await WebSessionsAsync(second);

        seen.Count.ShouldBe(2);
        seen.Count(s => s.GetProperty("isCurrent").GetBoolean()).ShouldBe(1);
        seen.ShouldAllBe(s => s.GetProperty("status").GetString() == "active");
        seen.ShouldAllBe(s => !string.IsNullOrWhiteSpace(s.GetProperty("deviceName").GetString()));
        seen.Select(s => s.GetProperty("id").GetGuid()).Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public async Task Revoking_one_browser_ends_that_session_only()
    {
        var (first, owner) = await SetUpOwnerAsync();
        var second = await SecondBrowserOfAsync(first);
        var third = await SecondBrowserOfAsync(first);
        var secondId = (await WebSessionsAsync(second)).Single(s => s.GetProperty("isCurrent").GetBoolean()).GetProperty("id").GetGuid();

        var revoked = await first.SendAsync(HttpMethod.Delete, $"/api/v1/me/web-sessions/{secondId}", null);

        revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent, await revoked.Content.ReadAsStringAsync());
        (await second.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await first.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await third.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ActiveRowsAsync(owner.UserId)).Count.ShouldBe(2);
        (await WebSessionsAsync(first)).Select(s => s.GetProperty("id").GetGuid()).ShouldNotContain(secondId);
    }

    [Fact]
    public async Task Revoking_the_browser_you_are_using_signs_it_out()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var mine = (await WebSessionsAsync(browser)).Single().GetProperty("id").GetGuid();

        (await browser.SendAsync(HttpMethod.Delete, $"/api/v1/me/web-sessions/{mine}", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await browser.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Revoking_a_browser_twice_is_fine_and_a_made_up_id_is_not_found()
    {
        var (first, _) = await SetUpOwnerAsync();
        var second = await SecondBrowserOfAsync(first);
        var secondId = (await WebSessionsAsync(second)).Single(s => s.GetProperty("isCurrent").GetBoolean()).GetProperty("id").GetGuid();

        (await first.SendAsync(HttpMethod.Delete, $"/api/v1/me/web-sessions/{secondId}", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await first.SendAsync(HttpMethod.Delete, $"/api/v1/me/web-sessions/{secondId}", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await first.SendAsync(HttpMethod.Delete, $"/api/v1/me/web-sessions/{Guid.NewGuid()}", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ---- 5. nobody else's -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Another_user_cannot_list_or_revoke_your_browser_sessions()
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var (member, _) = await AddMemberAsync(owner, ownerAccount.LibraryId!.Value, "editor");
        var ownerSessionId = (await WebSessionsAsync(owner)).Single().GetProperty("id").GetGuid();

        var listed = (await WebSessionsAsync(member)).Select(s => s.GetProperty("id").GetGuid()).ToList();
        var revoke = await member.SendAsync(HttpMethod.Delete, $"/api/v1/me/web-sessions/{ownerSessionId}", null);

        listed.ShouldNotContain(ownerSessionId);
        await ShouldBeProblemAsync(revoke, HttpStatusCode.NotFound);
        (await owner.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ActiveRowsAsync(ownerAccount.UserId)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Signing_out_everywhere_needs_a_sign_in_and_the_csrf_header()
    {
        var (browser, _) = await SetUpOwnerAsync();

        (await App.NewBrowser().SendAsync(HttpMethod.Delete, "/api/v1/me/web-sessions", null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await browser.SendAsync(HttpMethod.Delete, "/api/v1/me/web-sessions", null, csrf: false)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await browser.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ---- 6. cookies from before this change, and cookies that were doctored ----------------------------------------------

    [Fact]
    public async Task A_cookie_from_before_web_sessions_existed_is_refused_cleanly_and_a_normal_sign_in_works()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var legacy = Reseal(browser.SessionCookieValue!, identity => identity.FindAll(SessionIdClaim).ToList().ForEach(identity.RemoveClaim));
        var old = App.NewBrowser();
        old.Identity = browser.Identity;
        old.UseSessionCookie(legacy);

        await ShouldBeProblemAsync(await old.GetAsync("/api/v1/me"), HttpStatusCode.Unauthorized);
        old.HasSessionCookie.ShouldBeFalse("the browser is told to forget the dead cookie");

        (await old.LoginAsync()).Headers.Location?.ToString().ShouldBe("/");
        (await old.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_cookie_naming_a_session_that_does_not_exist_is_refused()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var forged = Reseal(browser.SessionCookieValue!, identity => Replace(identity, SessionIdClaim, Guid.NewGuid().ToString()));
        var thief = App.NewBrowser();
        thief.UseSessionCookie(forged);

        (await thief.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_cookie_cannot_borrow_the_session_of_another_user()
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var (member, _) = await AddMemberAsync(owner, ownerAccount.LibraryId!.Value, "editor");
        var membersSession = Unseal(member.SessionCookieValue!).Principal.FindFirstValue(SessionIdClaim)!;
        var forged = Reseal(owner.SessionCookieValue!, identity => Replace(identity, SessionIdClaim, membersSession));
        var thief = App.NewBrowser();
        thief.UseSessionCookie(forged);

        (await thief.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await member.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ---- housekeeping ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Last_seen_is_written_at_most_once_a_minute()
    {
        var (browser, owner) = await SetUpOwnerAsync();
        var signedInAt = (await ActiveRowsAsync(owner.UserId)).Single().LastSeenAt;

        App.Time.Advance(TimeSpan.FromSeconds(30));
        (await browser.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ActiveRowsAsync(owner.UserId)).Single().LastSeenAt.ShouldBe(signedInAt, "a request inside the minute writes nothing");

        App.Time.Advance(TimeSpan.FromSeconds(31));
        (await browser.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ActiveRowsAsync(owner.UserId)).Single().LastSeenAt.ShouldBe(App.Time.GetUtcNow(), TimeSpan.FromMilliseconds(5));
    }

    [Theory]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0", "Edge on Windows")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36", "Chrome on Mac")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1", "Safari on iPhone")]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64; rv:130.0) Gecko/20100101 Firefox/130.0", "Firefox on Linux")]
    [InlineData("curl/8.0", "Web browser")]
    public async Task A_browser_is_listed_by_a_friendly_name_made_from_what_it_says_it_is(string userAgent, string expected)
    {
        var browser = App.NewBrowser();
        browser.Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        await browser.SetUpOwnerAsync(await App.IssueCodeAsync(CollectionMaxxing.Domain.Accounts.SetupCodePurpose.Setup));

        (await WebSessionsAsync(browser)).Single().GetProperty("deviceName").GetString().ShouldBe(expected);
    }

    // ---- helpers --------------------------------------------------------------------------------------------------------

    private async Task<WebClient> SecondBrowserOfAsync(WebClient existing)
    {
        var another = App.NewBrowser();
        another.Identity = existing.Identity;
        (await another.LoginAsync()).Headers.Location?.ToString().ShouldBe("/");
        return another;
    }

    private static async Task<HttpResponseMessage> DeleteMemberAccountAsync(WebClient member)
    {
        await member.ReauthAsync();
        return await member.SendAsync(HttpMethod.Delete, "/api/v1/me", null);
    }

    private static async Task<List<JsonElement>> WebSessionsAsync(WebClient browser)
    {
        var response = await browser.GetAsync("/api/v1/me/web-sessions");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return [.. (await ReadAsync(response)).GetProperty("items").EnumerateArray()];
    }

    private static void Replace(ClaimsIdentity identity, string type, string value)
    {
        foreach (var claim in identity.FindAll(type).ToList())
        {
            identity.RemoveClaim(claim);
        }

        identity.AddClaim(new Claim(type, value));
    }

    private ISecureDataFormat<AuthenticationTicket> Format =>
        App.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme).TicketDataFormat;

    private AuthenticationTicket Unseal(string cookie) => Format.Unprotect(cookie) ?? throw new InvalidOperationException("The cookie could not be read.");

    /// <summary>Opens a real session cookie, changes the claims the way an older or doctored one would differ, and seals it again with the server's own keys.</summary>
    private string Reseal(string cookie, Action<ClaimsIdentity> change)
    {
        var ticket = Unseal(cookie);
        var identity = (ClaimsIdentity)ticket.Principal.Identity!;
        change(identity);
        return Format.Protect(new AuthenticationTicket(new ClaimsPrincipal(identity), ticket.Properties, ticket.AuthenticationScheme));
    }

    private sealed record WebSessionRow(Guid Id, Guid UserId, bool Revoked, DateTimeOffset LastSeenAt);

    private async Task<List<WebSessionRow>> RowsAsync(Guid userId)
    {
        await using var connection = new NpgsqlConnection(Db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("select id, user_id, revoked_at is not null, last_seen_at from web_sessions where user_id = @user order by created_at", connection);
        command.Parameters.AddWithValue("user", userId);
        var rows = new List<WebSessionRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new WebSessionRow(reader.GetGuid(0), reader.GetGuid(1), reader.GetBoolean(2), reader.GetFieldValue<DateTimeOffset>(3)));
        }

        return rows;
    }

    private async Task<List<WebSessionRow>> ActiveRowsAsync(Guid userId) => [.. (await RowsAsync(userId)).Where(r => !r.Revoked)];
}
