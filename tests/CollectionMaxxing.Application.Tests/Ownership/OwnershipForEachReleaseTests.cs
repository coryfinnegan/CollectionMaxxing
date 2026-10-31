using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json;
using CollectionMaxxing.Application.Ownership;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CollectionMaxxing.Application.Tests.Ownership;

/// <summary>
/// A scan of a whole shelf asks "do I own this?" of every release it might point at. Asking once for all of them must give each
/// release exactly the answer asking about it alone gives, in a fixed number of queries.
/// </summary>
public class OwnershipForEachReleaseTests(PostgresFixture db) : DatabaseTest(db)
{
    private static readonly DateTimeOffset Created = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private async Task<Guid> ReleaseAsync(
        string title, ReleaseFormat format, Guid? workId = null, Guid? platformId = null, Guid? ownerLibraryId = null)
    {
        await using var context = Db.CreateContext();
        var work = workId is null
            ? new Work { Id = Guid.CreateVersion7(), Kind = WorkKind.Game, Title = title, SortTitle = title, NormalizedTitle = title.ToLowerInvariant(), CreatedAt = Created }
            : null;
        if (work is not null)
        {
            context.Works.Add(work);
        }

        var release = new Release
        {
            Id = Guid.CreateVersion7(),
            WorkId = workId ?? work!.Id,
            Format = format,
            PlatformId = platformId,
            Source = ownerLibraryId is null ? ReleaseSource.Provider : ReleaseSource.User,
            OwnerLibraryId = ownerLibraryId,
            CreatedAt = Created,
        };
        context.Releases.Add(release);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return release.Id;
    }

    private async Task<Guid> WorkOfAsync(Guid releaseId)
    {
        await using var context = Db.CreateContext();
        return await context.Releases.IgnoreQueryFilters().Where(r => r.Id == releaseId).Select(r => r.WorkId).SingleAsync(TestContext.Current.CancellationToken);
    }

