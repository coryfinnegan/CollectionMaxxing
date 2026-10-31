using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Deletion;

/// <summary>What the purge of a deleted account leaves of the catalogue: nothing a library made for itself, and none of the shared rows touched.</summary>
public class AccountPurgeCatalogueTests(ApiFixture api) : DeletionTest(api)
{
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    [Fact]
    public async Task The_purge_leaves_no_work_that_only_the_librarys_own_releases_used_and_keeps_every_shared_release_and_work()
    {
        var (browser, owner) = await SetUpOwnerAsync();
        var libraryId = owner.LibraryId!.Value;
        await SeedLibraryAsync(libraryId, owner.UserId); // two releases: one the library made (with a work of its own), one shared

        // A release the library made on a work the shared catalogue also has, and another library's manual release that must not be touched.
        Guid sharedWorkId;
        await using (var context = Db.CreateContext())
        {
            sharedWorkId = await context.Releases.IgnoreQueryFilters().Where(r => r.OwnerLibraryId == null).Select(r => r.WorkId).SingleAsync();
            context.Releases.Add(new Release
            {
                Id = Guid.CreateVersion7(), WorkId = sharedWorkId, Format = ReleaseFormat.Dvd, Source = ReleaseSource.User, OwnerLibraryId = libraryId, CreatedAt = SeedTime,
            });
            await context.SaveChangesAsync();
        }

        var other = await CreateLibraryAsync();
        var otherRelease = await TestData.CreateReleaseAsync(Db, other.Id, "Somebody else's tape");
        var sharedReleases = await ReleaseIdsAsync(shared: true);

        (await DeleteAccountAsync(browser)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        App.Time.Advance(30 * Day + TimeSpan.FromSeconds(1));
        await RunDueJobsAsync();

        await using var check = Db.CreateContext();
        (await check.Releases.IgnoreQueryFilters().CountAsync(r => r.OwnerLibraryId == libraryId)).ShouldBe(0);
        (await ReleaseIdsAsync(shared: true)).ShouldBe(sharedReleases, "the shared releases are untouched");
        var worksLeft = await check.Works.Select(w => w.Id).ToListAsync();
        worksLeft.ShouldContain(sharedWorkId);
        (await check.Works.CountAsync(w => !check.Releases.IgnoreQueryFilters().Any(r => r.WorkId == w.Id))).ShouldBe(0, "no work is left with nothing pointing at it");
        worksLeft.Count.ShouldBe(2, "the shared work and the other library's own, and nothing else");
        (await check.Releases.IgnoreQueryFilters().AnyAsync(r => r.Id == otherRelease)).ShouldBeTrue();
    }

    private async Task<List<Guid>> ReleaseIdsAsync(bool shared)
    {
        await using var context = Db.CreateContext();
        return await context.Releases.IgnoreQueryFilters().Where(r => (r.OwnerLibraryId == null) == shared).OrderBy(r => r.Id).Select(r => r.Id).ToListAsync();
    }
}
