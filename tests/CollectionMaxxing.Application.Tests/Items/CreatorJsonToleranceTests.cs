using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CollectionMaxxing.Application.Tests.Items;

// Issue #286: a stored creator object whose name is not a string must not break reads.
public class CreatorJsonToleranceTests(PostgresFixture db) : DatabaseTest(db)
{
    private async Task<Guid> CreateReleaseWithNumericCreatorNameAsync()
    {
        var releaseId = await TestData.CreateReleaseAsync(Db);
        await using var context = Db.CreateContext();
        var release = await context.Releases.SingleAsync(r => r.Id == releaseId);
        var work = await context.Works.SingleAsync(w => w.Id == release.WorkId);
        work.CreatorsJson = """[{"name": 123}]""";
        await context.SaveChangesAsync();
        return releaseId;
    }

    [Fact]
    public async Task Item_detail_renders_when_a_stored_creator_name_is_not_a_string()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var releaseId = await CreateReleaseWithNumericCreatorNameAsync();
        var item = await TestData.CreateItemAsync(Db, library, releaseId);

        var result = await new GetItem.Handler(Db.CreateContext(library.ActingAs(LibraryRole.Viewer)))
            .HandleAsync(new GetItem.Query(library.Id, item), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Value.Work.Title.ShouldBe("Jurassic Park");
        result.Value.Value.Work.Creators.ShouldBeEmpty();
        result.Value.Value.Release.Creators.ShouldBeEmpty();
    }

    [Fact]
    public async Task Release_renders_when_a_stored_creator_name_is_not_a_string()
    {
        var releaseId = await CreateReleaseWithNumericCreatorNameAsync();

        var result = await new GetRelease.Handler(Db.CreateContext())
            .HandleAsync(new GetRelease.Query(releaseId), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Creators.ShouldBeEmpty();
    }

    [Fact]
    public async Task Search_renders_a_release_whose_stored_creator_name_is_not_a_string()
    {
        var releaseId = await CreateReleaseWithNumericCreatorNameAsync();
        await using var context = Db.CreateContext();
        var release = await context.Releases.AsNoTracking().SingleAsync(r => r.Id == releaseId);
        var work = await context.Works.AsNoTracking().SingleAsync(w => w.Id == release.WorkId);

        var handler = new SearchCatalog.Handler(
            Db.CreateContext(),
            new FixedLocalSearch([new LocalHit(release, work)]),
            [],
            new CatalogOptions(),
            NullLogger<SearchCatalog.Handler>.Instance);
        var result = await handler.HandleAsync(new SearchCatalog.Query("jurassic park", null, null, null, null), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        var hit = result.Value.Items.ShouldHaveSingleItem();
        hit.Work.Title.ShouldBe("Jurassic Park");
        hit.Work.Creators.ShouldBeEmpty();
        hit.Releases.ShouldHaveSingleItem().Creators.ShouldBeEmpty();
    }

    private sealed class FixedLocalSearch(IReadOnlyList<LocalHit> hits) : ILocalCatalogSearch
    {
        public Task<IReadOnlyList<LocalHit>> FindAsync(LocalSearch search, CancellationToken cancellationToken) =>
            Task.FromResult(hits);
    }
}
