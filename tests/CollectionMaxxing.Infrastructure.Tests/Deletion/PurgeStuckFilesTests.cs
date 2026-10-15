using CollectionMaxxing.Application.Accounts.Deletion;
using CollectionMaxxing.Application.Audit;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Exports;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Infrastructure.Audit;
using CollectionMaxxing.Infrastructure.Exports;
using CollectionMaxxing.Infrastructure.Ids;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CollectionMaxxing.Infrastructure.Tests.Deletion;

// LIB-2d fix round (PR #106): a file the purge can't delete is never forgotten and never blocks erasing the person.
public class PurgeStuckFilesTests(PostgresFixture db) : DatabaseTest(db)
{
    private static readonly DateTimeOffset DeletedAt = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Due = DeletedAt.AddDays(30).AddSeconds(1);

    private sealed class FakeAccounts : IDeletedAccounts
    {
        public int Erasures { get; private set; }

        public Task<DateTimeOffset?> DeletedAtAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult<DateTimeOffset?>(DeletedAt);

        public Task ScrubAuditTrailAsync(Guid userId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EraseAsync(Guid userId, CancellationToken cancellationToken)
        {
            Erasures++;
            return Task.CompletedTask;
        }
    }

    private sealed class LockableFiles(IEnumerable<string> existing) : IAccountFiles
    {
        public HashSet<string> Existing { get; } = [.. existing];

        public HashSet<string> Locked { get; } = [];

        public Task DeleteAsync(string blobPath, CancellationToken cancellationToken) => TryDeleteAsync(blobPath, cancellationToken);

        public Task<bool> TryDeleteAsync(string blobPath, CancellationToken cancellationToken)
        {
            if (Locked.Contains(blobPath))
            {
                return Task.FromResult(false);
            }

            Existing.Remove(blobPath);
            return Task.FromResult(true);
        }
    }

    private async Task RunAsync(FakeAccounts accounts, LockableFiles files, Guid userId)
    {
        var time = new FakeTimeProvider(Due);
        await using var context = Db.CreateContext();
        var audit = new AuditLog(context, new CurrentUser(), new NoRequestOrigin(), time, new Uuid7IdGenerator(time), Options.Create(new AuditOptions()));
        await new PurgeAccount.Handler(context, time, accounts, files, [], new AnonymiseAuditActor.Handler(context), audit)
            .HandleAsync(new PurgeAccountPayload(userId), CancellationToken.None);
    }

    [Fact]
    public async Task A_stuck_image_keeps_its_record_and_the_library_while_the_identity_is_erased_and_a_later_run_finishes_the_job()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        await using (var context = Db.CreateContext())
        {
            await context.LibraryMembers.IgnoreQueryFilters().Where(m => m.LibraryId == library.Id && m.Role != LibraryRole.Owner).ExecuteDeleteAsync();
        }

        var paths = new List<string>();
        await using (var context = Db.CreateContext())
        {
            foreach (var kind in new[] { AssetKind.Cover, AssetKind.UserPhoto })
            {
                var id = Guid.CreateVersion7();
                paths.Add($"ab/cd/{id:N}.jpg");
                context.Assets.Add(new Asset { Id = id, Kind = kind, BlobPath = paths[^1], ContentType = "image/jpeg", Sha256 = id.ToString("N"), OwnerLibraryId = library.Id, CreatedAt = DeletedAt });
            }

            await context.SaveChangesAsync();
        }

        var files = new LockableFiles(paths);
        files.Locked.Add(paths[0]);
        var accounts = new FakeAccounts();

        await RunAsync(accounts, files, library.OwnerId);

        accounts.Erasures.ShouldBe(1, "a stuck file does not hold back erasing the person");
        files.Existing.ShouldBe([paths[0]]);
        await using (var context = Db.CreateContext())
        {
            (await context.Assets.IgnoreQueryFilters().Where(a => a.OwnerLibraryId == library.Id).Select(a => a.BlobPath).ToListAsync())
                .ShouldBe([paths[0]], "only the record of the stuck file is kept");
            (await context.Libraries.IgnoreQueryFilters().AnyAsync(l => l.Id == library.Id)).ShouldBeTrue();
            (await context.LibraryMembers.IgnoreQueryFilters().AnyAsync(m => m.UserId == library.OwnerId)).ShouldBeTrue("this is what the daily sweep finds the person by");
        }

