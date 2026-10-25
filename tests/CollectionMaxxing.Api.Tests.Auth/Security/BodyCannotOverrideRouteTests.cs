using System.Net;
using System.Text;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Security;

/// <summary>
/// Mass assignment (OWASP API3, docs/08 "Tampering"): the library comes from the route or from the stored resource, never from the body, and a
/// caller cannot write fields that are the server's to decide (<c>createdBy</c>, <c>role</c> on an invite's way in, <c>ownerLibraryId</c>, ids,
/// versions, timestamps). The caller below is the owner of one library and an editor of the other, so a body that names the other library is one the caller could
/// legitimately use through its own route: the test is that the body can't steer the write there.
/// </summary>
public class BodyCannotOverrideRouteTests(ApiFixture api) : ApiTest(api)
{
    private sealed record World(TestLibrary Route, TestLibrary Other, Guid Caller, Guid Stranger, Guid ItemId);

    private async Task<World> ArrangeAsync()
    {
        var route = await CreateLibraryAsync();
        var other = await CreateLibraryAsync();
        var stranger = await TestData.CreateUserAsync(Db);
        await using (var context = Db.CreateContext())
        {
            context.LibraryMembers.Add(new LibraryMember(other.Id, route.OwnerId, LibraryRole.Editor, DateTimeOffset.UtcNow));
            await context.SaveChangesAsync();
        }

        return new World(route, other, route.OwnerId, stranger, await TestData.CreateItemAsync(Db, route));
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private async Task<HttpResponseMessage> SendAsync(World w, HttpMethod method, string url, string json, params (string Name, string Value)[] headers)
    {
        using var client = Api.CreateClient(w.Caller);
        using var request = new HttpRequestMessage(method, url) { Content = Json(json) };
        foreach (var (name, value) in headers)
        {
            request.Headers.Add(name, value);
        }

        return await client.SendAsync(request);
    }

    private static string L(Guid id) => $"/api/v1/libraries/{id}";

    private static void ShouldBeIgnoredOrRefused(HttpResponseMessage response, string what) =>
        ((int)response.StatusCode).ShouldBeInRange(200, 499, what);

    [Fact]
    public async Task A_new_item_belongs_to_the_route_library_and_its_caller_whatever_the_body_says()
    {
        var w = await ArrangeAsync();
        var forged = Guid.CreateVersion7();
        var body = $$"""
            { "manual": { "title": "Planted", "ownerLibraryId": "{{w.Other.Id}}" },
              "libraryId": "{{w.Other.Id}}", "ownerLibraryId": "{{w.Other.Id}}", "createdBy": "{{w.Stranger}}", "id": "{{forged}}",
              "createdAt": "2001-01-01T00:00:00Z", "deletedAt": null, "version": 99, "addedVia": "manual" }
            """;

        var response = await SendAsync(w, HttpMethod.Post, $"{L(w.Route.Id)}/items", body, ("Idempotency-Key", Guid.NewGuid().ToString()));

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var created = await ReadJsonAsync(response);
        var id = created.GetProperty("id").GetGuid();
        id.ShouldNotBe(forged);
        await using var context = Db.CreateContext();
        var item = await context.Items.IgnoreQueryFilters().SingleAsync(i => i.Id == id);
        item.LibraryId.ShouldBe(w.Route.Id);
        item.CreatedBy.ShouldBe(w.Caller);
        item.CreatedAt.ShouldBeGreaterThan(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        item.DeletedAt.ShouldBeNull();
        (await context.Items.IgnoreQueryFilters().CountAsync(i => i.LibraryId == w.Other.Id)).ShouldBe(0);
        var release = await context.Releases.IgnoreQueryFilters().SingleAsync(r => r.Id == item.ReleaseId);
        release.OwnerLibraryId.ShouldNotBe(w.Other.Id);
    }

    [Fact]
    public async Task Patching_an_item_changes_only_what_a_patch_may_change()
    {
        var w = await ArrangeAsync();
        await using var before = Db.CreateContext();
        var original = await before.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.Id == w.ItemId);
        var body = $$"""
            { "notes": "Edited", "libraryId": "{{w.Other.Id}}", "createdBy": "{{w.Stranger}}", "id": "{{Guid.CreateVersion7()}}",
              "releaseId": "{{Guid.CreateVersion7()}}", "createdAt": "2001-01-01T00:00:00Z", "deletedAt": "2002-02-02T00:00:00Z", "addedVia": "scan" }
            """;

        var response = await SendAsync(w, HttpMethod.Patch, $"{L(w.Route.Id)}/items/{w.ItemId}", body, ("If-Match", $"\"{original.Version}\""));

        ShouldBeIgnoredOrRefused(response, await response.Content.ReadAsStringAsync());
        await using var context = Db.CreateContext();
        var item = await context.Items.IgnoreQueryFilters().SingleAsync(i => i.Id == w.ItemId);
        item.LibraryId.ShouldBe(w.Route.Id);
        item.CreatedBy.ShouldBe(original.CreatedBy);
        item.CreatedAt.ShouldBe(original.CreatedAt);
        item.ReleaseId.ShouldBe(original.ReleaseId);
        item.DeletedAt.ShouldBeNull();
        item.AddedVia.ShouldBe(original.AddedVia);
    }

    [Fact]
    public async Task Renaming_a_library_changes_its_name_and_nothing_else()
    {
        var w = await ArrangeAsync();
        var body = $$"""{ "name": "Renamed", "id": "{{w.Other.Id}}", "ownerLibraryId": "{{w.Other.Id}}", "ownerId": "{{w.Stranger}}", "createdBy": "{{w.Stranger}}" }""";

        var response = await SendAsync(w, HttpMethod.Patch, L(w.Route.Id), body);

        ShouldBeIgnoredOrRefused(response, await response.Content.ReadAsStringAsync());
        await using var context = Db.CreateContext();
        (await context.Libraries.IgnoreQueryFilters().SingleAsync(l => l.Id == w.Other.Id)).Name.ShouldBe("Test library");
        (await context.LibraryMembers.IgnoreQueryFilters().Where(m => m.LibraryId == w.Route.Id && m.Role == LibraryRole.Owner).Select(m => m.UserId).SingleAsync()).ShouldBe(w.Route.OwnerId);
    }

    [Fact]
    public async Task A_new_invite_is_for_the_route_library_made_by_the_caller_and_lasts_what_the_server_says()
    {
        var w = await ArrangeAsync();
        var body = $$"""
            { "name": "Sam", "role": "viewer", "libraryId": "{{w.Other.Id}}", "createdBy": "{{w.Stranger}}", "acceptedBy": "{{w.Stranger}}",
              "acceptedAt": "2026-01-01T00:00:00Z", "expiresAt": "2099-01-01T00:00:00Z", "tokenHash": "aaaa", "revokedAt": null, "id": "{{Guid.CreateVersion7()}}" }
            """;

        var response = await SendAsync(w, HttpMethod.Post, $"{L(w.Route.Id)}/invites", body);

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        await using var context = Db.CreateContext();
        (await context.LibraryInvites.IgnoreQueryFilters().CountAsync(i => i.LibraryId == w.Other.Id)).ShouldBe(0);
        var invite = await context.LibraryInvites.IgnoreQueryFilters().SingleAsync(i => i.LibraryId == w.Route.Id);
        invite.CreatedBy.ShouldBe(w.Caller);
        invite.AcceptedBy.ShouldBeNull();
        invite.AcceptedAt.ShouldBeNull();
        invite.TokenHash.ShouldNotBe("aaaa");
        invite.ExpiresAt.ShouldBeLessThan(DateTimeOffset.UtcNow.AddDays(8));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("admin")]
    [InlineData("Owner")]
    [InlineData("")]
    [InlineData("0")]
    public async Task An_invite_can_never_be_made_for_a_role_that_is_not_editor_or_viewer(string role)
    {
        var w = await ArrangeAsync();

        var response = await SendAsync(w, HttpMethod.Post, $"{L(w.Route.Id)}/invites", $$"""{ "name": "Sam", "role": "{{role}}" }""");

        ((int)response.StatusCode).ShouldBe(400, await response.Content.ReadAsStringAsync());
        await using var context = Db.CreateContext();
        (await context.LibraryInvites.IgnoreQueryFilters().CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData("PATCH")]
    [InlineData("PUT")]
    [InlineData("POST")]
    public async Task An_existing_invite_has_no_route_that_edits_its_role_or_expiry(string method)
    {
        var w = await ArrangeAsync();
        var created = await SendAsync(w, HttpMethod.Post, $"{L(w.Route.Id)}/invites", """{ "name": "Sam", "role": "viewer" }""");
        var id = (await ReadJsonAsync(created)).GetProperty("invite").GetProperty("id").GetGuid();

        var response = await SendAsync(w, new HttpMethod(method), $"{L(w.Route.Id)}/invites/{id}", """{ "role": "owner", "expiresAt": "2099-01-01T00:00:00Z" }""");

        ((int)response.StatusCode).ShouldBeInRange(404, 405);
        await using var context = Db.CreateContext();
        var invite = await context.LibraryInvites.IgnoreQueryFilters().SingleAsync(i => i.Id == id);
        invite.Role.ShouldBe(LibraryRole.Viewer);
        invite.ExpiresAt.ShouldBeLessThan(DateTimeOffset.UtcNow.AddDays(8));
    }

    [Fact]
    public async Task A_member_patch_changes_the_member_in_the_route_and_only_their_role()
    {
        var w = await ArrangeAsync();
        var body = $$"""{ "role": "viewer", "userId": "{{w.Route.OwnerId}}", "libraryId": "{{w.Other.Id}}", "joinedAt": "2001-01-01T00:00:00Z" }""";

        var response = await SendAsync(w, HttpMethod.Patch, $"{L(w.Route.Id)}/members/{w.Route.EditorId}", body);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        await using var context = Db.CreateContext();
        var members = await context.LibraryMembers.IgnoreQueryFilters().ToListAsync();
        members.Single(m => m.LibraryId == w.Route.Id && m.UserId == w.Route.EditorId).Role.ShouldBe(LibraryRole.Viewer);
        members.Single(m => m.LibraryId == w.Route.Id && m.UserId == w.Route.OwnerId).Role.ShouldBe(LibraryRole.Owner);
        members.Single(m => m.LibraryId == w.Other.Id && m.UserId == w.Other.OwnerId).Role.ShouldBe(LibraryRole.Owner);
        members.Single(m => m.LibraryId == w.Route.Id && m.UserId == w.Route.EditorId).JoinedAt.ShouldBeGreaterThan(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("admin")]
    public async Task Nobody_can_be_made_an_owner_or_an_admin_by_a_member_patch(string role)
    {
        var w = await ArrangeAsync();

        var response = await SendAsync(w, HttpMethod.Patch, $"{L(w.Route.Id)}/members/{w.Route.EditorId}", $$"""{ "role": "{{role}}" }""");

        ((int)response.StatusCode).ShouldBe(400, await response.Content.ReadAsStringAsync());
        await using var context = Db.CreateContext();
        (await context.LibraryMembers.IgnoreQueryFilters().SingleAsync(m => m.LibraryId == w.Route.Id && m.UserId == w.Route.EditorId)).Role.ShouldBe(LibraryRole.Editor);
    }

    [Fact]
    public async Task A_new_location_tag_and_scan_session_belong_to_the_route_library_and_the_caller()
    {
        var w = await ArrangeAsync();
        var named = $$"""{ "name": "Thing", "libraryId": "{{w.Other.Id}}", "createdBy": "{{w.Stranger}}", "userId": "{{w.Stranger}}", "id": "{{Guid.CreateVersion7()}}" }""";

        (await SendAsync(w, HttpMethod.Post, $"{L(w.Route.Id)}/locations", named)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await SendAsync(w, HttpMethod.Post, $"{L(w.Route.Id)}/tags", named)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var session = await SendAsync(w, HttpMethod.Post, $"{L(w.Route.Id)}/scan-sessions", $$"""{ "libraryId": "{{w.Other.Id}}", "userId": "{{w.Stranger}}", "status": "committed" }""");
        session.StatusCode.ShouldBe(HttpStatusCode.Created, await session.Content.ReadAsStringAsync());

        await using var context = Db.CreateContext();
        (await context.Locations.IgnoreQueryFilters().CountAsync(l => l.LibraryId == w.Other.Id)).ShouldBe(0);
        (await context.Tags.IgnoreQueryFilters().CountAsync(t => t.LibraryId == w.Other.Id)).ShouldBe(0);
        (await context.Locations.IgnoreQueryFilters().CountAsync(l => l.LibraryId == w.Route.Id)).ShouldBe(1);
        var sessionId = (await ReadJsonAsync(session)).GetProperty("id").GetGuid();
        var started = await context.ScanSessions.IgnoreQueryFilters().SingleAsync(s => s.Id == sessionId);
        started.LibraryId.ShouldBe(w.Route.Id);
        started.UserId.ShouldBe(w.Caller);
        started.Status.ShouldBe(Domain.Scanning.ScanSessionStatus.Open);
    }
}
