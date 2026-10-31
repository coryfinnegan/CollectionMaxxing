using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CollectionMaxxing.Application.Tests.Scanning.Learning;

/// <summary>
/// A barcode a library taught by hand is that library's own knowledge, so a release shown to another library lists only the
/// barcodes everyone shares and the ones that library taught itself.
/// </summary>
public class LearnedBarcodeVisibilityTests(PostgresFixture db) : DatabaseTest(db)
{
    private const string SharedCode = "0045496870010";
    private const string TaughtCode = "0045496830069";

    private sealed record Setup(TestLibrary Teacher, TestLibrary Other, Guid ReleaseId);

    /// <summary>A global release with a shared barcode, one more barcode the teacher library taught, and an item of it in each library.</summary>
    private async Task<Setup> SeedAsync()
    {
        var teacher = await TestData.CreateLibraryAsync(Db, "Teacher");
        var other = await TestData.CreateLibraryAsync(Db, "Other");
        var releaseId = await TestData.CreateReleaseAsync(Db);
        await using (var context = Db.CreateContext())
        {
            context.ReleaseIdentifiers.AddRange(
                new ReleaseIdentifier { ReleaseId = releaseId, Type = IdentifierType.Gtin13, Value = SharedCode },
                new ReleaseIdentifier { ReleaseId = releaseId, Type = IdentifierType.Gtin13, Value = TaughtCode, LibraryId = teacher.Id });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        return new Setup(teacher, other, releaseId);
    }

    private static List<string> Codes(ReleaseDto release) => [.. release.Identifiers.Select(i => i.Value)];

    [Fact]
    public async Task An_item_shows_the_barcodes_its_own_library_may_use()
    {
        var s = await SeedAsync();
        var teacherItem = await TestData.CreateItemAsync(Db, s.Teacher, s.ReleaseId);
        var otherItem = await TestData.CreateItemAsync(Db, s.Other, s.ReleaseId);

        await using var teacherContext = Db.CreateContext(s.Teacher.ActingAs(LibraryRole.Viewer));
        var mine = await new GetItem.Handler(teacherContext).HandleAsync(new GetItem.Query(s.Teacher.Id, teacherItem), CancellationToken.None);
        await using var otherContext = Db.CreateContext(s.Other.ActingAs(LibraryRole.Viewer));
        var theirs = await new GetItem.Handler(otherContext).HandleAsync(new GetItem.Query(s.Other.Id, otherItem), CancellationToken.None);

        Codes(mine.Value.Value.Release).ShouldBe([SharedCode, TaughtCode], ignoreOrder: true);
        Codes(theirs.Value.Value.Release).ShouldBe([SharedCode]);
    }

    [Fact]
    public async Task A_release_by_id_shows_the_barcodes_the_caller_may_use()
    {
        var s = await SeedAsync();

        await using var teacherContext = Db.CreateContext(s.Teacher.ActingAs(LibraryRole.Viewer));
        var mine = await new GetRelease.Handler(teacherContext, s.Teacher.ActingAs(LibraryRole.Viewer))
            .HandleAsync(new GetRelease.Query(s.ReleaseId), CancellationToken.None);
        await using var otherContext = Db.CreateContext(s.Other.ActingAs(LibraryRole.Viewer));
        var theirs = await new GetRelease.Handler(otherContext, s.Other.ActingAs(LibraryRole.Viewer))
            .HandleAsync(new GetRelease.Query(s.ReleaseId), CancellationToken.None);

        Codes(mine.Value).ShouldBe([SharedCode, TaughtCode], ignoreOrder: true);
        Codes(theirs.Value).ShouldBe([SharedCode]);
    }

    [Fact]
    public async Task A_release_by_id_with_no_known_caller_shows_only_the_shared_barcodes()
    {
        var s = await SeedAsync();

        await using var context = Db.CreateContext();
        var result = await new GetRelease.Handler(context).HandleAsync(new GetRelease.Query(s.ReleaseId), CancellationToken.None);

        Codes(result.Value).ShouldBe([SharedCode]);
    }

    [Fact]
    public async Task A_search_result_shows_the_barcodes_the_caller_may_use()
    {
        var s = await SeedAsync();

        var mine = await SearchAsync(s.Teacher, s.ReleaseId);
        var theirs = await SearchAsync(s.Other, s.ReleaseId);

        Codes(mine.Releases.Single()).ShouldBe([SharedCode, TaughtCode], ignoreOrder: true);
        Codes(theirs.Releases.Single()).ShouldBe([SharedCode]);
    }

    [Fact]
    public async Task A_barcode_lookup_finds_a_taught_code_for_the_teaching_library_only()
    {
        var s = await SeedAsync();

        var mine = await LookupAsync(s.Teacher, TaughtCode);
        var theirs = await LookupAsync(s.Other, TaughtCode);
        var sharedForTheirs = await LookupAsync(s.Other, SharedCode);

        mine.Releases.Select(r => r.Id).ShouldBe([s.ReleaseId]);
        theirs.Releases.ShouldBeEmpty();
        Codes(sharedForTheirs.Releases.Single()).ShouldBe([SharedCode]);
    }

    [Fact]
    public async Task A_cover_re_ask_never_asks_the_providers_by_an_isbn_only_a_library_taught()
    {
        const string taughtIsbn = "9780441172719";
        const string sharedIsbn = "9780306406157";
        var teacher = await TestData.CreateLibraryAsync(Db);
        var taughtOnly = await TestData.CreateReleaseAsync(Db);
        var sharedToo = await TestData.CreateReleaseAsync(Db);
        await using (var seed = Db.CreateContext())
        {
            seed.ReleaseIdentifiers.AddRange(
                new ReleaseIdentifier { ReleaseId = taughtOnly, Type = IdentifierType.Isbn13, Value = taughtIsbn, LibraryId = teacher.Id },
                new ReleaseIdentifier { ReleaseId = sharedToo, Type = IdentifierType.Isbn13, Value = sharedIsbn });
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var store = new RecordingIsbnStore();
        await using var context = Db.CreateContext();
        var resolver = new CoverReResolver(
            context, new BookLookup([], store, NullLogger<BookLookup>.Instance), new CatalogOptions(), new ScanningOptions());

        await resolver.ReResolveAsync(
            await context.Releases.SingleAsync(r => r.Id == taughtOnly, TestContext.Current.CancellationToken), DateTimeOffset.UnixEpoch, CancellationToken.None);
        store.Asked.ShouldBeEmpty();

        await resolver.ReResolveAsync(
            await context.Releases.SingleAsync(r => r.Id == sharedToo, TestContext.Current.CancellationToken), DateTimeOffset.UnixEpoch, CancellationToken.None);
        store.Asked.ShouldBe([sharedIsbn]);
    }

    private async Task<CatalogSearchResultDto> SearchAsync(TestLibrary library, Guid releaseId)
    {
        await using var context = Db.CreateContext(library.ActingAs(LibraryRole.Viewer));
        var release = await context.Releases.AsNoTracking().SingleAsync(r => r.Id == releaseId, TestContext.Current.CancellationToken);
        var work = await context.Works.AsNoTracking().SingleAsync(w => w.Id == release.WorkId, TestContext.Current.CancellationToken);
        var handler = new SearchCatalog.Handler(
            context,
            new FixedLocalSearch([new LocalHit(release, work)]),
            [],
            new CatalogOptions(),
            NullLogger<SearchCatalog.Handler>.Instance,
            currentUser: library.ActingAs(LibraryRole.Viewer));

        var result = await handler.HandleAsync(new SearchCatalog.Query("jurassic park", null, null, null, null), CancellationToken.None);

        return result.Value.Items.Single();
    }

    private async Task<CatalogLookupDto> LookupAsync(TestLibrary library, string code)
    {
        await using var context = Db.CreateContext(library.ActingAs(LibraryRole.Viewer));
        var books = new BookLookup([], new EmptyIsbnStore(), NullLogger<BookLookup>.Instance);
        var handler = new LookupCatalog.Handler(
            context, [], books, new CatalogOptions(), NullLogger<LookupCatalog.Handler>.Instance, library.ActingAs(LibraryRole.Viewer));

        return (await handler.HandleAsync(new LookupCatalog.Query(code), CancellationToken.None)).Value;
    }

    private sealed class FixedLocalSearch(IReadOnlyList<LocalHit> hits) : ILocalCatalogSearch
    {
        public Task<IReadOnlyList<LocalHit>> FindAsync(LocalSearch search, CancellationToken cancellationToken) =>
            Task.FromResult(hits);
    }

    private class EmptyIsbnStore : IIsbnStore
    {
        public virtual Task<ProviderRelease?> FindAsync(string isbn13, CancellationToken ct) => Task.FromResult<ProviderRelease?>(null);

        public Task SaveAsync(string isbn13, ProviderRelease release, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Remembers which ISBNs the book lookup was asked about.</summary>
    private sealed class RecordingIsbnStore : EmptyIsbnStore
    {
        public List<string> Asked { get; } = [];

        public override Task<ProviderRelease?> FindAsync(string isbn13, CancellationToken ct)
        {
            Asked.Add(isbn13);
            return base.FindAsync(isbn13, ct);
        }
    }
}
