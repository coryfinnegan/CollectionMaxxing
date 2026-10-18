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

        var options = await joiner.PostAsync(
            "/account/passkey/creation-options",
            new { inviteToken = token, displayName = "Sam", role = "owner", libraryId, isAdmin = true, roles = new[] { "admin" } });
        options.StatusCode.ShouldBe(HttpStatusCode.OK, await options.Content.ReadAsStringAsync());
        var creation = (await options.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("options");
        var credential = JsonDocument.Parse(joiner.Authenticator.Create(creation.GetRawText())).RootElement.Clone();
        var registered = await joiner.PostAsync(
            $"/account/passkey/register?invite={Uri.EscapeDataString(token)}&role=owner",
            new { credential, name = "Sam's passkey", role = "owner", libraryId, userId = account.UserId, isAdmin = true, roles = new[] { "admin" } });

        registered.StatusCode.ShouldBe(HttpStatusCode.Created, await registered.Content.ReadAsStringAsync());
        var sam = await registered.Content.ReadFromJsonAsync<WebClient.SignedIn>(Json);
        (await RoleOfAsync(libraryId, sam!.UserId)).ShouldBe("viewer");
        sam.UserId.ShouldNotBe(account.UserId);
        (await CountOwnersAsync()).ShouldBe(1);
        (await joiner.GetAsync("/api/admin/jobs")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await joiner.PostAsync($"/api/v1/libraries/{libraryId}/tags", new { name = "Not for viewers" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_invite_link_cannot_be_used_to_add_a_passkey_to_an_existing_account()
    {
        var (owner, account) = await SetUpOwnerAsync();
        var libraryId = account.LibraryId!.Value;
        var (token, _) = await CreateInviteAsync(owner, libraryId, "viewer");
        var stranger = App.NewBrowser();

        // The invite plans a NEW user; there is no field that names an existing one.
        var options = await stranger.PostAsync("/account/passkey/creation-options", new { inviteToken = token, displayName = "Owner", userId = account.UserId, setupCode = (string?)null });
        var registered = await stranger.RegisterAsync(options, invite: token);

        registered.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await registered.Content.ReadFromJsonAsync<WebClient.SignedIn>(Json))!.UserId.ShouldNotBe(account.UserId);
        (await CountUsersAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task A_pairing_is_always_for_the_caller_whatever_user_the_request_names()
    {
        var (owner, account) = await SetUpOwnerAsync();
        var libraryId = account.LibraryId!.Value;
        var (member, memberAccount) = await AddMemberAsync(owner, libraryId, "viewer");

        var offered = await member.SendAsync(HttpMethod.Post, $"/api/v1/me/devices/pair?userId={account.UserId}", new { userId = account.UserId, forUser = account.UserId });
        offered.StatusCode.ShouldBe(HttpStatusCode.Created, await offered.Content.ReadAsStringAsync());
        var offer = (await offered.Content.ReadFromJsonAsync<PairingOfferShape>(Json))!;
        var device = await ReadDeviceAsync(await RedeemTokenAsync(offer.Token, "Mallory's phone"));

        var me = await ReadAsync(await AsDeviceAsync(HttpMethod.Get, "/api/v1/me", device.AccessToken));
        me.GetProperty("id").GetGuid().ShouldBe(memberAccount.UserId);
        me.GetProperty("id").GetGuid().ShouldNotBe(account.UserId);
        (await AsDeviceAsync(HttpMethod.Get, $"{MembersUrl(libraryId)}/{account.UserId}", device.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        (await AsDeviceAsync(HttpMethod.Post, $"/api/v1/libraries/{libraryId}/tags", device.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_phone_paired_by_a_member_is_gone_with_the_member_and_the_owners_phones_are_not_touched()
    {
        var (owner, account) = await SetUpOwnerAsync();
        var libraryId = account.LibraryId!.Value;
        var ownerPhone = await PairAsync(owner, "Owner phone");
        var (member, memberAccount) = await AddMemberAsync(owner, libraryId, "editor");
        var memberPhone = await PairAsync(member, "Member phone", App.NewBrowser().Http);

        (await owner.SendAsync(HttpMethod.Delete, $"{MembersUrl(libraryId)}/{memberAccount.UserId}", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await AsDeviceAsync(HttpMethod.Get, "/api/v1/me", memberPhone.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AsDeviceAsync(HttpMethod.Get, "/api/v1/me", ownerPhone.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_removed_member_cannot_get_back_in_with_the_invite_they_used_or_with_their_old_passkey()
    {
        var (owner, account) = await SetUpOwnerAsync();
        var libraryId = account.LibraryId!.Value;
        var (token, _) = await CreateInviteAsync(owner, libraryId, "editor");
        var (member, registered) = await JoinAsync(token);
        var memberAccount = (await registered.Content.ReadFromJsonAsync<WebClient.SignedIn>(Json))!;
        (await owner.SendAsync(HttpMethod.Delete, $"{MembersUrl(libraryId)}/{memberAccount.UserId}", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // The spent link is spent for good.
        var again = App.NewBrowser();
        (await StartJoinAsync(again, token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await again.GetAsync($"/api/v1/invites/{token}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // The old passkey signs in nobody.
        member.ClearCookies();
        (await member.LoginAsync()).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await member.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private sealed record PairingOfferShape(string ServerUrl, string Token, string Code, DateTimeOffset ExpiresAt);
}
