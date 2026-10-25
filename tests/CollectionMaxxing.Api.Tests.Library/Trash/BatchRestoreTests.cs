using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Items;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Trash;

// SCAN-1d (issue #105), contract item 1: Review's Undo restores everything a commit added in one call. The tests marked
// LOCKED protect other libraries' items and the role rule; do not edit or weaken them.
public class BatchRestoreTests(ApiFixture api) : ApiTest(api)
{
    private static string RestoreUrl(Guid libraryId) => $"/api/v1/libraries/{libraryId}/items:restore";

    private static string BinUrl(Guid libraryId) => $"/api/v1/libraries/{libraryId}/items:deleted";

    private async Task SoftDeleteAsync(Guid itemId, TimeSpan? ago = null)
    {
        var at = DateTimeOffset.UtcNow - (ago ?? TimeSpan.FromMinutes(5));
        await using var context = Db.CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync($"update items set deleted_at = {at} where id = {itemId}");
    }

    private async Task<DateTimeOffset?> DeletedAtAsync(Guid itemId)
    {
        await using var context = Db.CreateContext();
        return await context.Items.IgnoreQueryFilters().Where(i => i.Id == itemId).Select(i => i.DeletedAt).SingleAsync();
    }

    private static async Task<int> BinCountAsync(HttpClient client, Guid libraryId) =>
        (await ReadJsonAsync(await client.GetAsync(BinUrl(libraryId)))).GetProperty("items").GetArrayLength();

    [Fact]
    public async Task Restoring_many_items_in_one_call_takes_them_all_out_of_Recently_deleted()
    {
        var library = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);
        var ids = new List<Guid>();
        foreach (var title in new[] { "Alien", "Aliens", "Alien 3" })
        {
            var id = await ItemSeed.AddItemAsync(Db, library, title);
            await SoftDeleteAsync(id);
            ids.Add(id);
        }

        (await BinCountAsync(editor, library.Id)).ShouldBe(3);
        var response = await editor.PostAsJsonAsync(RestoreUrl(library.Id), new { ids });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await ReadJsonAsync(response)).GetProperty("affected").GetInt32().ShouldBe(3);
        (await BinCountAsync(editor, library.Id)).ShouldBe(0);
        foreach (var id in ids)
        {
            (await DeletedAtAsync(id)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task Ids_that_are_already_live_are_left_alone_so_a_retry_is_harmless()
    {
        var library = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);
        var deleted = await ItemSeed.AddItemAsync(Db, library, "Deleted");
        var live = await ItemSeed.AddItemAsync(Db, library, "Live");
        await SoftDeleteAsync(deleted);

        var first = await editor.PostAsJsonAsync(RestoreUrl(library.Id), new { ids = new[] { deleted, live } });
        var again = await editor.PostAsJsonAsync(RestoreUrl(library.Id), new { ids = new[] { deleted, live } });

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await DeletedAtAsync(deleted)).ShouldBeNull();
        (await DeletedAtAsync(live)).ShouldBeNull();
    }

    [Fact]
    public async Task More_than_500_ids_or_none_at_all_is_rejected()
    {
        var library = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);

        var tooMany = await editor.PostAsJsonAsync(RestoreUrl(library.Id), new { ids = Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToArray() });
        var none = await editor.PostAsJsonAsync(RestoreUrl(library.Id), new { ids = Array.Empty<Guid>() });

        tooMany.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        none.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task One_item_deleted_longer_ago_than_the_window_restores_nothing()
    {
        var library = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);
        var recent = await ItemSeed.AddItemAsync(Db, library, "Recent");
        var old = await ItemSeed.AddItemAsync(Db, library, "Old");
        await SoftDeleteAsync(recent);
        await SoftDeleteAsync(old, TimeSpan.FromDays(31));

        var response = await editor.PostAsJsonAsync(RestoreUrl(library.Id), new { ids = new[] { recent, old } });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await DeletedAtAsync(recent)).ShouldNotBeNull();
    }

    [Fact]
    public async Task LOCKED_Batch_restore_never_touches_another_librarys_items_and_restores_nothing()
    {
        var library = await CreateLibraryAsync();
        var other = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);
        var mine = await ItemSeed.AddItemAsync(Db, library, "Mine");
        var theirs = await ItemSeed.AddItemAsync(Db, other, "Theirs");
        await SoftDeleteAsync(mine);
        await SoftDeleteAsync(theirs);

        var response = await editor.PostAsJsonAsync(RestoreUrl(library.Id), new { ids = new[] { mine, theirs } });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await DeletedAtAsync(mine)).ShouldNotBeNull("all or nothing: not even this library's own item");
        (await DeletedAtAsync(theirs)).ShouldNotBeNull();

        // Aiming at the other library's URL is the same 404 for a stranger.
        var stranger = await editor.PostAsJsonAsync(RestoreUrl(other.Id), new { ids = new[] { theirs } });
        stranger.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await DeletedAtAsync(theirs)).ShouldNotBeNull();
    }

    [Fact]
    public async Task LOCKED_A_viewer_cannot_restore_and_nothing_changes()
    {
        var library = await CreateLibraryAsync();
        var item = await ItemSeed.AddItemAsync(Db, library, "Deleted");
        await SoftDeleteAsync(item);

        var response = await ClientFor(library, LibraryRole.Viewer).PostAsJsonAsync(RestoreUrl(library.Id), new { ids = new[] { item } });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await DeletedAtAsync(item)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Restoring_leaves_one_audit_event_with_the_count_and_no_titles()
    {
        var library = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);
        var one = await ItemSeed.AddItemAsync(Db, library, "Secret One");
        var two = await ItemSeed.AddItemAsync(Db, library, "Secret Two");
        await SoftDeleteAsync(one);
        await SoftDeleteAsync(two);

        (await editor.PostAsJsonAsync(RestoreUrl(library.Id), new { ids = new[] { one, two } })).StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var context = Db.CreateContext();
        var events = await context.AuditEvents.AsNoTracking().ToListAsync();
        var restored = events.ShouldHaveSingleItem();
        restored.Action.ShouldBe("items.bulk_restored");
        restored.ActorUserId.ShouldBe(library.EditorId);
        restored.LibraryId.ShouldBe(library.Id);
        JsonDocument.Parse(restored.DetailsJson!).RootElement.GetProperty("count").GetInt32().ShouldBe(2);
        restored.DetailsJson!.ShouldNotContain("Secret");
    }

    [Fact]
    public async Task Restoring_items_that_are_not_deleted_changes_nothing_and_leaves_no_audit_event()
    {
        var library = await CreateLibraryAsync();
        var editor = ClientFor(library, LibraryRole.Editor);
        var live = await ItemSeed.AddItemAsync(Db, library, "Still here");

        var response = await editor.PostAsJsonAsync(RestoreUrl(library.Id), new { ids = new[] { live } });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await ReadJsonAsync(response)).GetProperty("affected").GetInt32().ShouldBe(1);
        await using var context = Db.CreateContext();
        (await context.AuditEvents.AsNoTracking().Where(e => e.Action == "items.bulk_restored").CountAsync()).ShouldBe(0);
    }
}