    private async Task CopyAsync(
        TestLibrary library, Guid releaseId, ItemStatus status = ItemStatus.Owned, Guid? locationId = null, bool deleted = false, int minute = 0)
    {
        await using var context = Db.CreateContext();
        context.Items.Add(new Item
        {
            Id = Guid.CreateVersion7(),
            LibraryId = library.Id,
            ReleaseId = releaseId,
            Status = status,
            LocationId = locationId,
            DeletedAt = deleted ? Created : null,
            CoverChoice = CoverChoice.Release,
            AddedVia = AddedVia.Manual,
            CreatedAt = Created.AddMinutes(minute),
            CreatedBy = library.OwnerId,
            UpdatedAt = Created,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static string Json(OwnershipDto owned) => JsonSerializer.Serialize(owned);

    [Fact]
    public async Task Each_release_gets_the_answer_asking_about_it_alone_gives()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var other = await TestData.CreateLibraryAsync(Db);
        var shelf = await TestData.CreateLocationAsync(Db, library.Id, "Shelf");
        var top = await TestData.CreateLocationAsync(Db, library.Id, "Top", shelf);

        // One work in three formats: one exactly owned twice, one owned only as another format of the work, one not owned at all.
        var dvd = await ReleaseAsync("The Matrix", ReleaseFormat.Dvd);
        var work = await WorkOfAsync(dvd);
        var vhs = await ReleaseAsync("The Matrix", ReleaseFormat.Vhs, work);
        var bluray = await ReleaseAsync("The Matrix", ReleaseFormat.Bluray, work);
        await CopyAsync(library, dvd, locationId: top, minute: 2);
        await CopyAsync(library, dvd, ItemStatus.Lent, minute: 1);
        await CopyAsync(library, vhs, minute: 3);

        // Things that never count: a copy that is wishlisted or deleted, another library's copy, a work nobody owns, a private release of another library.
        var alone = await ReleaseAsync("Kindred", ReleaseFormat.Paperback);
        var wished = await ReleaseAsync("Solaris", ReleaseFormat.Paperback);
        await CopyAsync(library, wished, ItemStatus.Wishlist);
        var gone = await ReleaseAsync("Gateway", ReleaseFormat.Paperback);
        await CopyAsync(library, gone, deleted: true);
        var theirs = await ReleaseAsync("Hyperion", ReleaseFormat.Paperback);
        await CopyAsync(other, theirs);
        var hidden = await ReleaseAsync("Ubik", ReleaseFormat.Paperback, ownerLibraryId: other.Id);

        var ids = new[] { dvd, vhs, bluray, alone, wished, gone, theirs, hidden, Guid.CreateVersion7() };
        await using var context = Db.CreateContext(library.ActingAs(LibraryRole.Editor));
        var ownership = new OwnershipQuery(context);

        var each = await ownership.ForEachReleaseAsync(library.Id, ids, TestContext.Current.CancellationToken);

        each.Keys.ShouldBe(ids, ignoreOrder: true);
        foreach (var id in ids)
        {
            var alonesAnswer = await ownership.ForReleaseAsync(library.Id, id, TestContext.Current.CancellationToken);
            Json(each[id]).ShouldBe(Json(alonesAnswer), $"release {id}");
        }

        each[dvd].Count.ShouldBe(2);
        each[dvd].Copies.Select(c => c.LocationPath).ShouldBe([null, "Shelf › Top"]); // oldest copy first; the lent one has no place
        each[dvd].SameWorkOtherFormat.Count.ShouldBe(1);
        each[bluray].Owned.ShouldBeFalse();
        each[bluray].SameWorkOtherFormat.Count.ShouldBe(3);
        each[hidden].Owned.ShouldBeFalse();
    }

    [Fact]
    public async Task Missing_counts_as_owned_and_wishlist_and_sold_are_named_without_counting_the_same_for_a_whole_shelf_as_for_one_release()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var other = await TestData.CreateLibraryAsync(Db);

        var lost = await ReleaseAsync("Pong", ReleaseFormat.GameCartridge);
        var want = await ReleaseAsync("Metroid", ReleaseFormat.GameCartridge);
        var letGo = await ReleaseAsync("Tetris", ReleaseFormat.GameCartridge);
        var everything = await ReleaseAsync("Zelda", ReleaseFormat.GameCartridge);
        var theirs = await ReleaseAsync("Mario", ReleaseFormat.GameCartridge);
        await CopyAsync(library, lost, ItemStatus.Missing);
        await CopyAsync(library, want, ItemStatus.Wishlist, minute: 1);
        await CopyAsync(library, want, ItemStatus.Wishlist, minute: 2);
        await CopyAsync(library, letGo, ItemStatus.Sold, minute: 1);
        await CopyAsync(library, letGo, ItemStatus.Sold, minute: 5);
        await CopyAsync(library, everything, ItemStatus.Owned, minute: 1);
        await CopyAsync(library, everything, ItemStatus.Wishlist, minute: 2);
        await CopyAsync(library, everything, ItemStatus.Sold, minute: 3);
        await CopyAsync(other, theirs, ItemStatus.Wishlist);
        await CopyAsync(other, theirs, ItemStatus.Sold);

        var ids = new[] { lost, want, letGo, everything, theirs };
        await using var context = Db.CreateContext(library.ActingAs(LibraryRole.Editor));
        var ownership = new OwnershipQuery(context);

        var each = await ownership.ForEachReleaseAsync(library.Id, ids, TestContext.Current.CancellationToken);

        foreach (var id in ids)
        {
            Json(each[id]).ShouldBe(Json(await ownership.ForReleaseAsync(library.Id, id, TestContext.Current.CancellationToken)), $"release {id}");
        }

        each[lost].Owned.ShouldBeTrue(); // still yours, you just can't find it
        each[lost].Copies.Single().Status.ShouldBe(ItemStatus.Missing);
        each[want].Owned.ShouldBeFalse();
        each[want].WishlistItemId.ShouldNotBeNull();
        each[want].SoldItemId.ShouldBeNull();
        each[letGo].Owned.ShouldBeFalse();
        each[letGo].SoldItemId.ShouldNotBeNull();
        each[letGo].WishlistItemId.ShouldBeNull();
        each[everything].Count.ShouldBe(1);
        each[everything].WishlistItemId.ShouldNotBeNull();
        each[everything].SoldItemId.ShouldNotBeNull();
        each[theirs].WishlistItemId.ShouldBeNull(); // another library's wishlist never shows
        each[theirs].SoldItemId.ShouldBeNull();
    }

    [Fact]
    public async Task A_whole_shelf_is_asked_in_a_fixed_number_of_queries()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var shelf = await TestData.CreateLocationAsync(Db, library.Id, "Shelf");
        var ids = new List<Guid>();
        for (var i = 0; i < 40; i++)
        {
            var release = await ReleaseAsync($"Title {i}", ReleaseFormat.Paperback);
            ids.Add(release);
            if (i % 3 == 0)
            {
                await CopyAsync(library, release, locationId: shelf);
            }
        }

        var counter = new CommandCounter();
        await using var context = Db.CreateContext(library.ActingAs(LibraryRole.Editor), counter);

        var each = await new OwnershipQuery(context).ForEachReleaseAsync(library.Id, ids, TestContext.Current.CancellationToken);

        each.Count.ShouldBe(40);
        each.Values.Count(o => o.Owned).ShouldBe(14);
        counter.Count.ShouldBeLessThanOrEqualTo(3); // the releases, the copies of their works, the locations
    }

    [Fact]
    public async Task Asking_about_nothing_runs_no_query()
    {
        var counter = new CommandCounter();
        await using var context = Db.CreateContext(null, counter);

        var each = await new OwnershipQuery(context).ForEachReleaseAsync(Guid.CreateVersion7(), [], TestContext.Current.CancellationToken);

        each.ShouldBeEmpty();
        counter.Count.ShouldBe(0);
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<string> _commands = new();

        public int Count => _commands.Count;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            _commands.Enqueue(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
