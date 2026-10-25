using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Items;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Trash;

/// <summary>Acceptance tests for the "Recently deleted" bin and the audit events that deleting and restoring leave.</summary>
public class TrashEndpointsTests(ApiFixture api) : ApiTest(api)
{
    private static string ItemUrl(Guid libraryId, Guid itemId) => $"/api/v1/libraries/{libraryId}/items/{itemId}";

    private static string BinUrl(Guid libraryId, string query = "") => $"/api/v1/libraries/{libraryId}/items:deleted{query}";

    private static async Task DeleteAsync(HttpClient client, Guid libraryId, Guid itemId)
    {
        var etag = (await client.GetAsync(ItemUrl(libraryId, itemId))).Headers.ETag!.Tag;
        using var request = new HttpRequestMessage(HttpMethod.Delete, ItemUrl(libraryId, itemId));
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        (await client.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    private static string[] Titles(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()!).ToArray();

    private async Task<List<Domain.Operations.AuditEvent>> AuditAsync()
    {
        await using var context = Db.CreateContext();
        return await context.AuditEvents.AsNoTracking().OrderBy(e => e.At).ToListAsync();
    }

    [Fact]
    public async Task The_bin_lists_deleted_items_newest_first_with_when_they_will_be_purged()
    {
        var library = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);
        var older = await ItemSeed.AddItemAsync(Db, library, "Older");
        var newer = await ItemSeed.AddItemAsync(Db, library, "Newer");
        await ItemSeed.AddItemAsync(Db, library, "Kept");
        await DeleteAsync(editor, library.Id, older);
        await Task.Delay(20);
        await DeleteAsync(editor, library.Id, newer);

        var response = await editor.GetAsync(BinUrl(library.Id));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var page = await ReadJsonAsync(response);
        Titles(page).ShouldBe(["Newer", "Older"]);
        var first = page.GetProperty("items")[0];
        (first.GetProperty("purgesAt").GetDateTimeOffset() - first.GetProperty("deletedAt").GetDateTimeOffset()).ShouldBe(TimeSpan.FromDays(30));
    }

    [Fact]
    public async Task A_restored_item_leaves_the_bin_and_is_back_in_the_library()
    {
        var library = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);
        var itemId = await ItemSeed.AddItemAsync(Db, library, "Jaws");
        await DeleteAsync(editor, library.Id, itemId);
        Titles(await ReadJsonAsync(await editor.GetAsync(BinUrl(library.Id)))).ShouldBe(["Jaws"]);

        (await editor.PostAsync($"{ItemUrl(library.Id, itemId)}:restore", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        Titles(await ReadJsonAsync(await editor.GetAsync(BinUrl(library.Id)))).ShouldBeEmpty();
        Titles(await ReadJsonAsync(await editor.GetAsync($"/api/v1/libraries/{library.Id}/items"))).ShouldBe(["Jaws"]);
    }

    [Fact]
    public async Task The_bin_pages_with_a_cursor()
    {
        var library = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);
        foreach (var title in new[] { "A", "B", "C" })
        {
            await DeleteAsync(editor, library.Id, await ItemSeed.AddItemAsync(Db, library, title));
            await Task.Delay(20);
        }

        var first = await ReadJsonAsync(await editor.GetAsync(BinUrl(library.Id, "?limit=2")));
        Titles(first).ShouldBe(["C", "B"]);
        var cursor = first.GetProperty("nextCursor").GetString()!;
        var second = await ReadJsonAsync(await editor.GetAsync(BinUrl(library.Id, $"?limit=2&cursor={Uri.EscapeDataString(cursor)}")));

        Titles(second).ShouldBe(["A"]);
        second.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task An_item_deleted_more_than_30_days_ago_is_not_in_the_bin()
    {
        var library = await CreateLibraryAsync();
        var itemId = await ItemSeed.AddItemAsync(Db, library, "Expired");
        await ItemSeed.SetDeletedAtAsync(Db, itemId, DateTimeOffset.UtcNow.AddDays(-31));

        var page = await ReadJsonAsync(await ClientFor(library, LibraryRole.Editor).GetAsync(BinUrl(library.Id)));

        Titles(page).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_viewer_cannot_open_the_bin()
    {
        var library = await CreateLibraryAsync();

        var response = await ClientFor(library, LibraryRole.Viewer).GetAsync(BinUrl(library.Id));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Another_librarys_deleted_items_never_appear_in_the_bin()
    {
        var mine = await CreateLibraryAsync();
        var theirs = await CreateLibraryAsync();
        var itemId = await ItemSeed.AddItemAsync(Db, theirs, "Theirs");
        await DeleteAsync(ClientFor(theirs, LibraryRole.Editor), theirs.Id, itemId);

        var own = await ReadJsonAsync(await ClientFor(mine, LibraryRole.Editor).GetAsync(BinUrl(mine.Id)));
        var crossing = await ClientFor(mine, LibraryRole.Editor).GetAsync(BinUrl(theirs.Id));

        Titles(own).ShouldBeEmpty();
        crossing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Restoring_an_item_after_it_was_purged_returns_404()
    {
        var library = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);
        var itemId = await ItemSeed.AddItemAsync(Db, library, "Gone");
        await DeleteAsync(editor, library.Id, itemId);
        await using (var context = Db.CreateContext())
        {
            await context.Items.IgnoreQueryFilters().Where(i => i.Id == itemId).ExecuteDeleteAsync();
        }

        var response = await editor.PostAsync($"{ItemUrl(library.Id, itemId)}:restore", null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Deleting_restoring_and_bulk_deleting_each_leave_one_audit_event_with_the_actor_and_library()
    {
        var library = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);
        var itemId = await ItemSeed.AddItemAsync(Db, library, "Alien");
        var bulk1 = await ItemSeed.AddItemAsync(Db, library, "Aliens");
        var bulk2 = await ItemSeed.AddItemAsync(Db, library, "Alien 3");
        await DeleteAsync(editor, library.Id, itemId);
        await editor.PostAsync($"{ItemUrl(library.Id, itemId)}:restore", null);
        (await editor.PostAsJsonAsync($"/api/v1/libraries/{library.Id}/items:bulk", new { action = "delete", ids = new[] { bulk1, bulk2 } }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var events = await AuditAsync();

        events.Select(e => e.Action).ShouldBe(["item.deleted", "item.restored", "items.bulk_deleted"]);
        events.ShouldAllBe(e => e.ActorUserId == library.EditorId && e.LibraryId == library.Id);
        events[0].EntityId.ShouldBe(itemId);
        JsonDocument.Parse(events[2].DetailsJson!).RootElement.GetProperty("count").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task A_failed_delete_leaves_no_audit_event()
    {
        var library = await CreateLibraryAsync();
        var itemId = await ItemSeed.AddItemAsync(Db, library, "Stale");
        using var request = new HttpRequestMessage(HttpMethod.Delete, ItemUrl(library.Id, itemId));
        request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");

        var response = await ClientFor(library, LibraryRole.Editor).SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await AuditAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Audit_details_never_contain_titles_or_notes()
    {
        var library = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);
        var itemId = await ItemSeed.AddItemAsync(Db, library, "Secret Title");
        await DeleteAsync(editor, library.Id, itemId);
        await editor.PostAsync($"{ItemUrl(library.Id, itemId)}:restore", null);
        await editor.PostAsJsonAsync($"/api/v1/libraries/{library.Id}/items:bulk", new { action = "delete", ids = new[] { itemId } });

        var events = await AuditAsync();

        events.ShouldNotBeEmpty();
        events.ShouldAllBe(e => e.DetailsJson == null || (!e.DetailsJson.Contains("Secret") && !e.DetailsJson.Contains("title")));
    }
}
