using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CollectionMaxxing.Infrastructure.Tests.Persistence;

/// <summary>
/// A barcode link is either shared (from a provider or the catalogue: no library) or learned by one library (its hand pick).
/// The same code may be learned separately by several libraries for the same release; a shared link exists once.
/// </summary>
public class LearnedBarcodeSchemaTests(PostgresFixture db) : DatabaseTest(db)
{
    private const string Code = "0045496870010";

    private static ReleaseIdentifier Link(Guid releaseId, Guid? libraryId) =>
        new() { ReleaseId = releaseId, Type = IdentifierType.Gtin13, Value = Code, LibraryId = libraryId };

    private async Task AddAsync(params ReleaseIdentifier[] links)
    {
        await using var context = Db.CreateContext();
        context.ReleaseIdentifiers.AddRange(links);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<Guid?>> LibrariesOfAsync(Guid releaseId)
    {
        await using var context = Db.CreateContext();
        return await context.ReleaseIdentifiers.AsNoTracking().Where(i => i.ReleaseId == releaseId).Select(i => i.LibraryId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Two_libraries_can_each_learn_the_same_barcode_for_the_same_release()
    {
        var first = await TestData.CreateLibraryAsync(Db);
        var second = await TestData.CreateLibraryAsync(Db);
        var release = await TestData.CreateReleaseAsync(Db);

        await AddAsync(Link(release, first.Id), Link(release, second.Id));

        (await LibrariesOfAsync(release)).ShouldBe([first.Id, second.Id], ignoreOrder: true);
    }

    [Fact]
    public async Task A_library_learns_a_barcode_for_a_release_only_once()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var release = await TestData.CreateReleaseAsync(Db);
        await AddAsync(Link(release, library.Id));

        await using var duplicate = Db.CreateContext();
        duplicate.ReleaseIdentifiers.Add(Link(release, library.Id));

        await SchemaAssert.FailsWithAsync(PostgresErrorCodes.UniqueViolation, () => duplicate.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_shared_barcode_link_exists_once_per_release_even_though_it_has_no_library()
    {
        var release = await TestData.CreateReleaseAsync(Db);
        await AddAsync(Link(release, null));

        await using var duplicate = Db.CreateContext();
        duplicate.ReleaseIdentifiers.Add(Link(release, null));

        await SchemaAssert.FailsWithAsync(PostgresErrorCodes.UniqueViolation, () => duplicate.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_library_leaving_takes_the_barcodes_it_taught_and_leaves_the_shared_ones()
    {
        var leaving = await TestData.CreateLibraryAsync(Db);
        var staying = await TestData.CreateLibraryAsync(Db);
        var release = await TestData.CreateReleaseAsync(Db);
        var other = await TestData.CreateReleaseAsync(Db);
        await AddAsync(Link(release, leaving.Id), Link(release, staying.Id), Link(other, null));

        await using (var context = Db.CreateContext())
        {
            await context.Libraries.IgnoreQueryFilters().Where(l => l.Id == leaving.Id).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }

        (await LibrariesOfAsync(release)).ShouldBe([staying.Id]);
        (await LibrariesOfAsync(other)).ShouldBe([null]);
    }

    [Fact]
    public async Task A_barcode_link_names_a_library_that_exists()
    {
        var release = await TestData.CreateReleaseAsync(Db);

        await using var context = Db.CreateContext();
        context.ReleaseIdentifiers.Add(Link(release, Guid.CreateVersion7()));

        await SchemaAssert.FailsWithAsync(PostgresErrorCodes.ForeignKeyViolation, () => context.SaveChangesAsync(TestContext.Current.CancellationToken));
    }
}
