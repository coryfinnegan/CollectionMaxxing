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

        (await RunAsync(library, Patch(library.Id, [item], new BulkChange(default, null, [tag], null)))).IsSuccess.ShouldBeTrue();
        (await RunAsync(library, Patch(library.Id, [item], new BulkChange(default, null, null, [tag])))).IsSuccess.ShouldBeTrue();
        (await RunAsync(library, Patch(library.Id, [item], new BulkChange(default, null, [], [])))).Error!.Code.ShouldBe("bulk.patch_required");
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

        (await RunAsync(library, Patch(library.Id, [item], new BulkChange(default, status, null, null)))).IsSuccess.ShouldBeTrue();

        var after = await ReadAsync(item);
        after.Status.ShouldBe(status);
        after.LentTo.ShouldBeNull();
        after.LentOn.ShouldBeNull();
    }

    [Fact]
    public async Task Setting_lent_is_refused_when_any_item_has_no_borrower_and_nothing_changes()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var owned = await TestData.CreateItemAsync(Db, library);
        var alreadyLent = await TestData.CreateItemAsync(Db, library);
        await LendAsync(alreadyLent, "Sam", new DateOnly(2026, 5, 1));

        var result = await RunAsync(library, Patch(library.Id, [owned, alreadyLent], new BulkChange(default, ItemStatus.Lent, null, null)));

        result.Error!.Code.ShouldBe("item.lent_to_required");
        (await ReadAsync(owned)).Status.ShouldBe(ItemStatus.Owned);
    }

    [Fact]
    public async Task Setting_lent_is_refused_for_an_item_that_only_carries_a_stale_borrower()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var item = await TestData.CreateItemAsync(Db, library);
        await using (var context = Db.CreateContext())
        {
            (await context.Items.IgnoreQueryFilters().SingleAsync(i => i.Id == item)).LentTo = "Sam"; // left over from an earlier lend
            await context.SaveChangesAsync();
        }

        var result = await RunAsync(library, Patch(library.Id, [item], new BulkChange(default, ItemStatus.Lent, null, null)));

        result.Error!.Code.ShouldBe("item.lent_to_required");
    }

    [Fact]
    public async Task Setting_lent_on_items_already_lent_to_someone_leaves_their_lends_alone()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var alreadyLent = await TestData.CreateItemAsync(Db, library);
        await LendAsync(alreadyLent, "Sam", new DateOnly(2026, 5, 1));

        (await RunAsync(library, Patch(library.Id, [alreadyLent], new BulkChange(default, ItemStatus.Lent, null, null)))).IsSuccess.ShouldBeTrue();

        var kept = await ReadAsync(alreadyLent);
        kept.LentTo.ShouldBe("Sam");
        kept.LentOn.ShouldBe(new DateOnly(2026, 5, 1));
    }

    [Fact]
    public async Task A_location_set_to_null_clears_it_and_one_that_is_not_set_leaves_it()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var shelf = await TestData.CreateLocationAsync(Db, library.Id, "Shelf");
        var item = await TestData.CreateItemAsync(Db, library, locationId: shelf);

        (await RunAsync(library, Patch(library.Id, [item], new BulkChange(default, ItemStatus.Missing, null, null)))).IsSuccess.ShouldBeTrue();
        (await ReadAsync(item)).LocationId.ShouldBe(shelf);

        (await RunAsync(library, Patch(library.Id, [item], new BulkChange(PatchValue<Guid?>.Of(null), null, null, null)))).IsSuccess.ShouldBeTrue();
        (await ReadAsync(item)).LocationId.ShouldBeNull();
    }

    [Fact]
    public async Task A_patch_with_no_status_leaves_the_lend_alone()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var item = await TestData.CreateItemAsync(Db, library);
        var tag = await TestData.CreateTagAsync(Db, library.Id, "signed");
        await LendAsync(item, "Sam", new DateOnly(2026, 5, 1));

        (await RunAsync(library, Patch(library.Id, [item], new BulkChange(default, null, [tag], null)))).IsSuccess.ShouldBeTrue();

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

        var result = await RunAsync(library, Patch(library.Id, [first], new BulkChange(default, null, null, [signed])));

        result.IsSuccess.ShouldBeTrue();
        (await TestData.CountItemTagsAsync(Db, library.Id)).ShouldBe(2); // first keeps "loaned", second keeps "signed"
    }
}
