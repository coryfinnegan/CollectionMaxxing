using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Tests.Items;

public class BulkItemsTests(PostgresFixture db) : DatabaseTest(db)
{
    private static readonly SteppingClock Clock = new(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero));

    private Task<Result<BulkItemsResultDto>> RunAsync(TestLibrary library, BulkItems.Command command)
    {
        var context = Db.CreateContext(library.ActingAs(LibraryRole.Editor));
        return new BulkItems.Handler(context, Clock, new NoAudit()).HandleAsync(command, CancellationToken.None);
    }

    private static BulkItems.Command Patch(Guid libraryId, IReadOnlyList<Guid> ids, BulkChange change) =>
        new(libraryId, ids, BulkOperation.Patch, change);

    [Fact]
    public async Task Exactly_the_maximum_number_of_ids_is_allowed_and_one_more_is_not()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var atMax = Enumerable.Range(0, BulkItems.MaxIds).Select(_ => Guid.CreateVersion7()).ToList();

        // Allowed in size, so it gets as far as looking the items up (none exist).
        (await RunAsync(library, new(library.Id, atMax, BulkOperation.Delete, null))).Error!.Code.ShouldBe("item.not_found");

        atMax.Add(Guid.CreateVersion7());
        (await RunAsync(library, new(library.Id, atMax, BulkOperation.Delete, null))).Error!.Code.ShouldBe("bulk.too_many_ids");
    }

    [Fact]
    public async Task A_patch_that_only_adds_tags_or_only_removes_tags_is_a_real_patch()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var item = await TestData.CreateItemAsync(Db, library);
        var tag = await TestData.CreateTagAsync(Db, library.Id, "signed");

        (await RunAsync(library, Patch(library.Id, [item], new BulkChange(null, null, [tag], null)))).IsSuccess.ShouldBeTrue();
        (await RunAsync(library, Patch(library.Id, [item], new BulkChange(null, null, null, [tag])))).IsSuccess.ShouldBeTrue();
        (await RunAsync(library, Patch(library.Id, [item], new BulkChange(null, null, [], [])))).Error!.Code.ShouldBe("bulk.patch_required");
    }

    private async Task LendAsync(Guid itemId, string to, DateOnly on)
    {
        await using var context = Db.CreateContext();
        var item = await context.Items.IgnoreQueryFilters().SingleAsync(i => i.Id == itemId);
        item.Status = ItemStatus.Lent;
        item.LentTo = to;
        item.LentOn = on;
        await context.SaveChangesAsync();
    }

    private async Task<Item> ReadAsync(Guid itemId)
    {
        await using var context = Db.CreateContext();
        return await context.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.Id == itemId);
    }

    [Theory]
    [InlineData(ItemStatus.Owned)]
    [InlineData(ItemStatus.Wishlist)]
    [InlineData(ItemStatus.Sold)]
    [InlineData(ItemStatus.Missing)]
    public async Task Setting_any_status_but_lent_clears_who_it_was_lent_to_and_since_when(ItemStatus status)
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var item = await TestData.CreateItemAsync(Db, library);
        await LendAsync(item, "Sam", new DateOnly(2026, 5, 1));

        (await RunAsync(library, Patch(library.Id, [item], new BulkChange(null, status, null, null)))).IsSuccess.ShouldBeTrue();

        var after = await ReadAsync(item);
        after.Status.ShouldBe(status);
        after.LentTo.ShouldBeNull();
        after.LentOn.ShouldBeNull();
    }

    [Fact]
    public async Task Setting_lent_starts_the_lend_today_with_no_name_and_leaves_an_existing_lend_alone()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var owned = await TestData.CreateItemAsync(Db, library);
        var alreadyLent = await TestData.CreateItemAsync(Db, library);
        await LendAsync(alreadyLent, "Sam", new DateOnly(2026, 5, 1));

        (await RunAsync(library, Patch(library.Id, [owned, alreadyLent], new BulkChange(null, ItemStatus.Lent, null, null)))).IsSuccess.ShouldBeTrue();

        var started = await ReadAsync(owned);
        started.Status.ShouldBe(ItemStatus.Lent);
        started.LentTo.ShouldBeNull();
        started.LentOn.ShouldBe(new DateOnly(2026, 6, 1)); // the clock's day, as the single-item edit does

        var kept = await ReadAsync(alreadyLent);
        kept.LentTo.ShouldBe("Sam");
        kept.LentOn.ShouldBe(new DateOnly(2026, 5, 1));
    }

    [Fact]
    public async Task A_patch_with_no_status_leaves_the_lend_alone()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var item = await TestData.CreateItemAsync(Db, library);
        var tag = await TestData.CreateTagAsync(Db, library.Id, "signed");
        await LendAsync(item, "Sam", new DateOnly(2026, 5, 1));

        (await RunAsync(library, Patch(library.Id, [item], new BulkChange(null, null, [tag], null)))).IsSuccess.ShouldBeTrue();

        var after = await ReadAsync(item);
        after.Status.ShouldBe(ItemStatus.Lent);
        after.LentTo.ShouldBe("Sam");
        after.LentOn.ShouldBe(new DateOnly(2026, 5, 1));
    }

    [Fact]
    public async Task Removing_a_tag_from_one_item_leaves_other_items_and_other_tags_alone()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var first = await TestData.CreateItemAsync(Db, library);
        var second = await TestData.CreateItemAsync(Db, library);
        var signed = await TestData.CreateTagAsync(Db, library.Id, "signed");
        var loaned = await TestData.CreateTagAsync(Db, library.Id, "loaned");
        await TestData.AttachTagAsync(Db, library.Id, first, signed);
        await TestData.AttachTagAsync(Db, library.Id, first, loaned);
        await TestData.AttachTagAsync(Db, library.Id, second, signed);

        var result = await RunAsync(library, Patch(library.Id, [first], new BulkChange(null, null, null, [signed])));

        result.IsSuccess.ShouldBeTrue();
        (await TestData.CountItemTagsAsync(Db, library.Id)).ShouldBe(2); // first keeps "loaned", second keeps "signed"
    }
}
