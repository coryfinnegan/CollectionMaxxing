using System.Net;
using System.Net.Http.Json;
using CollectionMaxxing.Api.Tests.Items;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Scanning;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Locations;

/// <summary>
/// A location that only history still points at can be deleted. A scan session's default location and a kept row's location
/// are settings and leftovers, not things the person stored; trashed items wait out their 30 days and a restored one simply
/// has no location. Only a live item still in the place keeps it (<see cref="LocationEndpointsTests"/>).
/// </summary>
public class LocationDeleteInUseTests(ApiFixture api) : ApiTest(api)
{
    [Fact]
    public async Task A_location_that_was_a_scan_sessions_default_can_be_deleted_and_the_session_forgets_it()
    {
        var library = await CreateLibraryAsync();
        var shelf = await TestData.CreateLocationAsync(Db, library.Id, "Shelf");
        var editor = ClientFor(library, LibraryRole.Editor);
        var started = await editor.PostAsJsonAsync($"/api/v1/libraries/{library.Id}/scan-sessions", new { defaultLocationId = shelf });
        started.StatusCode.ShouldBe(HttpStatusCode.Created, await started.Content.ReadAsStringAsync());
        var sessionId = (await ReadJsonAsync(started)).GetProperty("id").GetGuid();

        var response = await editor.DeleteAsync(LocationUrl(library.Id, shelf));

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
        (await TestData.ReadLocationsAsync(Db, library.Id)).ShouldBeEmpty();
        await using var context = Db.CreateContext();
        var session = await context.ScanSessions.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == sessionId);
        session.DefaultLocationId.ShouldBeNull();
        session.Status.ShouldBe(ScanSessionStatus.Open, "the session carries on without a default place");
    }

    [Fact]
    public async Task A_location_a_kept_row_points_at_can_be_deleted_and_the_row_stays_without_it()
    {
        var library = await CreateLibraryAsync();
        var shelf = await TestData.CreateLocationAsync(Db, library.Id, "Shelf");
        var provisionalId = Guid.CreateVersion7();
        await using (var seed = Db.CreateContext())
        {
            seed.ProvisionalRecords.Add(new ProvisionalRecord
            {
                Id = provisionalId,
                LibraryId = library.Id,
                TitleGuess = "Zzxq Unreadable Spine Qqz",
                MediaHint = ScanMediaHint.Books,
                LocationId = shelf,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var response = await ClientFor(library, LibraryRole.Editor).DeleteAsync(LocationUrl(library.Id, shelf));

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
        await using var context = Db.CreateContext();
        var kept = await context.ProvisionalRecords.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == provisionalId);
        kept.LocationId.ShouldBeNull();
        kept.ResolvedAt.ShouldBeNull("deleting a place does not resolve the kept row");
    }

    [Fact]
    public async Task A_trashed_item_does_not_block_deleting_its_location_and_comes_back_without_one()
    {
        var library = await CreateLibraryAsync();
        var shelf = await TestData.CreateLocationAsync(Db, library.Id, "Shelf");
        var releaseId = await ItemSeed.AddReleaseAsync(Db, "Dune", WorkKind.Book, ReleaseFormat.Paperback, 1965);
        var trashedId = await TestData.CreateItemAsync(Db, library, releaseId, shelf);
        await ItemSeed.SetDeletedAtAsync(Db, trashedId, DateTimeOffset.UtcNow.AddDays(-1));
        var editor = ClientFor(library, LibraryRole.Editor);

        var response = await editor.DeleteAsync(LocationUrl(library.Id, shelf));

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
        (await TestData.ReadLocationsAsync(Db, library.Id)).ShouldBeEmpty();
        await using (var context = Db.CreateContext())
        {
            var trashed = await context.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.Id == trashedId);
            trashed.LocationId.ShouldBeNull();
            trashed.DeletedAt.ShouldNotBeNull("it is still in Recently deleted");
        }

        var restored = await editor.PostAsync($"/api/v1/libraries/{library.Id}/items/{trashedId}:restore", null);
        restored.StatusCode.ShouldBe(HttpStatusCode.OK, await restored.Content.ReadAsStringAsync());
        (await ReadJsonAsync(restored)).GetProperty("locationId").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task A_live_item_still_blocks_the_delete_even_when_a_trashed_one_sits_beside_it()
    {
        var library = await CreateLibraryAsync();
        var shelf = await TestData.CreateLocationAsync(Db, library.Id, "Shelf");
        var trashedId = await TestData.CreateItemAsync(Db, library, locationId: shelf);
        await ItemSeed.SetDeletedAtAsync(Db, trashedId, DateTimeOffset.UtcNow.AddDays(-1));
        var liveId = await TestData.CreateItemAsync(Db, library, locationId: shelf);

        var response = await ClientFor(library, LibraryRole.Editor).DeleteAsync(LocationUrl(library.Id, shelf));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await TestData.ReadLocationsAsync(Db, library.Id)).Count.ShouldBe(1);
        await using var context = Db.CreateContext();
        (await context.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.Id == liveId)).LocationId.ShouldBe(shelf);
        (await context.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.Id == trashedId)).LocationId.ShouldBe(shelf, "nothing was changed when the delete was refused");
    }
}
