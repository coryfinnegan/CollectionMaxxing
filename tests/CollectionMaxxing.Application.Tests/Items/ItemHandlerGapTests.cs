using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Tests.Items;

// Added by QA-3b: behaviours the Application mutation sweep showed nothing was checking.
public class ItemHandlerGapTests(PostgresFixture db) : DatabaseTest(db)
{
    private static readonly SteppingClock Clock = new(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero));

    private ListItems.Handler Lister(TestLibrary library) => new(Db.CreateContext(library.ActingAs(LibraryRole.Viewer)));

    private static ListItems.Query ListQuery(Guid libraryId, string? sort = null, int? limit = null, string? cursor = null, string? tag = null) =>
        new(libraryId, null, null, null, null, null, null, tag, sort, limit, cursor);

    // ---- DeleteItem ----

    [Fact]
    public async Task Deleting_an_unknown_id_is_not_found_and_leaves_the_library_s_other_items()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var other = await TestData.CreateItemAsync(Db, library);

        var handler = new DeleteItem.Handler(Db.CreateContext(library.ActingAs(LibraryRole.Editor)), Clock, new NoAudit());
        var result = await handler.HandleAsync(new DeleteItem.Command(library.Id, Guid.CreateVersion7(), "*"), CancellationToken.None);

        result.Error!.Code.ShouldBe("item.not_found");
        await using var check = Db.CreateContext(library.ActingAs(LibraryRole.Viewer));
        (await check.Items.SingleAsync(i => i.Id == other)).DeletedAt.ShouldBeNull();
    }

    // ---- GetItem ----

    private async Task<Guid> AddCopyOfSameWorkAsync(TestLibrary library, Guid releaseId, string platformName)
    {
        await using var context = Db.CreateContext();
        var source = await context.Releases.SingleAsync(r => r.Id == releaseId);
        var platform = new Platform { Id = Guid.CreateVersion7(), Name = platformName, ShortName = "TC" };
        var release = new Release
        {
            Id = Guid.CreateVersion7(),
            WorkId = source.WorkId,
            Format = ReleaseFormat.Bluray,
            PlatformId = platform.Id,
            Source = ReleaseSource.Provider,
            CreatedAt = source.CreatedAt,
        };
        context.Platforms.Add(platform);
        context.Releases.Add(release);
        await context.SaveChangesAsync();
        return await TestData.CreateItemAsync(Db, library, release.Id);
    }

    [Fact]
    public async Task Other_copies_are_only_this_library_s_items_of_the_same_work_with_their_platform_name()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var releaseId = await TestData.CreateReleaseAsync(Db);
        var item = await TestData.CreateItemAsync(Db, library, releaseId);
        var copy = await AddCopyOfSameWorkAsync(library, releaseId, "Test Console");
        await TestData.CreateItemAsync(Db, library); // a different work

        var result = await new GetItem.Handler(Db.CreateContext(library.ActingAs(LibraryRole.Viewer)))
            .HandleAsync(new GetItem.Query(library.Id, item), CancellationToken.None);

        var copies = result.Value.Value.OtherCopies;
        copies.Count.ShouldBe(1);
        copies[0].ItemId.ShouldBe(copy);
        copies[0].Platform.ShouldBe("Test Console");
    }

    [Fact]
    public async Task An_item_with_no_other_copy_has_an_empty_list_and_a_copy_without_a_platform_has_no_platform_name()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var releaseId = await TestData.CreateReleaseAsync(Db);
        var item = await TestData.CreateItemAsync(Db, library, releaseId);
        var handler = new GetItem.Handler(Db.CreateContext(library.ActingAs(LibraryRole.Viewer)));

        (await handler.HandleAsync(new GetItem.Query(library.Id, item), CancellationToken.None)).Value.Value.OtherCopies.ShouldBeEmpty();

        await TestData.CreateItemAsync(Db, library, releaseId);
        var again = new GetItem.Handler(Db.CreateContext(library.ActingAs(LibraryRole.Viewer)));
        var copies = (await again.HandleAsync(new GetItem.Query(library.Id, item), CancellationToken.None)).Value.Value.OtherCopies;
        copies.Count.ShouldBe(1);
        copies[0].Platform.ShouldBeNull();
    }

    // ---- ListItems ----

    [Fact]
    public async Task A_limit_of_one_returns_one_item_and_a_cursor_and_a_limit_below_one_means_the_default()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        await TestData.CreateItemAsync(Db, library);
        await TestData.CreateItemAsync(Db, library);

        var one = await Lister(library).HandleAsync(ListQuery(library.Id, limit: 1), CancellationToken.None);
        var zero = await Lister(library).HandleAsync(ListQuery(library.Id, limit: 0), CancellationToken.None);

        one.Value.Items.Count.ShouldBe(1);
        one.Value.NextCursor.ShouldNotBeNull();
        zero.Value.Items.Count.ShouldBe(2);
        zero.Value.NextCursor.ShouldBeNull();
    }

    [Theory]
    [InlineData("year")]
    [InlineData("title")]
    [InlineData("-addedAt")]
    [InlineData("platform")]
    public async Task Paging_one_at_a_time_visits_every_item_once(string sort)
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var made = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            made.Add(await TestData.CreateItemAsync(Db, library));
        }

        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await Lister(library).HandleAsync(ListQuery(library.Id, sort, 1, cursor), CancellationToken.None);
            page.IsSuccess.ShouldBeTrue();
            seen.AddRange(page.Value.Items.Select(i => i.Id));
            cursor = page.Value.NextCursor;
        }
        while (cursor is not null && seen.Count < 10);

        seen.Count.ShouldBe(3);
        seen.ToHashSet().SetEquals(made).ShouldBeTrue();
    }

    [Fact]
    public async Task Listing_by_tag_returns_only_items_with_that_tag_and_none_for_another_name()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var tagged = await TestData.CreateItemAsync(Db, library);
        await TestData.CreateItemAsync(Db, library);
        var signed = await TestData.CreateTagAsync(Db, library.Id, "signed");
        var other = await TestData.CreateTagAsync(Db, library.Id, "other");
        await TestData.AttachTagAsync(Db, library.Id, tagged, signed);

        var withTag = await Lister(library).HandleAsync(ListQuery(library.Id, tag: "signed"), CancellationToken.None);
        var withOther = await Lister(library).HandleAsync(ListQuery(library.Id, tag: "other"), CancellationToken.None);

        withTag.Value.Items.Select(i => i.Id).ShouldBe([tagged]);
        withOther.Value.Items.ShouldBeEmpty();
        other.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task Listing_shows_this_library_s_items_and_not_another_one()
    {
        var mine = await TestData.CreateLibraryAsync(Db);
        var theirs = await TestData.CreateLibraryAsync(Db);
        var myItem = await TestData.CreateItemAsync(Db, mine);
        await TestData.CreateItemAsync(Db, theirs);

        var page = await Lister(mine).HandleAsync(ListQuery(mine.Id), CancellationToken.None);

        page.Value.Items.Select(i => i.Id).ShouldBe([myItem]);
    }

    // ---- RestoreItems ----

    [Fact]
    public async Task Restoring_a_list_with_an_unknown_id_fails_and_restores_nothing()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var item = await TestData.CreateItemAsync(Db, library);
        var delete = new DeleteItem.Handler(Db.CreateContext(library.ActingAs(LibraryRole.Editor)), Clock, new NoAudit());
        await using (var read = Db.CreateContext(library.ActingAs(LibraryRole.Editor)))
        {
            var version = (await read.Items.SingleAsync(i => i.Id == item)).Version;
            (await delete.HandleAsync(new DeleteItem.Command(library.Id, item, $"\"{version}\""), CancellationToken.None)).IsSuccess.ShouldBeTrue();
        }

        var handler = () => new RestoreItems.Handler(Db.CreateContext(library.ActingAs(LibraryRole.Editor)), Clock, new NoAudit());
        var bad = await handler().HandleAsync(new RestoreItems.Command(library.Id, [item, Guid.CreateVersion7()]), CancellationToken.None);
        bad.Error!.Code.ShouldBe("item.not_found");

        var good = await handler().HandleAsync(new RestoreItems.Command(library.Id, [item]), CancellationToken.None);
        good.IsSuccess.ShouldBeTrue();
        good.Value.Affected.ShouldBe(1);
    }

    // ---- UpdateItem ----

    private async Task<Result<Versioned<ItemDto>>> PatchAsync(TestLibrary library, Guid itemId, ItemPatch patch)
    {
        await using var read = Db.CreateContext(library.ActingAs(LibraryRole.Editor));
        var version = (await read.Items.SingleAsync(i => i.Id == itemId)).Version;
        var handler = new UpdateItem.Handler(Db.CreateContext(library.ActingAs(LibraryRole.Editor)), Clock);
        return await handler.HandleAsync(new UpdateItem.Command(library.Id, itemId, $"\"{version}\"", patch), CancellationToken.None);
    }

    [Fact]
    public async Task Moving_an_item_to_a_location_that_does_not_exist_is_refused()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var item = await TestData.CreateItemAsync(Db, library);

        var result = await PatchAsync(library, item, new ItemPatch { LocationId = PatchValue<Guid?>.Of(Guid.CreateVersion7()) });

        result.Error!.Code.ShouldBe("location.not_found");
    }

    [Fact]
    public async Task Setting_the_tag_list_adds_missing_tags_keeps_existing_ones_and_removes_the_rest()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var item = await TestData.CreateItemAsync(Db, library);
        var first = await TestData.CreateTagAsync(Db, library.Id, "first");
        var second = await TestData.CreateTagAsync(Db, library.Id, "second");
        var third = await TestData.CreateTagAsync(Db, library.Id, "third");

        (await PatchAsync(library, item, new ItemPatch { TagIds = [first] })).IsSuccess.ShouldBeTrue();
        (await TestData.CountItemTagsAsync(Db, library.Id)).ShouldBe(1);

        (await PatchAsync(library, item, new ItemPatch { TagIds = [first, second] })).IsSuccess.ShouldBeTrue();
        (await TestData.CountItemTagsAsync(Db, library.Id)).ShouldBe(2);

        (await PatchAsync(library, item, new ItemPatch { TagIds = [second, third] })).IsSuccess.ShouldBeTrue();
        (await TestData.CountItemTagsAsync(Db, library.Id)).ShouldBe(2);

        var result = await PatchAsync(library, item, new ItemPatch { TagIds = [] });
        result.IsSuccess.ShouldBeTrue();
        (await TestData.CountItemTagsAsync(Db, library.Id)).ShouldBe(0);
    }
}