        await RunAsync(accounts, files, library.OwnerId); // still locked
        files.Existing.ShouldBe([paths[0]]);

        files.Locked.Clear();
        await RunAsync(accounts, files, library.OwnerId);

        files.Existing.ShouldBeEmpty();
        await using var after = Db.CreateContext();
        (await after.Assets.IgnoreQueryFilters().AnyAsync(a => a.OwnerLibraryId == library.Id)).ShouldBeFalse();
        (await after.Libraries.IgnoreQueryFilters().AnyAsync(l => l.Id == library.Id)).ShouldBeFalse();
        (await after.LibraryMembers.IgnoreQueryFilters().AnyAsync(m => m.UserId == library.OwnerId)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_stuck_export_file_keeps_its_job_row_and_the_rest_are_removed()
    {
        var jobs = new FakeExportJobs(Guid.NewGuid(), Guid.NewGuid());
        var store = new FakeExportStore();
        store.Locked.Add(jobs.Ids[0]);

        await new PurgeUserExports(jobs, store).PurgeAsync(Guid.NewGuid(), CancellationToken.None);

        jobs.Ids.ShouldBe([jobs.Ids[0]], "the record of the stuck export stays");
        await new PurgeUserExports(jobs, store).PurgeAsync(Guid.NewGuid(), CancellationToken.None);
        jobs.Ids.Count.ShouldBe(1);

        store.Locked.Clear();
        await new PurgeUserExports(jobs, store).PurgeAsync(Guid.NewGuid(), CancellationToken.None);
        jobs.Ids.ShouldBeEmpty("gone with its file");
    }

    [Fact]
    public async Task A_locked_export_file_is_reported_as_stuck_instead_of_throwing()
    {
        var root = Path.Combine(Path.GetTempPath(), "cm-exports-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileExportStore(root);
            var id = Guid.NewGuid();

            // A non-empty folder where the file should be: deleting it fails on every operating system, like a locked file.
            var path = Path.Combine(root, id.ToString("N") + ".zip");
            Directory.CreateDirectory(path);
            await File.WriteAllTextAsync(Path.Combine(path, "inside"), "x");

            (await store.TryDeleteAsync(id, CancellationToken.None)).ShouldBeFalse();
            (await store.TryDeleteAsync(Guid.NewGuid(), CancellationToken.None)).ShouldBeTrue("a file that is not there is gone");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FakeExportJobs : IExportJobs
    {
        public FakeExportJobs(params Guid[] ids) => Ids = [.. ids];

        public List<Guid> Ids { get; private set; }

        public Task<ExportJobState?> FindAsync(Guid exportId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Guid?> FindActiveAsync(Guid userId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> ListIdsForUserAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Guid>>(Ids.ToList());

        public Task DeleteForUserAsync(Guid userId, CancellationToken cancellationToken)
        {
            Ids = [];
            return Task.CompletedTask;
        }

        public Task DeleteForUserAsync(Guid userId, IReadOnlyCollection<Guid> exportIds, CancellationToken cancellationToken)
        {
            Ids = Ids.Where(id => !exportIds.Contains(id)).ToList();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeExportStore : IExportStore
    {
        public HashSet<Guid> Locked { get; } = [];

        public Task SaveAsync(Guid exportId, DateTimeOffset createdAt, Func<Stream, CancellationToken, Task> write, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public bool Exists(Guid exportId) => false;

        public Task<Stream?> OpenReadAsync(Guid exportId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteAsync(Guid exportId, CancellationToken cancellationToken) => TryDeleteAsync(exportId, cancellationToken);

        public Task<bool> TryDeleteAsync(Guid exportId, CancellationToken cancellationToken) => Task.FromResult(!Locked.Contains(exportId));

        public Task<int> DeleteCreatedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) => Task.FromResult(0);
    }
}
