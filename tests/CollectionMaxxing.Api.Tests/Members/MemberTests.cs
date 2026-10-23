using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Auth;
using CollectionMaxxing.Api.Tests.Invites;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

using WebClient = CollectionMaxxing.Api.Tests.Auth.WebClient;

namespace CollectionMaxxing.Api.Tests.Members;

// LOCKED acceptance tests of ID-3a (issue #64), members: removal, role changes, who may see what. Do not edit, weaken, skip or delete: add new tests instead.
public class MemberTests(ApiFixture api) : InviteTest(api)
{
    private static string ItemsUrl(Guid libraryId) => $"/api/v1/libraries/{libraryId}/items";

    private static Task<HttpResponseMessage> AddItemAsync(WebClient browser, Guid libraryId, Guid releaseId) =>
        browser.SendAsync(
            HttpMethod.Post,
            ItemsUrl(libraryId),
            new { releaseId, status = "owned" },
            tweak: request => request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString()));

    [Fact]
    public async Task Removing_a_member_makes_their_very_next_api_call_401_and_their_refresh_fail()
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var libraryId = ownerAccount.LibraryId!.Value;
        var (sam, samAccount) = await AddMemberAsync(owner, libraryId, "editor", "Sam");
        var phone = await SignInPhoneAsync(sam, "Sam's iPhone");
        (await sam.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await AsDeviceAsync(HttpMethod.Get, "/api/v1/me", phone.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var removed = await owner.SendAsync(HttpMethod.Delete, $"{MembersUrl(libraryId)}/{samAccount.UserId}", null);

        removed.StatusCode.ShouldBe(HttpStatusCode.NoContent, await BodyOfAsync(removed));

        // The web session and the phone's access token (with nine minutes left) are refused at once...
        (await sam.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AsDeviceAsync(HttpMethod.Get, "/api/v1/me", phone.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // ...and the account is no longer a way in, with the old cookie or a fresh Auth0 sign-in.
        (await sam.GetAsync($"/api/v1/libraries/{libraryId}")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        sam.ClearCookies();
        (await sam.LoginAsync()).Headers.Location?.ToString().ShouldBe("/login?error=signin_failed");

        // The owner's own session is untouched, and the member is off the list.
        (await ListMembersAsync(owner, libraryId)).Select(m => m.GetProperty("userId").GetGuid()).ShouldBe([ownerAccount.UserId]);
        (await CountMembersAsync(libraryId)).ShouldBe(1);
    }

    [Fact]
    public async Task A_removed_members_items_remain_with_added_by_intact()
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var libraryId = ownerAccount.LibraryId!.Value;
        var (sam, samAccount) = await AddMemberAsync(owner, libraryId, "editor", "Sam");
        var releaseId = await TestData.CreateReleaseAsync(Db);
        var added = await AddItemAsync(sam, libraryId, releaseId);
        added.StatusCode.ShouldBe(HttpStatusCode.Created, await BodyOfAsync(added));
        var itemId = (await ReadAsync(added)).GetProperty("id").GetGuid();
        var before = await ReadAsync(await owner.GetAsync($"{ItemsUrl(libraryId)}/{itemId}"));
        before.GetProperty("item").GetProperty("createdByName").GetString().ShouldBe("Sam");

        (await owner.SendAsync(HttpMethod.Delete, $"{MembersUrl(libraryId)}/{samAccount.UserId}", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var after = await ReadAsync(await owner.GetAsync($"{ItemsUrl(libraryId)}/{itemId}"));
        after.GetProperty("item").GetProperty("createdBy").GetGuid().ShouldBe(samAccount.UserId);
        after.GetProperty("item").GetProperty("createdByName").GetString().ShouldBe("Sam");
        await using var context = Db.CreateContext();
        (await context.Items.IgnoreQueryFilters().CountAsync(i => i.Id == itemId && i.DeletedAt == null)).ShouldBe(1);
        (await context.Users.CountAsync(u => u.Id == samAccount.UserId)).ShouldBe(1, "the account is deactivated, not erased, so the name stays");
    }

    [Fact]
    public async Task The_owner_cannot_be_removed()
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var libraryId = ownerAccount.LibraryId!.Value;

        var response = await owner.SendAsync(HttpMethod.Delete, $"{MembersUrl(libraryId)}/{ownerAccount.UserId}", null);

        await ShouldBeProblemAsync(response, HttpStatusCode.Conflict);
        (await owner.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CountOwnersAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task The_owner_cannot_be_demoted_and_nobody_can_be_made_owner()
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var libraryId = ownerAccount.LibraryId!.Value;
        var (_, sam) = await AddMemberAsync(owner, libraryId, "editor");

        var demote = await owner.SendAsync(HttpMethod.Patch, $"{MembersUrl(libraryId)}/{ownerAccount.UserId}", new { role = "viewer" });
        var promote = await owner.SendAsync(HttpMethod.Patch, $"{MembersUrl(libraryId)}/{sam.UserId}", new { role = "owner" });

        await ShouldBeProblemAsync(demote, HttpStatusCode.Conflict);
        promote.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await BodyOfAsync(promote));
        (await RoleOfAsync(libraryId, ownerAccount.UserId)).ShouldBe("owner");
        (await RoleOfAsync(libraryId, sam.UserId)).ShouldBe("editor");
    }

    [Fact]
    public async Task A_role_change_is_effective_immediately_a_demoted_editors_next_write_is_403()
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var libraryId = ownerAccount.LibraryId!.Value;
        var (sam, samAccount) = await AddMemberAsync(owner, libraryId, "editor");
        var shelves = $"/api/v1/libraries/{libraryId}/locations";
        (await sam.PostAsync(shelves, new { name = "Shelf A" })).StatusCode.ShouldBe(HttpStatusCode.Created);

        var demoted = await owner.SendAsync(HttpMethod.Patch, $"{MembersUrl(libraryId)}/{samAccount.UserId}", new { role = "viewer" });

        demoted.StatusCode.ShouldBe(HttpStatusCode.OK, await BodyOfAsync(demoted));
        (await ReadAsync(demoted)).GetProperty("role").GetString().ShouldBe("viewer");
        (await sam.PostAsync(shelves, new { name = "Shelf B" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await sam.GetAsync(shelves)).StatusCode.ShouldBe(HttpStatusCode.OK); // still a member who can read
    }

    [Fact]
    public async Task A_promoted_viewers_next_write_succeeds_without_signing_in_again()
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var libraryId = ownerAccount.LibraryId!.Value;
        var (sam, samAccount) = await AddMemberAsync(owner, libraryId, "viewer");
        var shelves = $"/api/v1/libraries/{libraryId}/locations";
        (await sam.PostAsync(shelves, new { name = "Shelf A" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await owner.SendAsync(HttpMethod.Patch, $"{MembersUrl(libraryId)}/{samAccount.UserId}", new { role = "editor" })).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await sam.PostAsync(shelves, new { name = "Shelf A" })).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_viewer_reads_names_and_roles_only_and_cannot_see_or_make_invites()
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var libraryId = ownerAccount.LibraryId!.Value;
        var (viewer, viewerAccount) = await AddMemberAsync(owner, libraryId, "viewer", "Alex");
        var (token, _) = await CreateInviteAsync(owner, libraryId, "editor", "Jordan");
        await SignInPhoneAsync(viewer, "Alex's iPhone");

        var members = await ListMembersAsync(viewer, libraryId);

        members.Select(m => m.GetProperty("displayName").GetString()).Order().ShouldBe(["Alex", "Owner"]);
        members.Single(m => m.GetProperty("userId").GetGuid() == viewerAccount.UserId).GetProperty("role").GetString().ShouldBe("viewer");
        foreach (var member in members)
        {
            member.TryGetProperty("devices", out var devices).ShouldBeTrue("the shape is the same for everyone");
            devices.ValueKind.ShouldBe(JsonValueKind.Null, "a viewer is told no devices");
            member.GetProperty("lastSeenAt").ValueKind.ShouldBe(JsonValueKind.Null);
        }

        (await BodyOfAsync(await viewer.GetAsync(MembersUrl(libraryId)))).ShouldNotContain(token);
        (await viewer.GetAsync(InvitesUrl(libraryId))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await InviteAsync(viewer, libraryId)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await viewer.SendAsync(HttpMethod.Patch, $"{MembersUrl(libraryId)}/{ownerAccount.UserId}", new { role = "viewer" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await viewer.SendAsync(HttpMethod.Delete, $"{MembersUrl(libraryId)}/{viewerAccount.UserId}", null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_owner_also_sees_each_members_devices_and_when_they_were_last_seen()
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var libraryId = ownerAccount.LibraryId!.Value;
        var (sam, samAccount) = await AddMemberAsync(owner, libraryId, "editor", "Sam");
        await SignInPhoneAsync(sam, "Sam's iPhone");
        await AddMemberAsync(owner, libraryId, "viewer", "Alex");

        var members = await ListMembersAsync(owner, libraryId);

        var samRow = members.Single(m => m.GetProperty("userId").GetGuid() == samAccount.UserId);
        // AUTH-1 (#228): phones sign in with Auth0 now and are registered with the server by AUTH-2 (#229); until then nobody has one listed.
        samRow.GetProperty("devices").GetArrayLength().ShouldBe(0);
        samRow.GetProperty("lastSeenAt").ValueKind.ShouldBe(JsonValueKind.Null);
        var alexRow = members.Single(m => m.GetProperty("displayName").GetString() == "Alex");
        alexRow.GetProperty("devices").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Removing_someone_who_is_not_a_member_is_404_and_removing_twice_is_404()
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var libraryId = ownerAccount.LibraryId!.Value;
        var (_, sam) = await AddMemberAsync(owner, libraryId, "viewer");

        (await owner.SendAsync(HttpMethod.Delete, $"{MembersUrl(libraryId)}/{Guid.NewGuid()}", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await owner.SendAsync(HttpMethod.Delete, $"{MembersUrl(libraryId)}/{sam.UserId}", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.SendAsync(HttpMethod.Delete, $"{MembersUrl(libraryId)}/{sam.UserId}", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}

// LOCKED acceptance tests of ID-3a (issue #64): who may invite and manage members, and keeping libraries apart. Do not edit, weaken, skip or delete.
public class MemberAccessTests(ApiFixture api) : ApiTest(api)
{
    private static string Members(Guid libraryId) => $"/api/v1/libraries/{libraryId}/members";

    private static string Invites(Guid libraryId) => $"/api/v1/libraries/{libraryId}/invites";

    private static HttpRequestMessage Request(HttpMethod method, string url, object? body = null) =>
        new(method, url) { Content = body is null ? null : System.Net.Http.Json.JsonContent.Create(body) };

    [Fact]
    public async Task Editors_and_viewers_get_403_on_everything_an_owner_manages()
    {
        var library = await CreateLibraryAsync();
        var invite = Guid.NewGuid();

        foreach (var role in new[] { Domain.Libraries.LibraryRole.Editor, Domain.Libraries.LibraryRole.Viewer })
        {
            using var client = ClientFor(library, role);
            var calls = new[]
            {
                Request(HttpMethod.Get, Invites(library.Id)),
                Request(HttpMethod.Post, Invites(library.Id), new { name = "Sam", role = "viewer" }),
                Request(HttpMethod.Delete, $"{Invites(library.Id)}/{invite}"),
                Request(HttpMethod.Patch, $"{Members(library.Id)}/{library.ViewerId}", new { role = "editor" }),
                Request(HttpMethod.Delete, $"{Members(library.Id)}/{library.ViewerId}"),
            };
            foreach (var call in calls)
            {
                using var response = await client.SendAsync(call);
                response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{role}: {call.Method} {call.RequestUri}");
            }
        }

        await using var context = Db.CreateContext();
        (await context.LibraryInvites.IgnoreQueryFilters().CountAsync()).ShouldBe(0);
        (await context.LibraryMembers.IgnoreQueryFilters().CountAsync(m => m.LibraryId == library.Id)).ShouldBe(3);
    }

    [Fact]
    public async Task The_owner_of_one_library_cannot_list_invite_to_patch_or_remove_members_of_another()
    {
        var mine = await CreateLibraryAsync();
        var theirs = await CreateLibraryAsync();
        var invite = Guid.NewGuid();
        using var client = Api.CreateClient(mine.OwnerId);

        var calls = new[]
        {
            Request(HttpMethod.Get, Members(theirs.Id)),
            Request(HttpMethod.Get, Invites(theirs.Id)),
            Request(HttpMethod.Post, Invites(theirs.Id), new { name = "Sam", role = "viewer" }),
            Request(HttpMethod.Delete, $"{Invites(theirs.Id)}/{invite}"),
            Request(HttpMethod.Patch, $"{Members(theirs.Id)}/{theirs.EditorId}", new { role = "viewer" }),
            Request(HttpMethod.Delete, $"{Members(theirs.Id)}/{theirs.EditorId}"),
        };
        foreach (var call in calls)
        {
            using var response = await client.SendAsync(call);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"{call.Method} {call.RequestUri}");
        }

        await using var context = Db.CreateContext();
        (await context.LibraryInvites.IgnoreQueryFilters().CountAsync()).ShouldBe(0);
        (await context.LibraryMembers.IgnoreQueryFilters().CountAsync(m => m.LibraryId == theirs.Id)).ShouldBe(3);
    }

    [Fact]
    public async Task An_owner_cannot_reach_a_member_of_another_library_through_their_own_library_route()
    {
        var mine = await CreateLibraryAsync();
        var theirs = await CreateLibraryAsync();
        using var client = Api.CreateClient(mine.OwnerId);

        using var patch = await client.SendAsync(Request(HttpMethod.Patch, $"{Members(mine.Id)}/{theirs.EditorId}", new { role = "viewer" }));
        using var remove = await client.SendAsync(Request(HttpMethod.Delete, $"{Members(mine.Id)}/{theirs.EditorId}"));
        using var revoke = await client.SendAsync(Request(HttpMethod.Delete, $"{Invites(mine.Id)}/{Guid.NewGuid()}"));

        patch.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        remove.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        revoke.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await using var context = Db.CreateContext();
        (await context.LibraryMembers.IgnoreQueryFilters().CountAsync(m => m.LibraryId == theirs.Id)).ShouldBe(3);
        (await context.Users.CountAsync(u => u.Id == theirs.EditorId && u.DeletedAt != null)).ShouldBe(0);
    }

    [Fact]
    public async Task An_owner_cannot_revoke_an_invite_of_another_library_through_their_own_route()
    {
        var mine = await CreateLibraryAsync();
        var theirs = await CreateLibraryAsync();
        using var theirOwner = Api.CreateClient(theirs.OwnerId);
        using var created = await theirOwner.PostAsync(Invites(theirs.Id), System.Net.Http.Json.JsonContent.Create(new { name = "Sam", role = "viewer" }));
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var inviteId = (await ReadJsonAsync(created)).GetProperty("invite").GetProperty("id").GetGuid();
        using var client = Api.CreateClient(mine.OwnerId);

        using var response = await client.SendAsync(Request(HttpMethod.Delete, $"{Invites(mine.Id)}/{inviteId}"));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await using var context = Db.CreateContext();
        (await context.LibraryInvites.IgnoreQueryFilters().SingleAsync()).RevokedAt.ShouldBeNull();
    }
}
