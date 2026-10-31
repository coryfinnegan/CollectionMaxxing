using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Items;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Ownership;

/// <summary>Acceptance tests for "do I own this?" and the offline snapshot, from the caller's side.</summary>
public class OwnershipEndpointsTests(ApiFixture api) : ApiTest(api)
{
    private const string UpcA = "045496870010";
    private const string Ean13 = "0045496870010";

    private static string OwnershipUrl(Guid libraryId, string query) => $"/api/v1/libraries/{libraryId}/ownership?{query}";

    private static string SnapshotUrl(Guid libraryId, string? since = null, int? limit = null)
    {
        var parts = new List<string>();
        if (since is not null)
        {
            parts.Add($"since={Uri.EscapeDataString(since)}");
        }

        if (limit is not null)
        {
            parts.Add($"limit={limit}");
        }

        return $"/api/v1/libraries/{libraryId}/ownership/snapshot" + (parts.Count > 0 ? "?" + string.Join('&', parts) : string.Empty);
    }

    /// <summary>Adds a barcode to a release: a shared one, or (with <paramref name="taughtBy"/>) one that library taught by hand.</summary>
    private async Task AddIdentifierAsync(Guid releaseId, string value, Guid? taughtBy = null)
    {
        await using var context = Db.CreateContext();
        context.ReleaseIdentifiers.Add(new ReleaseIdentifier { ReleaseId = releaseId, Type = IdentifierType.Gtin13, Value = value, LibraryId = taughtBy });
        await context.SaveChangesAsync();
    }

