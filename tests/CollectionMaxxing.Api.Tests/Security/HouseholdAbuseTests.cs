using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Invites;
using CollectionMaxxing.Api.Tests.Support;
using WebClient = CollectionMaxxing.Api.Tests.Auth.WebClient;

namespace CollectionMaxxing.Api.Tests.Security;

/// <summary>
/// What a family member who turns hostile, or a stranger holding an invite link, tries against the household rules of docs/07: the owner is
/// fixed, an invite makes exactly the role it was made with, and nobody pairs a phone to someone else's account. Real passkey sign-in throughout.
/// </summary>
public class HouseholdAbuseTests(ApiFixture api) : InviteTest(api)
{
    [Theory]
    [InlineData("editor")]
    [InlineData("viewer")]
    public async Task A_member_cannot_remove_or_demote_the_owner_or_touch_the_household(string role)
    {
        var (owner, account) = await SetUpOwnerAsync();
        var libraryId = account.LibraryId!.Value;
        var (member, _) = await AddMemberAsync(owner, libraryId, role);

        var demote = await member.SendAsync(HttpMethod.Patch, $"{MembersUrl(libraryId)}/{account.UserId}", new { role = "viewer" });
        var remove = await member.SendAsync(HttpMethod.Delete, $"{MembersUrl(libraryId)}/{account.UserId}", null);
        var invite = await member.PostAsync(InvitesUrl(libraryId), new { name = "Mallory", role = "editor" });
        var rename = await member.SendAsync(HttpMethod.Patch, $"/api/v1/libraries/{libraryId}", new { name = "Mine now" });

        foreach (var response in new[] { demote, remove, invite, rename })
        {
            await ShouldBeProblemAsync(response, HttpStatusCode.Forbidden);
        }

        (await ListMembersAsync(owner, libraryId)).Select(m => m.GetProperty("role").GetString()).ShouldBe(["owner", role]);
        (await owner.GetAsync($"/api/v1/libraries/{libraryId}")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_member_cannot_make_themselves_the_owner_by_patching_their_own_row()
    {
        var (owner, account) = await SetUpOwnerAsync();
        var libraryId = account.LibraryId!.Value;
        var (member, memberAccount) = await AddMemberAsync(owner, libraryId, "editor");

        var response = await member.SendAsync(HttpMethod.Patch, $"{MembersUrl(libraryId)}/{memberAccount.UserId}", new { role = "owner" });

        await ShouldBeProblemAsync(response, HttpStatusCode.Forbidden);
        (await RoleOfAsync(libraryId, memberAccount.UserId)).ShouldBe("editor");
        (await CountOwnersAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Joining_with_an_invite_for_viewer_cannot_be_turned_into_a_bigger_role_by_what_the_joiner_sends()
    {
        var (owner, account) = await SetUpOwnerAsync();
        var libraryId = account.LibraryId!.Value;
        var (token, _) = await CreateInviteAsync(owner, libraryId, "viewer");
        var joiner = App.NewBrowser();

        var registered = await joiner.SignInAsync(
            $"/account/login?invite={Uri.EscapeDataString(token)}&role=owner&libraryId={libraryId}&userId={account.UserId}&isAdmin=true",
            tweak: claims =>
            {
                claims["role"] = "owner";
                claims["roles"] = new[] { "admin" };
                claims["http://schemas.microsoft.com/ws/2008/06/identity/claims/role"] = "admin";
                claims["user_id"] = account.UserId.ToString();
            });

        registered.Headers.Location?.ToString().ShouldBe("/");

        // ONB-1: joining is POST /api/v1/invites:accept once signed in; the body can't name a role, a library or a user either.
        var accepted = await joiner.PostAsync("/api/v1/invites:accept", new { invite = token, role = "owner", libraryId, userId = account.UserId, isAdmin = true });
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync(accepted)).GetProperty("role").GetString().ShouldBe("viewer");
        var sam = await joiner.MeAsync();
        (await RoleOfAsync(libraryId, sam.UserId)).ShouldBe("viewer");
        sam.UserId.ShouldNotBe(account.UserId);
        (await CountOwnersAsync()).ShouldBe(1);
        (await joiner.GetAsync("/api/admin/jobs")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await joiner.PostAsync($"/api/v1/libraries/{libraryId}/tags", new { name = "Not for viewers" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_invite_link_cannot_be_used_to_link_an_identity_to_an_existing_account()
    {
        var (owner, account) = await SetUpOwnerAsync();
        var libraryId = account.LibraryId!.Value;
        var (token, _) = await CreateInviteAsync(owner, libraryId, "viewer");
        var stranger = App.NewBrowser();

        // Linking needs a session of one's own, so this goes nowhere.
        (await stranger.SignInAsync($"/account/login?invite={Uri.EscapeDataString(token)}&userId={account.UserId}&link=1")).Headers.Location?.ToString().ShouldBe("/login");

        // A first sign-in makes a NEW user (ONB-1), and accepting the invite joins that user; nothing in the request can name an existing one.
        var registered = await stranger.SignInAsync($"/account/login?invite={Uri.EscapeDataString(token)}&userId={account.UserId}");
        var accepted = await stranger.PostAsync("/api/v1/invites:accept", new { invite = token, userId = account.UserId });

        registered.Headers.Location?.ToString().ShouldBe("/");
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await stranger.MeAsync()).UserId.ShouldNotBe(account.UserId);
        (await CountUsersAsync()).ShouldBe(2);
        (await CountLoginsAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task A_phone_is_always_the_person_its_token_is_for_whatever_user_the_request_names()
    {
        var (owner, account) = await SetUpOwnerAsync();
        var libraryId = account.LibraryId!.Value;
        var (member, memberAccount) = await AddMemberAsync(owner, libraryId, "viewer");
        var token = App.Auth0.AccessToken(member.Identity.Subject, claims =>
        {
            claims["user_id"] = account.UserId.ToString();
            claims["http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier"] = account.UserId.ToString();
            claims["http://schemas.microsoft.com/ws/2008/06/identity/claims/role"] = "admin";
        });

        var me = await ReadAsync(await AsDeviceAsync(HttpMethod.Get, $"/api/v1/me?userId={account.UserId}", token));
        me.GetProperty("id").GetGuid().ShouldBe(memberAccount.UserId);
        me.GetProperty("id").GetGuid().ShouldNotBe(account.UserId);
        (await AsDeviceAsync(HttpMethod.Get, "/api/admin/jobs", token)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AsDeviceAsync(HttpMethod.Post, $"/api/v1/libraries/{libraryId}/tags", token)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_removed_member_cannot_get_back_in_with_the_invite_they_used_or_with_their_old_passkey()
    {
        var (owner, account) = await SetUpOwnerAsync();
        var libraryId = account.LibraryId!.Value;
        var (token, _) = await CreateInviteAsync(owner, libraryId, "editor");
        var (member, registered) = await JoinAsync(token);
        registered.StatusCode.ShouldBe(HttpStatusCode.OK);
        var memberAccount = await member.MeAsync();
        (await owner.SendAsync(HttpMethod.Delete, $"{MembersUrl(libraryId)}/{memberAccount.UserId}", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // The spent link is spent for good, for them and for anyone else.
        await ShouldBeInvalidInviteAsync(await member.JoinAsync(token));
        var again = App.NewBrowser();
        await ShouldBeInvalidInviteAsync(await again.JoinAsync(token));
        (await again.GetAsync($"/api/v1/invites/{token}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // ONB-1: their sign-in still works (removal ends only the membership), but it reaches nothing of this library.
        member.ClearCookies();
        (await member.LoginAsync()).Headers.Location?.ToString().ShouldBe("/");
        (await member.GetAsync($"/api/v1/libraries/{libraryId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await AsDeviceAsync(HttpMethod.Get, $"/api/v1/libraries/{libraryId}/items", App.Auth0.AccessToken(member.Identity.Subject))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CountMembersAsync(libraryId)).ShouldBe(1);
    }

}
