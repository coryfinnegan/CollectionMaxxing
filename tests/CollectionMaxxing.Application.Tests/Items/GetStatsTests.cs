using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;

namespace CollectionMaxxing.Application.Tests.Items;

public class GetStatsTests(PostgresFixture db) : DatabaseTest(db)
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private async Task<StatsDto> StatsAsync(TestLibrary library)
    {
        var handler = new GetStats.Handler(Db.CreateContext(library.ActingAs(LibraryRole.Viewer)));
        return await handler.HandleAsync(new GetStats.Query(library.Id), CancellationToken.None);
    }

    private async Task<Guid> AddPlatformAsync(string name)
    {
        var platform = new Platform { Id = Guid.CreateVersion7(), Name = name, ShortName = "P" };
        await using var context = Db.CreateContext();
        context.Platforms.Add(platform);
        await context.SaveChangesAsync();
        return platform.Id;
    }

    /// <summary>A copy of a new release, or of <paramref name="releaseId"/> when given. Returns the release id.</summary>
    private async Task<Guid> AddCopyAsync(
        TestLibrary library,
        WorkKind kind,
        ReleaseFormat format,
        Guid? platformId = null,
        DateOnly? releaseDate = null,
        int? originalYear = null,
        ItemStatus status = ItemStatus.Owned,
        bool deleted = false,
        Guid? releaseId = null)
    {
        await using var context = Db.CreateContext();
        if (releaseId is null)
        {
            var work = new Work
            {
                Id = Guid.CreateVersion7(),
                Kind = kind,
                Title = "Title",
                SortTitle = "title",
                NormalizedTitle = "title",
                OriginalYear = originalYear,
                CreatedAt = Now,
            };
            var release = new Release
            {
                Id = Guid.CreateVersion7(),
                WorkId = work.Id,
                Format = format,
                PlatformId = platformId,
                ReleaseDate = releaseDate,
                Source = ReleaseSource.Provider,
                CreatedAt = Now,
            };
            context.Works.Add(work);
            context.Releases.Add(release);
            releaseId = release.Id;
        }

        context.Items.Add(new Item
        {
            Id = Guid.CreateVersion7(),
            LibraryId = library.Id,
            ReleaseId = releaseId.Value,
            Status = status,
            CoverChoice = CoverChoice.Release,
            AddedVia = AddedVia.Manual,
            DeletedAt = deleted ? Now : null,
            CreatedAt = Now,
            CreatedBy = library.OwnerId,
            UpdatedAt = Now,
        });
        await context.SaveChangesAsync();
        return releaseId.Value;
    }

    [Fact]
    public async Task An_empty_library_has_no_counts()
    {
        var library = await TestData.CreateLibraryAsync(Db);

        var stats = await StatsAsync(library);

        stats.Total.ShouldBe(0);
        stats.ByKind.ShouldBeEmpty();
        stats.ByFormat.ShouldBeEmpty();
        stats.ByPlatform.ShouldBeEmpty();
        stats.ByDecade.ShouldBeEmpty();
    }

    [Fact]
    public async Task Every_live_copy_counts_once_by_kind_format_platform_and_decade_in_ordinal_key_order()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var n64 = await AddPlatformAsync("Nintendo 64");
        var ps2 = await AddPlatformAsync("PlayStation 2");
        var atari = await AddPlatformAsync("atari 2600");

        // The release date, not the work's year, picks the decade when there is one.
        await AddCopyAsync(library, WorkKind.Game, ReleaseFormat.GameCartridge, n64, new DateOnly(2001, 5, 1), originalYear: 1998);
        var zelda = await AddCopyAsync(library, WorkKind.Game, ReleaseFormat.GameCartridge, n64, originalYear: 1996);
        await AddCopyAsync(library, WorkKind.Game, ReleaseFormat.GameCartridge, n64, releaseId: zelda); // a second copy counts again
        await AddCopyAsync(library, WorkKind.Game, ReleaseFormat.GameDisc, ps2, new DateOnly(2006, 11, 17), status: ItemStatus.Wishlist);
        await AddCopyAsync(library, WorkKind.Game, ReleaseFormat.GameCartridge, atari, originalYear: 1982);
        await AddCopyAsync(library, WorkKind.Film, ReleaseFormat.Vhs, originalYear: 1979, status: ItemStatus.Lent);
        await AddCopyAsync(library, WorkKind.Film, ReleaseFormat.Dvd); // no year at all: counted, but in no decade
        await AddCopyAsync(library, WorkKind.Book, ReleaseFormat.Paperback, releaseDate: new DateOnly(1965, 9, 1), originalYear: 2010);
        await AddCopyAsync(library, WorkKind.Book, ReleaseFormat.Unknown, originalYear: 995);

        // Not counted: a deleted copy, and another library's copy.
        await AddCopyAsync(library, WorkKind.Book, ReleaseFormat.Hardcover, originalYear: 1950, deleted: true);
        await AddCopyAsync(await TestData.CreateLibraryAsync(Db), WorkKind.Tv, ReleaseFormat.Dvd, originalYear: 1950);

        var stats = await StatsAsync(library);

        stats.Total.ShouldBe(9);
        InOrder(stats.ByKind).ShouldBe([("book", 2), ("film", 2), ("game", 5)]);
        InOrder(stats.ByFormat).ShouldBe(
            [("dvd", 1), ("game_cartridge", 4), ("game_disc", 1), ("paperback", 1), ("unknown", 1), ("vhs", 1)]);
        InOrder(stats.ByPlatform).ShouldBe([("Nintendo 64", 3), ("PlayStation 2", 1), ("atari 2600", 1)]);
        InOrder(stats.ByDecade).ShouldBe([("1960s", 1), ("1970s", 1), ("1980s", 1), ("1990s", 2), ("2000s", 2), ("990s", 1)]);
    }

    private static IEnumerable<(string Key, int Count)> InOrder(IReadOnlyDictionary<string, int> counts) =>
        counts.Select(count => (count.Key, count.Value));
}
