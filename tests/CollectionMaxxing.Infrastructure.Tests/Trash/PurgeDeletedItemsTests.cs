using CollectionMaxxing.Application.Audit;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Trash;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Infrastructure.Audit;
using CollectionMaxxing.Infrastructure.Ids;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CollectionMaxxing.Infrastructure.Tests.Trash;

public class PurgeDeletedItemsTests(PostgresFixture db) : DatabaseTest(db)
{
    private static readonly DateTimeOffset DeletedAt = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    private async Task RunPurgeAsync(FakeTimeProvider time)
    {
        await using var context = Db.CreateContext();
        var audit = new AuditLog(context, new CurrentUser(), new NoRequestOrigin(), time, new Uuid7IdGenerator(time), Options.Create(new AuditOptions()));
        await new PurgeDeletedItems.Handler(context, time, audit).HandleAsync(new PurgeDeletedItemsPayload(), CancellationToken.None);
    }

    private async Task<bool> ItemExistsAsync(Guid itemId)
    {
        await using var context = Db.CreateContext();
        return await context.Items.IgnoreQueryFilters().AnyAsync(i => i.Id == itemId);
    }

    private async Task<(TestLibrary Library, Guid ItemId)> DeletedItemAsync()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var itemId = await TestData.CreateItemAsync(Db, library);
        await using var context = Db.CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync($"update items set deleted_at = {DeletedAt} where id = {itemId}");
        return (library, itemId);
    }

    [Fact]
    public async Task The_record_of_an_item_removed_for_good_is_kept_30_days_and_then_trimmed()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var (old, recent) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await using (var context = Db.CreateContext())
        {
            context.ItemTombstones.AddRange(
                new ItemTombstone { ItemId = old, LibraryId = library.Id, DeletedAt = DeletedAt },
                new ItemTombstone { ItemId = recent, LibraryId = library.Id, DeletedAt = DeletedAt.AddDays(2) });
            await context.SaveChangesAsync();
        }

        await RunPurgeAsync(new FakeTimeProvider(DeletedAt.AddDays(30).AddSeconds(1)));

        await using var check = Db.CreateContext();
        (await check.ItemTombstones.IgnoreQueryFilters().Select(t => t.ItemId).ToListAsync()).ShouldBe([recent]);
    }

    [Fact]
    public async Task An_item_deleted_30_days_minus_one_second_ago_is_kept()
    {
        var (_, itemId) = await DeletedItemAsync();

        await RunPurgeAsync(new FakeTimeProvider(DeletedAt.AddDays(30).AddSeconds(-1)));

        (await ItemExistsAsync(itemId)).ShouldBeTrue();
    }

    [Fact]
    public async Task An_item_deleted_30_days_plus_one_second_ago_is_gone_with_its_dependent_rows()
    {
        var (library, itemId) = await DeletedItemAsync();
        var tagId = await TestData.CreateTagAsync(Db, library.Id, "Horror");
        await TestData.AttachTagAsync(Db, library.Id, itemId, tagId);
        var sessionId = await TestData.CreateScanSessionAsync(Db, library);
        await using (var context = Db.CreateContext())
        {
            context.ItemFeaturePrints.Add(new ItemFeaturePrint
            {
                Id = Guid.CreateVersion7(),
                LibraryId = library.Id,
                ItemId = itemId,
                ReleaseId = await context.Items.IgnoreQueryFilters().Where(i => i.Id == itemId).Select(i => i.ReleaseId).SingleAsync(),
                Revision = 1,
                Data = [1, 2, 3],
                CreatedAt = DeletedAt,
            });
            await context.SaveChangesAsync();
        }

        await RunPurgeAsync(new FakeTimeProvider(DeletedAt.AddDays(30).AddSeconds(1)));

        (await ItemExistsAsync(itemId)).ShouldBeFalse();
        await using var check = Db.CreateContext();
        (await check.ItemTags.IgnoreQueryFilters().CountAsync()).ShouldBe(0);
        (await check.ItemFeaturePrints.IgnoreQueryFilters().CountAsync()).ShouldBe(0);
        sessionId.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task Purging_touches_neither_live_items_nor_recently_deleted_ones()
    {
        var (library, deleted) = await DeletedItemAsync();
        var live = await TestData.CreateItemAsync(Db, library);
        var recent = await TestData.CreateItemAsync(Db, library);
        var now = DeletedAt.AddDays(40);
        await using (var context = Db.CreateContext())
        {
            var recentAt = now.AddDays(-2);
            await context.Database.ExecuteSqlInterpolatedAsync($"update items set deleted_at = {recentAt} where id = {recent}");
        }

        await RunPurgeAsync(new FakeTimeProvider(now));

        (await ItemExistsAsync(deleted)).ShouldBeFalse();
        (await ItemExistsAsync(live)).ShouldBeTrue();
        (await ItemExistsAsync(recent)).ShouldBeTrue();
    }

    [Fact]
    public async Task Purging_records_one_audit_event_per_item_with_the_library_and_no_actor()
    {
        var (library, itemId) = await DeletedItemAsync();

        await RunPurgeAsync(new FakeTimeProvider(DeletedAt.AddDays(31)));

        await using var context = Db.CreateContext();
        var events = await context.AuditEvents.AsNoTracking().ToListAsync();
        var purge = events.ShouldHaveSingleItem();
        purge.Action.ShouldBe("item.purged");
        purge.EntityId.ShouldBe(itemId);
        purge.LibraryId.ShouldBe(library.Id);
        purge.ActorUserId.ShouldBeNull();
    }

    private async Task TrashAsync(Guid itemId)
    {
        await using var context = Db.CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync($"update items set deleted_at = {DeletedAt} where id = {itemId}");
    }

    private async Task<(int Releases, int Works, int Identifiers)> CatalogueRowsAsync()
    {
        await using var context = Db.CreateContext();
        return (
            await context.Releases.IgnoreQueryFilters().CountAsync(),
            await context.Works.CountAsync(),
            await context.ReleaseIdentifiers.CountAsync());
    }

    [Fact]
    public async Task Purging_the_last_item_of_a_manual_release_removes_the_release_with_its_work_and_barcodes()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var releaseId = await TestData.CreateReleaseAsync(Db, library.Id, "Hand Made Quest");
        var itemId = await TestData.CreateItemAsync(Db, library, releaseId);
        await using (var context = Db.CreateContext())
        {
            context.ReleaseIdentifiers.Add(new Domain.Catalog.ReleaseIdentifier { ReleaseId = releaseId, Type = Domain.Catalog.IdentifierType.Gtin13, Value = "0123456789012" });
            await context.SaveChangesAsync();
        }

        await TrashAsync(itemId);

        await RunPurgeAsync(new FakeTimeProvider(DeletedAt.AddDays(31)));

        (await ItemExistsAsync(itemId)).ShouldBeFalse();
        (await CatalogueRowsAsync()).ShouldBe((0, 0, 0));
    }

    [Fact]
    public async Task A_scan_candidate_that_matched_a_removed_manual_release_forgets_it()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var releaseId = await TestData.CreateReleaseAsync(Db, library.Id);
        var itemId = await TestData.CreateItemAsync(Db, library, releaseId);
        var sessionId = await TestData.CreateScanSessionAsync(Db, library);
        var candidateId = Guid.CreateVersion7();
        await using (var arrange = Db.CreateContext())
        {
            arrange.ScanCandidates.Add(new Domain.Scanning.ScanCandidate
            {
                Id = candidateId,
                LibraryId = library.Id,
                SessionId = sessionId,
                Seq = 1,
                Source = Domain.Scanning.CandidateSource.Barcode,
                MatchedReleaseId = releaseId,
                CreatedAt = DeletedAt,
            });
            await arrange.SaveChangesAsync();
        }

        await TrashAsync(itemId);

        await RunPurgeAsync(new FakeTimeProvider(DeletedAt.AddDays(31)));

        (await CatalogueRowsAsync()).Releases.ShouldBe(0);
        await using var check = Db.CreateContext();
        (await check.ScanCandidates.IgnoreQueryFilters().SingleAsync(c => c.Id == candidateId)).MatchedReleaseId.ShouldBeNull();
    }

    [Fact]
    public async Task A_manual_release_stays_while_another_item_still_uses_it_even_one_in_the_trash()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var releaseId = await TestData.CreateReleaseAsync(Db, library.Id);
        var purged = await TestData.CreateItemAsync(Db, library, releaseId);
        var recentlyTrashed = await TestData.CreateItemAsync(Db, library, releaseId);
        await TrashAsync(purged);
        await using (var context = Db.CreateContext())
        {
            var recentAt = DeletedAt.AddDays(30);
            await context.Database.ExecuteSqlInterpolatedAsync($"update items set deleted_at = {recentAt} where id = {recentlyTrashed}");
        }

        await RunPurgeAsync(new FakeTimeProvider(DeletedAt.AddDays(31)));

        (await ItemExistsAsync(purged)).ShouldBeFalse();
        (await ItemExistsAsync(recentlyTrashed)).ShouldBeTrue();
        (await CatalogueRowsAsync()).ShouldBe((1, 1, 0));
    }

    [Fact]
    public async Task A_manual_release_stays_while_one_of_its_feature_prints_belongs_to_an_item_that_moved_on()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var manual = await TestData.CreateReleaseAsync(Db, library.Id);
        var purged = await TestData.CreateItemAsync(Db, library, manual);
        var moved = await TestData.CreateItemAsync(Db, library); // now on another release, but it was matched against the manual one
        await using (var context = Db.CreateContext())
        {
            context.ItemFeaturePrints.Add(new ItemFeaturePrint
            {
                Id = Guid.CreateVersion7(),
                LibraryId = library.Id,
                ItemId = moved,
                ReleaseId = manual,
                Revision = 1,
                Data = [1, 2, 3],
                CreatedAt = DeletedAt,
            });
            await context.SaveChangesAsync();
        }

        await TrashAsync(purged);

        await RunPurgeAsync(new FakeTimeProvider(DeletedAt.AddDays(31)));

        (await ItemExistsAsync(purged)).ShouldBeFalse();
        (await CatalogueRowsAsync()).Releases.ShouldBe(2); // the manual release and the other item's own
    }

    [Fact]
    public async Task Purging_an_item_of_a_shared_release_leaves_the_shared_catalogue_alone()
    {
        var (_, itemId) = await DeletedItemAsync();

        await RunPurgeAsync(new FakeTimeProvider(DeletedAt.AddDays(31)));

        (await ItemExistsAsync(itemId)).ShouldBeFalse();
        (await CatalogueRowsAsync()).ShouldBe((1, 1, 0));
    }

    [Fact]
    public async Task A_manual_release_on_a_shared_work_is_removed_but_the_work_stays()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var shared = await TestData.CreateReleaseAsync(Db);
        Guid manual;
        await using (var context = Db.CreateContext())
        {
            var workId = await context.Releases.IgnoreQueryFilters().Where(r => r.Id == shared).Select(r => r.WorkId).SingleAsync();
            manual = Guid.CreateVersion7();
            context.Releases.Add(new Domain.Catalog.Release
            {
                Id = manual,
                WorkId = workId,
                Format = Domain.Catalog.ReleaseFormat.Dvd,
                Source = Domain.Catalog.ReleaseSource.User,
                OwnerLibraryId = library.Id,
                CreatedAt = DeletedAt,
            });
            await context.SaveChangesAsync();
        }

        var itemId = await TestData.CreateItemAsync(Db, library, manual);
        await TrashAsync(itemId);

        await RunPurgeAsync(new FakeTimeProvider(DeletedAt.AddDays(31)));

        (await CatalogueRowsAsync()).ShouldBe((1, 1, 0));
        await using var check = Db.CreateContext();
        (await check.Releases.IgnoreQueryFilters().SingleAsync()).Id.ShouldBe(shared);
    }

    [Fact]
    public async Task Another_librarys_manual_releases_are_not_touched()
    {
        var mine = await TestData.CreateLibraryAsync(Db);
        var theirs = await TestData.CreateLibraryAsync(Db);
        var theirRelease = await TestData.CreateReleaseAsync(Db, theirs.Id);
        await TestData.CreateItemAsync(Db, theirs, theirRelease);
        var myItem = await TestData.CreateItemAsync(Db, mine, await TestData.CreateReleaseAsync(Db, mine.Id));
        await TrashAsync(myItem);

        await RunPurgeAsync(new FakeTimeProvider(DeletedAt.AddDays(31)));

        (await CatalogueRowsAsync()).ShouldBe((1, 1, 0));
        await using var check = Db.CreateContext();
        (await check.Releases.IgnoreQueryFilters().SingleAsync()).Id.ShouldBe(theirRelease);
    }

    [Fact]
    public async Task Purging_an_item_that_a_scan_candidate_created_works_and_clears_the_link()
    {
        var (library, itemId) = await DeletedItemAsync();
        var sessionId = await TestData.CreateScanSessionAsync(Db, library);
        var candidateId = Guid.CreateVersion7();
        await using (var arrange = Db.CreateContext())
        {
            arrange.ScanCandidates.Add(new Domain.Scanning.ScanCandidate
            {
                Id = candidateId,
                LibraryId = library.Id,
                SessionId = sessionId,
                Seq = 1,
                Source = Domain.Scanning.CandidateSource.Barcode,
                CreatedItemId = itemId,
                CreatedAt = DeletedAt,
            });
            await arrange.SaveChangesAsync();
        }

        await RunPurgeAsync(new FakeTimeProvider(DeletedAt.AddDays(31)));

        (await ItemExistsAsync(itemId)).ShouldBeFalse();
        await using var context = Db.CreateContext();
        (await context.ScanCandidates.IgnoreQueryFilters().SingleAsync(c => c.Id == candidateId)).CreatedItemId.ShouldBeNull();
    }
}
