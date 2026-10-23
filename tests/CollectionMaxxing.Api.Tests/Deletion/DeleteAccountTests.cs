using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;

using WebClient = CollectionMaxxing.Api.Tests.Auth.WebClient;

namespace CollectionMaxxing.Api.Tests.Deletion;

// LOCKED acceptance tests of LIB-2c (issue #67), deleting an account: the proof that it's you, the revocation, who may delete what.
// Do not edit, weaken, skip or delete: add new tests instead. AUTH-1 (#228) moved the proof from a passkey assertion made for the request
// to a sign-in from the last five minutes (contract addendum 2), and phones from paired devices to Auth0 tokens.
public class DeleteAccountTests(ApiFixture api) : DeletionTest(api)
{
    private const string ReauthType = "https://collectionmaxxing.app/problems/reauth_required";

    private static async Task ShouldBeReauthRequiredAsync(HttpResponseMessage response)
    {
        await ShouldBeProblemAsync(response, HttpStatusCode.Unauthorized);
        var problem = await ReadAsync(response);
        problem.GetProperty("type").GetString().ShouldBe(ReauthType);
        problem.GetProperty("code").GetString().ShouldBe("reauth_required");
        problem.GetProperty("reauth").GetBoolean().ShouldBeTrue();
    }

    private async Task ShouldBeUntouchedAsync(WebClient browser, Guid userId)
    {
        (await browser.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK, "the account still works");
        (await UserRowAsync(userId)).DeletedAt.ShouldBeNull();
        (await PurgeJobsAsync()).ShouldBeEmpty();
    }

    // ---- 1. without a recent sign-in: 401 and nothing changes ------------------------------------------------------------

