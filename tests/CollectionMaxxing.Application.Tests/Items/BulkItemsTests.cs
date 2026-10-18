using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;

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
