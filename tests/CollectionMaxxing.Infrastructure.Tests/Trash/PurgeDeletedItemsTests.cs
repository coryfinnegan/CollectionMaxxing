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