    [Fact]
    public async Task Without_a_sign_in_from_the_last_five_minutes_the_call_is_401_reauth_required()
    {
        var (browser, owner) = await SetUpOwnerAsync();
        var phone = PhoneOf(browser);
        App.Time.Advance(TimeSpan.FromMinutes(6));

        var response = await browser.SendAsync(HttpMethod.Delete, "/api/v1/me", null);

        await ShouldBeReauthRequiredAsync(response);
        await ShouldBeUntouchedAsync(browser, owner.UserId);
        (await phone.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_reauth_that_was_too_long_ago_does_not_count()
    {
        var (browser, owner) = await SetUpOwnerAsync();
        (await browser.ReauthAsync(App.Time.GetUtcNow().AddMinutes(-6))).Headers.Location?.ToString().ShouldBe("/?error=reauth_failed");

        App.Time.Advance(TimeSpan.FromMinutes(6));
        var response = await browser.SendAsync(HttpMethod.Delete, "/api/v1/me", null);

        await ShouldBeReauthRequiredAsync(response);
        await ShouldBeUntouchedAsync(browser, owner.UserId);
    }

    [Fact]
    public async Task A_reauth_as_somebody_else_does_not_count_for_this_account()
    {
        var (browser, owner) = await SetUpOwnerAsync();
        var (sam, samAccount) = await AddMemberAsync(browser, owner.LibraryId!.Value, "viewer", "Sam");
        App.Time.Advance(TimeSpan.FromMinutes(6));

        // The owner's browser goes to Auth0 to confirm, and comes back as Sam.
        (await browser.SignInAsync("/account/login?reauth=1", sam.Identity)).Headers.Location?.ToString().ShouldBe("/?error=reauth_failed");
        await ShouldBeReauthRequiredAsync(await browser.SendAsync(HttpMethod.Delete, "/api/v1/me", null));

        await ShouldBeUntouchedAsync(browser, owner.UserId);
        (await UserRowAsync(samAccount.UserId)).DeletedAt.ShouldBeNull();
    }

    // ---- 2. afterwards: every way in is closed ---------------------------------------------------------------------------

    [Fact]
    public async Task After_deletion_the_very_next_call_from_every_device_and_the_web_cookie_is_401_refresh_fails_and_sign_in_is_refused()
    {
        var (browser, owner) = await SetUpOwnerAsync();
        var libraryId = owner.LibraryId!.Value;
        var secondBrowser = App.NewBrowser(browser.Identity);
        (await secondBrowser.LoginAsync()).Headers.Location?.ToString().ShouldBe("/");
        var phone = PhoneOf(browser);
        var tablet = PhoneOf(browser);
        var (inviteToken, _) = await CreateInviteAsync(browser, libraryId, "viewer", "Guest");
        foreach (var web in new[] { browser, secondBrowser })
        {
            (await web.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await phone.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var deleted = await DeleteAccountAsync(browser);

        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent, await BodyOfAsync(deleted));

        // The very next call: both web sessions, and both phones' access tokens (which still have most of an hour left).
        (await browser.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await secondBrowser.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await secondBrowser.GetAsync($"/api/v1/libraries/{libraryId}")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await phone.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await tablet.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Signing in again at Auth0 is refused: no cookie.
        secondBrowser.ClearCookies();
        (await secondBrowser.LoginAsync()).Headers.Location?.ToString().ShouldBe("/login?error=signin_failed");
        secondBrowser.HasSessionCookie.ShouldBeFalse();

        // The open invite died with the account.
        var (guest, joined) = await JoinAsync(inviteToken, "Guest");
        joined.Headers.Location?.ToString().ShouldBe("/login?error=invite_invalid");
        guest.HasSessionCookie.ShouldBeFalse();

        var row = await UserRowAsync(owner.UserId);
        row.DeletedAt.ShouldNotBeNull();
        (await PurgeJobsAsync()).ShouldHaveSingleItem().RunAfter.ShouldBe(row.DeletedAt!.Value.AddDays(30));
    }

    [Fact]
    public async Task A_member_can_delete_their_own_account_and_the_library_carries_on()
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var libraryId = ownerAccount.LibraryId!.Value;
        var (sam, samAccount) = await AddMemberAsync(owner, libraryId, "editor", "Sam");
        var phone = PhoneOf(sam);

        var deleted = await DeleteAccountAsync(sam);

        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent, await BodyOfAsync(deleted));
        (await sam.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await phone.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await UserRowAsync(samAccount.UserId)).DeletedAt.ShouldNotBeNull();
        (await owner.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await UserRowAsync(ownerAccount.UserId)).DeletedAt.ShouldBeNull();
    }

    // ---- 3. only your own account ------------------------------------------------------------------------------------------

    [Fact]
    public async Task There_is_no_way_to_name_another_users_id()
    {
        var (browser, owner) = await SetUpOwnerAsync();
        var (sam, samAccount) = await AddMemberAsync(browser, owner.LibraryId!.Value, "editor", "Sam");

        // No route takes an id...
        foreach (var url in new[] { $"/api/v1/me/{samAccount.UserId}", $"/api/v1/users/{samAccount.UserId}", $"/api/v1/accounts/{samAccount.UserId}" })
        {
            var response = await browser.SendAsync(HttpMethod.Delete, url, new { });
            ((int)response.StatusCode).ShouldBeInRange(400, 499, url);
        }

        // ...and an id in the body or the query string is ignored: the signed-in user is the one who goes.
        await browser.ReauthAsync();
        var deleted = await browser.SendAsync(HttpMethod.Delete, $"/api/v1/me?userId={samAccount.UserId}", new { userId = samAccount.UserId, id = samAccount.UserId });

        deleted.StatusCode.ShouldBe(HttpStatusCode.Conflict); // the owner has a member, so nothing at all happens...
        (await UserRowAsync(samAccount.UserId)).DeletedAt.ShouldBeNull();
        (await UserRowAsync(owner.UserId)).DeletedAt.ShouldBeNull();

        // ...and Sam, deleting, removes Sam and never the owner.
        (await DeleteAccountAsync(sam)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await UserRowAsync(samAccount.UserId)).DeletedAt.ShouldNotBeNull();
        (await UserRowAsync(owner.UserId)).DeletedAt.ShouldBeNull();
        (await browser.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ---- 4. an owner with members must hand over first ------------------------------------------------------------------

    [Fact]
    public async Task The_owner_of_a_library_with_other_members_gets_409_and_nothing_is_deleted_or_revoked()
    {
        var (browser, owner) = await SetUpOwnerAsync();
        var libraryId = owner.LibraryId!.Value;
        var (sam, samAccount) = await AddMemberAsync(browser, libraryId, "editor", "Sam");
        var (_, inviteJson) = await CreateInviteAsync(browser, libraryId, "viewer", "Guest");
        var phone = PhoneOf(browser);
        var seeded = await SeedLibraryAsync(libraryId, owner.UserId);

        var response = await DeleteAccountAsync(browser);

        await ShouldBeProblemAsync(response, HttpStatusCode.Conflict);
        var problem = await ReadAsync(response);
        problem.GetProperty("code").GetString().ShouldBe("library_has_members");
        problem.GetProperty("detail").GetString()!.ShouldContain("transfer");
        await ShouldBeUntouchedAsync(browser, owner.UserId);
        (await phone.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await sam.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await UserRowAsync(samAccount.UserId)).DeletedAt.ShouldBeNull();
        (await CountMembersAsync(libraryId)).ShouldBe(2);
        (await RowsOfLibraryAsync(libraryId)).ShouldNotBeEmpty();
        await using var context = Db.CreateContext();
        (await context.LibraryInvites.IgnoreQueryFilters().CountAsync(i => i.RevokedAt != null)).ShouldBe(0, "the invites are still open");
        inviteJson.GetProperty("invite").GetProperty("id").GetGuid().ShouldNotBe(Guid.Empty);
        (await context.Items.IgnoreQueryFilters().CountAsync(i => i.Id == seeded.ItemId)).ShouldBe(1);
    }

    [Fact]
    public async Task Once_the_members_are_gone_the_owner_can_delete()
    {
        var (browser, owner) = await SetUpOwnerAsync();
        var libraryId = owner.LibraryId!.Value;
        var (_, sam) = await AddMemberAsync(browser, libraryId, "viewer", "Sam");
        (await DeleteAccountAsync(browser)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await browser.SendAsync(HttpMethod.Delete, $"{MembersUrl(libraryId)}/{sam.UserId}", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var response = await DeleteAccountAsync(browser);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await BodyOfAsync(response));
        (await browser.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