    private async Task<Guid> AddCopyAsync(
        TestLibrary library, Guid releaseId, Guid? locationId = null, ItemStatus status = ItemStatus.Owned, int updatedMinutes = 0)
    {
        var at = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero).AddMinutes(updatedMinutes);
        var item = new Item
        {
            Id = Guid.CreateVersion7(),
            LibraryId = library.Id,
            ReleaseId = releaseId,
            Status = status,
            LocationId = locationId,
            CoverChoice = CoverChoice.Release,
            AddedVia = AddedVia.Manual,
            CreatedAt = at,
            CreatedBy = library.OwnerId,
            UpdatedAt = at,
        };
        await using var context = Db.CreateContext();
        context.Items.Add(item);
        await context.SaveChangesAsync();
        return item.Id;
    }

    private async Task TouchAsync(Guid itemId, DateTimeOffset at, bool deleted = false, ItemStatus? status = null)
    {
        await using var context = Db.CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"update items set updated_at = {at}, deleted_at = {(deleted ? at : (DateTimeOffset?)null)} where id = {itemId}");
        if (status is { } s)
        {
            var text = s.ToString().ToLowerInvariant();
            await context.Database.ExecuteSqlInterpolatedAsync($"update items set status = {text} where id = {itemId}");
        }
    }

    // ---- Ownership --------------------------------------------------------------------------------------

    [Fact]
    public async Task An_owned_release_returns_its_copies_with_their_location_breadcrumb()
    {
        var library = await CreateLibraryAsync();
        var releaseId = await ItemSeed.AddReleaseAsync(Db);
        var room = await TestData.CreateLocationAsync(Db, library.Id, "Living room");
        var shelf = await TestData.CreateLocationAsync(Db, library.Id, "Shelf 2", room);
        var itemId = await AddCopyAsync(library, releaseId, shelf);

        var response = await ClientFor(library, LibraryRole.Viewer).GetAsync(OwnershipUrl(library.Id, $"releaseId={releaseId}"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var answer = await ReadJsonAsync(response);
        answer.GetProperty("owned").GetBoolean().ShouldBeTrue();
        answer.GetProperty("count").GetInt32().ShouldBe(1);
        var copy = answer.GetProperty("copies")[0];
        copy.GetProperty("itemId").GetGuid().ShouldBe(itemId);
        copy.GetProperty("locationPath").GetString().ShouldBe("Living room › Shelf 2");
        answer.GetProperty("sameWorkOtherFormat").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task The_same_work_in_another_format_is_a_soft_match_not_an_exact_one()
    {
        var library = await CreateLibraryAsync();
        var dvd = await ItemSeed.AddReleaseAsync(Db, format: ReleaseFormat.Dvd);
        var workId = await ItemSeed.WorkOfAsync(Db, dvd);
        var bluray = await ItemSeed.AddReleaseAsync(Db, format: ReleaseFormat.Bluray, workId: workId);
        await AddCopyAsync(library, bluray);

        var answer = await ReadJsonAsync(await ClientFor(library, LibraryRole.Viewer).GetAsync(OwnershipUrl(library.Id, $"releaseId={dvd}")));

        answer.GetProperty("owned").GetBoolean().ShouldBeFalse();
        answer.GetProperty("count").GetInt32().ShouldBe(0);
        answer.GetProperty("copies").GetArrayLength().ShouldBe(0);
        var soft = answer.GetProperty("sameWorkOtherFormat");
        soft.GetArrayLength().ShouldBe(1);
        soft[0].GetProperty("releaseId").GetGuid().ShouldBe(bluray);
        soft[0].GetProperty("format").GetString().ShouldBe("bluray");
    }

    [Fact]
    public async Task Another_release_of_the_same_work_in_the_same_format_is_not_a_soft_match()
    {
        var library = await CreateLibraryAsync();
        var first = await ItemSeed.AddReleaseAsync(Db, format: ReleaseFormat.Dvd);
        var second = await ItemSeed.AddReleaseAsync(Db, format: ReleaseFormat.Dvd, workId: await ItemSeed.WorkOfAsync(Db, first));
        await AddCopyAsync(library, second);

        var answer = await ReadJsonAsync(await ClientFor(library, LibraryRole.Viewer).GetAsync(OwnershipUrl(library.Id, $"releaseId={first}")));

        answer.GetProperty("owned").GetBoolean().ShouldBeFalse();
        answer.GetProperty("sameWorkOtherFormat").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Owning_nothing_gives_an_empty_answer()
    {
        var library = await CreateLibraryAsync();
        var releaseId = await ItemSeed.AddReleaseAsync(Db);

        var response = await ClientFor(library, LibraryRole.Viewer).GetAsync(OwnershipUrl(library.Id, $"releaseId={releaseId}"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var answer = await ReadJsonAsync(response);
        answer.GetProperty("owned").GetBoolean().ShouldBeFalse();
        answer.GetProperty("count").GetInt32().ShouldBe(0);
        answer.GetProperty("copies").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task A_work_query_lists_every_copy_of_the_work()
    {
        var library = await CreateLibraryAsync();
        var dvd = await ItemSeed.AddReleaseAsync(Db, format: ReleaseFormat.Dvd);
        var workId = await ItemSeed.WorkOfAsync(Db, dvd);
        var bluray = await ItemSeed.AddReleaseAsync(Db, format: ReleaseFormat.Bluray, workId: workId);
        await AddCopyAsync(library, dvd);
        await AddCopyAsync(library, bluray);

        var answer = await ReadJsonAsync(await ClientFor(library, LibraryRole.Viewer).GetAsync(OwnershipUrl(library.Id, $"workId={workId}")));

        answer.GetProperty("count").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task A_upc_a_and_its_ean_13_form_give_the_same_answer()
    {
        var library = await CreateLibraryAsync();
        var releaseId = await ItemSeed.AddReleaseAsync(Db);
        await AddIdentifierAsync(releaseId, Ean13);
        await AddCopyAsync(library, releaseId);
        var client = ClientFor(library, LibraryRole.Viewer);

        var byUpc = await ReadJsonAsync(await client.GetAsync(OwnershipUrl(library.Id, $"code={UpcA}")));
        var byEan = await ReadJsonAsync(await client.GetAsync(OwnershipUrl(library.Id, $"code={Ean13}")));

        byUpc.GetProperty("owned").GetBoolean().ShouldBeTrue();
        byUpc.GetProperty("count").GetInt32().ShouldBe(1);
        byEan.GetRawText().ShouldBe(byUpc.GetRawText());
    }

    [Fact]
    public async Task A_code_that_is_not_a_barcode_is_a_validation_error_and_so_is_asking_with_no_key_or_two()
    {
        var library = await CreateLibraryAsync();
        var client = ClientFor(library, LibraryRole.Viewer);

        (await client.GetAsync(OwnershipUrl(library.Id, "code=12345"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.GetAsync(OwnershipUrl(library.Id, string.Empty))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.GetAsync(OwnershipUrl(library.Id, $"code={UpcA}&workId={Guid.NewGuid()}"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(true, ItemStatus.Owned)]
    [InlineData(false, ItemStatus.Wishlist)]
    [InlineData(false, ItemStatus.Sold)]
    [InlineData(true, ItemStatus.Lent)]
    public async Task Only_copies_that_are_kept_count_as_owned(bool expected, ItemStatus status)
    {
        var library = await CreateLibraryAsync();
        var releaseId = await ItemSeed.AddReleaseAsync(Db);
        await AddCopyAsync(library, releaseId, status: status);

        var answer = await ReadJsonAsync(await ClientFor(library, LibraryRole.Viewer).GetAsync(OwnershipUrl(library.Id, $"releaseId={releaseId}")));

        answer.GetProperty("owned").GetBoolean().ShouldBe(expected);
    }

    [Fact]
    public async Task A_soft_deleted_item_is_not_owned()
    {
        var library = await CreateLibraryAsync();
        var releaseId = await ItemSeed.AddReleaseAsync(Db);
        var itemId = await AddCopyAsync(library, releaseId);
        await ItemSeed.SetDeletedAtAsync(Db, itemId, DateTimeOffset.UtcNow);

        var answer = await ReadJsonAsync(await ClientFor(library, LibraryRole.Viewer).GetAsync(OwnershipUrl(library.Id, $"releaseId={releaseId}")));

        answer.GetProperty("owned").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task One_library_cannot_learn_what_another_owns_by_release_id_work_id_or_code()
    {
        var mine = await CreateLibraryAsync();
        var theirs = await CreateLibraryAsync();
        var releaseId = await ItemSeed.AddReleaseAsync(Db);
        var workId = await ItemSeed.WorkOfAsync(Db, releaseId);
        await AddIdentifierAsync(releaseId, Ean13);
        await AddCopyAsync(theirs, releaseId);
        var privateRelease = await ItemSeed.AddReleaseAsync(Db, title: "Mine alone", ownerLibraryId: theirs.Id);
        await AddCopyAsync(theirs, privateRelease);
        var client = ClientFor(mine, LibraryRole.Viewer);

        foreach (var query in new[] { $"releaseId={releaseId}", $"workId={workId}", $"code={Ean13}", $"releaseId={privateRelease}" })
        {
            var response = await client.GetAsync(OwnershipUrl(mine.Id, query));
            response.StatusCode.ShouldBe(HttpStatusCode.OK, query);
            var answer = await ReadJsonAsync(response);
            answer.GetProperty("owned").GetBoolean().ShouldBeFalse(query);
            answer.GetProperty("copies").GetArrayLength().ShouldBe(0, query);
            answer.GetProperty("sameWorkOtherFormat").GetArrayLength().ShouldBe(0, query);
        }
    }

    [Fact]
    public async Task A_member_of_another_library_gets_404_for_both_endpoints()
    {
        var library = await CreateLibraryAsync();
        var other = await CreateLibraryAsync();
        var client = ClientFor(other, LibraryRole.Owner);

        (await client.GetAsync(OwnershipUrl(library.Id, $"code={UpcA}"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync(SnapshotUrl(library.Id))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ---- Snapshot ---------------------------------------------------------------------------------------

    [Fact]
    public async Task The_first_snapshot_has_every_owned_item_with_its_identifiers_title_format_and_location()
    {
        var library = await CreateLibraryAsync();
        var platformId = await ItemSeed.AddPlatformAsync(Db, "Nintendo 64", "N64");
        var game = await ItemSeed.AddReleaseAsync(Db, "Goldeneye", WorkKind.Game, ReleaseFormat.GameCartridge, platformId: platformId);
        var movie = await ItemSeed.AddReleaseAsync(Db, "Jurassic Park");
        var wished = await ItemSeed.AddReleaseAsync(Db, "Wishlisted");
        await AddIdentifierAsync(game, Ean13);
        var shelf = await TestData.CreateLocationAsync(Db, library.Id, "Shelf");
        await AddCopyAsync(library, game, shelf, updatedMinutes: 1);
        await AddCopyAsync(library, movie, updatedMinutes: 2);
        await AddCopyAsync(library, wished, status: ItemStatus.Wishlist, updatedMinutes: 3);

        var response = await ClientFor(library, LibraryRole.Viewer).GetAsync(SnapshotUrl(library.Id));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var snapshot = await ReadJsonAsync(response);
        snapshot.GetProperty("hasMore").GetBoolean().ShouldBeFalse();
        snapshot.GetProperty("nextCursor").GetString().ShouldNotBeNullOrEmpty();
        var entries = snapshot.GetProperty("entries");
        entries.GetArrayLength().ShouldBe(2);
        entries[0].GetProperty("title").GetString().ShouldBe("Goldeneye");
        entries[0].GetProperty("format").GetString().ShouldBe("game_cartridge");
        entries[0].GetProperty("platform").GetString().ShouldBe("Nintendo 64");
        entries[0].GetProperty("locationPath").GetString().ShouldBe("Shelf");
        entries[0].GetProperty("identifiers")[0].GetString().ShouldBe(Ean13);
        entries[0].GetProperty("deleted").GetBoolean().ShouldBeFalse();
        entries[1].GetProperty("title").GetString().ShouldBe("Jurassic Park");
    }

    [Fact]
    public async Task The_snapshot_lists_the_barcodes_the_library_taught_but_never_one_only_another_library_taught()
    {
        var mine = await CreateLibraryAsync();
        var theirs = await CreateLibraryAsync();
        var game = await ItemSeed.AddReleaseAsync(Db, "Goldeneye", WorkKind.Game, ReleaseFormat.GameCartridge);
        await AddIdentifierAsync(game, Ean13);
        await AddIdentifierAsync(game, "0045496830069", taughtBy: mine.Id);
        await AddIdentifierAsync(game, "0045496830076", taughtBy: theirs.Id);
        await AddCopyAsync(mine, game);

        var snapshot = await ReadJsonAsync(await ClientFor(mine, LibraryRole.Viewer).GetAsync(SnapshotUrl(mine.Id)));

        snapshot.GetProperty("entries")[0].GetProperty("identifiers").EnumerateArray().Select(i => i.GetString())
            .ShouldBe(["0045496830069", Ean13]); // sorted
    }

    [Fact]
    public async Task A_snapshot_with_a_cursor_returns_only_changes_and_tombstones()
    {
        var library = await CreateLibraryAsync();
        var client = ClientFor(library, LibraryRole.Viewer);
        var kept = await AddCopyAsync(library, await ItemSeed.AddReleaseAsync(Db, "Kept"), updatedMinutes: 1);
        var deleted = await AddCopyAsync(library, await ItemSeed.AddReleaseAsync(Db, "Deleted"), updatedMinutes: 2);
        var sold = await AddCopyAsync(library, await ItemSeed.AddReleaseAsync(Db, "Sold"), updatedMinutes: 3);
        var cursor = (await ReadJsonAsync(await client.GetAsync(SnapshotUrl(library.Id)))).GetProperty("nextCursor").GetString()!;

        var later = new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);
        await TouchAsync(deleted, later, deleted: true);
        await TouchAsync(sold, later.AddMinutes(1), status: ItemStatus.Sold);
        var added = await AddCopyAsync(library, await ItemSeed.AddReleaseAsync(Db, "Added"), updatedMinutes: 60 * 48);

        var snapshot = await ReadJsonAsync(await client.GetAsync(SnapshotUrl(library.Id, cursor)));

        var entries = snapshot.GetProperty("entries").EnumerateArray().ToList();
        entries.Select(e => e.GetProperty("itemId").GetGuid()).ShouldBe([deleted, sold, added]);
        entries.Select(e => e.GetProperty("deleted").GetBoolean()).ShouldBe([true, true, false]);
        entries.ShouldNotContain(e => e.GetProperty("itemId").GetGuid() == kept);
        entries[0].GetProperty("title").ValueKind.ShouldBe(JsonValueKind.Null);

        var again = await ReadJsonAsync(await client.GetAsync(SnapshotUrl(library.Id, snapshot.GetProperty("nextCursor").GetString())));
        again.GetProperty("entries").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Paging_a_snapshot_neither_repeats_nor_skips_items_even_when_they_share_a_timestamp_or_arrive_mid_paging()
    {
        var library = await CreateLibraryAsync();
        var client = ClientFor(library, LibraryRole.Viewer);
        var expected = new List<Guid>();
        for (var i = 0; i < 7; i++)
        {
            expected.Add(await AddCopyAsync(library, await ItemSeed.AddReleaseAsync(Db, $"Item {i}"), updatedMinutes: i < 5 ? 0 : i));
        }

        var seen = new List<Guid>();
        var first = await ReadJsonAsync(await client.GetAsync(SnapshotUrl(library.Id, limit: 3)));
        first.GetProperty("hasMore").GetBoolean().ShouldBeTrue();
        seen.AddRange(first.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("itemId").GetGuid()));

        // A write lands between pages.
        var late = await AddCopyAsync(library, await ItemSeed.AddReleaseAsync(Db, "Late"), updatedMinutes: 600);
        expected.Add(late);

        var cursor = first.GetProperty("nextCursor").GetString();
        var guard = 0;
        while (guard++ < 10)
        {
            var page = await ReadJsonAsync(await client.GetAsync(SnapshotUrl(library.Id, cursor, 3)));
            seen.AddRange(page.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("itemId").GetGuid()));
            cursor = page.GetProperty("nextCursor").GetString();
            if (!page.GetProperty("hasMore").GetBoolean())
            {
                break;
            }
        }

        seen.Count.ShouldBe(seen.Distinct().Count());
        seen.Order().ShouldBe(expected.Order());
    }

    [Fact]
    public async Task A_snapshot_never_includes_another_librarys_items_and_rejects_a_bad_cursor()
    {
        var mine = await CreateLibraryAsync();
        var theirs = await CreateLibraryAsync();
        await AddCopyAsync(theirs, await ItemSeed.AddReleaseAsync(Db, "Theirs"));
        var client = ClientFor(mine, LibraryRole.Viewer);

        var snapshot = await ReadJsonAsync(await client.GetAsync(SnapshotUrl(mine.Id)));
        snapshot.GetProperty("entries").GetArrayLength().ShouldBe(0);

        (await client.GetAsync(SnapshotUrl(mine.Id, "nonsense"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
