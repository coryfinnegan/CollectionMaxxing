using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Accounts.Deletion;
using CollectionMaxxing.Application.Exports;
using CollectionMaxxing.Application.Jobs;
using CollectionMaxxing.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

using WebClient = CollectionMaxxing.Api.Tests.Auth.WebClient;

namespace CollectionMaxxing.Api.Tests.Deletion;

// LIB-2d fix round (PR #106): a file the purge can't delete is never forgotten and never blocks erasing the person; a delete that fails
// halfway leaves the person active so they can try again.
public class StuckFilesTests(ApiFixture api) : DeletionTest(api)
{
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    private string ExportsRoot { get; } = Path.Combine(Path.GetTempPath(), "cm-exports-" + Guid.NewGuid().ToString("N"));

    protected override Dictionary<string, string?>? MoreSettings => new()
    {
        ["Catalog:CoversRoot"] = CoversRoot,
        ["Exports:Path"] = ExportsRoot,
    };

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
        if (Directory.Exists(ExportsRoot))
        {
            Directory.Delete(ExportsRoot, recursive: true);
        }
    }

    /// <summary>Runs due jobs while the fake clock ticks on, so that the waits between a stuck file's delete attempts pass.</summary>
    private async Task RunDueJobsWhileTimePassesAsync()
    {
        using var stop = new CancellationTokenSource();
        var pump = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                App.Time.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(5);
            }
        });
        try
        {
            await RunDueJobsAsync();
        }
        finally
        {
            await stop.CancelAsync();
            await pump;
        }
    }

    private async Task SweepAsync()
    {
        App.Time.Advance(Day + TimeSpan.FromSeconds(1));
        await App.Services.GetRequiredService<RecurringJobScheduler>().EnqueueDueAsync(CancellationToken.None);
        await RunDueJobsWhileTimePassesAsync();
    }

    /// <summary>Puts a non-empty folder where the file is: deleting it fails on every try, on every operating system, like a locked file.</summary>
    private static void MakeStuck(string path)
    {
        File.Delete(path);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "inside"), "x");
    }

    private static async Task<int> ExportJobsOfAsync(CollectionMaxxing.Infrastructure.Persistence.AppDbContext context, Guid userId) =>
        (await context.Jobs.AsNoTracking().Where(j => j.Type == "exports.build").ToListAsync())
            .Count(j => j.PayloadJson.Contains(userId.ToString(), StringComparison.OrdinalIgnoreCase));

    private string CoverFile(string blobPath) => Path.Combine(CoversRoot, blobPath.Replace('/', Path.DirectorySeparatorChar));

    private async Task<(string? Email, string DisplayName)> IdentityOfAsync(Guid userId)
    {
        await using var context = Db.CreateContext();
        var user = await context.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        return (user.Email, user.DisplayName);
    }

    [Fact]
    public async Task A_photo_that_stays_locked_is_remembered_and_removed_by_a_later_sweep_while_the_identity_is_erased_at_once()
    {
        var (browser, owner) = await SetUpOwnerAsync();
        var libraryId = owner.LibraryId!.Value;
        var seeded = await SeedLibraryAsync(libraryId, owner.UserId);
        var stuckPath = seeded.BlobPaths[0];
        (await DeleteAccountAsync(browser)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        MakeStuck(CoverFile(stuckPath));

        App.Time.Advance(30 * Day + TimeSpan.FromSeconds(1));
        await RunDueJobsWhileTimePassesAsync();

        var identity = await IdentityOfAsync(owner.UserId);
        identity.Email.ShouldBeNull("a stuck file does not keep the person's email");
        identity.DisplayName.ShouldBe("Removed user");
        (await PersonalRowsAsync(owner.UserId)).ShouldNotContainKey("user_passkeys");
        Directory.Exists(CoverFile(stuckPath)).ShouldBeTrue("the file is still on disk");
        await using (var context = Db.CreateContext())
        {
            (await context.Assets.IgnoreQueryFilters().AsNoTracking().Where(a => a.OwnerLibraryId == libraryId).Select(a => a.BlobPath).ToListAsync())
                .ShouldBe([stuckPath], "the record of the stuck file is kept, and only that one");
        }

        // A sweep while it is still locked changes nothing and forgets nothing.
        await SweepAsync();
        Directory.Exists(CoverFile(stuckPath)).ShouldBeTrue();
        (await RowsOfLibraryAsync(libraryId)).ShouldContainKey("assets.owner_library_id");

        // Once the file is free, the next sweep removes it, and only then its record.
        Directory.Delete(CoverFile(stuckPath), recursive: true);
        await SweepAsync();

        FileExists(stuckPath).ShouldBeFalse();
        (await RowsOfLibraryAsync(libraryId)).ShouldBeEmpty();
        (await PersonalRowsAsync(owner.UserId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_export_file_that_stays_locked_does_not_keep_the_email_and_is_removed_by_a_later_sweep()
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var (sam, samAccount) = await AddMemberAsync(owner, ownerAccount.LibraryId!.Value, "editor", "Sam");
        App.Services.GetRequiredService<ExportSettings>().Retention = TimeSpan.FromDays(365);
        var started = await sam.GetAsync("/api/v1/me/export");
        started.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var exportId = (await ReadAsync(started)).GetProperty("jobId").GetGuid();
        await RunDueJobsAsync();
        var exportFile = Path.Combine(ExportsRoot, exportId.ToString("N") + ".zip");
        File.Exists(exportFile).ShouldBeTrue();
        (await DeleteAccountAsync(sam)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        MakeStuck(exportFile);

        App.Time.Advance(30 * Day + TimeSpan.FromSeconds(1));
        await RunDueJobsWhileTimePassesAsync();

        (await IdentityOfAsync(samAccount.UserId)).Email.ShouldBeNull("a locked export file does not keep the person's email");
        (await PersonalRowsAsync(samAccount.UserId)).ShouldNotContainKey("user_passkeys");
        await using (var context = Db.CreateContext())
        {
            (await ExportJobsOfAsync(context, samAccount.UserId))
                .ShouldBe(1, "the export's record is kept while its file is on disk");
        }

        Directory.Delete(exportFile, recursive: true);
        await SweepAsync();

        await using var after = Db.CreateContext();
        (await ExportJobsOfAsync(after, samAccount.UserId))
            .ShouldBe(0, "gone with its file");
    }

    [Fact]
    public async Task A_delete_that_fails_halfway_leaves_the_person_active_and_a_retry_finishes_it()
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var (sam, samAccount) = await AddMemberAsync(owner, ownerAccount.LibraryId!.Value, "editor", "Sam");
        await using var scope = App.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var failing = Substitute.For<CollectionMaxxing.Application.Accounts.Devices.IDeviceAuthorizations>();
        failing.RevokeAllAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("The database went away."));

        await Should.ThrowAsync<IOException>(() => HandlerWith(services, failing).HandleAsync(samAccount.UserId, CancellationToken.None));

        (await UserRowAsync(samAccount.UserId)).DeletedAt.ShouldBeNull("nothing was half done: the person is still active");
        (await sam.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await using (var context = Db.CreateContext())
        {
            (await context.LibraryMembers.IgnoreQueryFilters().CountAsync(m => m.UserId == samAccount.UserId)).ShouldBe(1, "still a member");
            (await context.AuditEvents.CountAsync(e => e.Action == AccountAuditActions.Deleted)).ShouldBe(0);
        }

        var retried = await HandlerWith(services, services.GetRequiredService<CollectionMaxxing.Application.Accounts.Devices.IDeviceAuthorizations>())
            .HandleAsync(samAccount.UserId, CancellationToken.None);

        retried.IsSuccess.ShouldBeTrue();
        (await UserRowAsync(samAccount.UserId)).DeletedAt.ShouldNotBeNull();
        await using var after = Db.CreateContext();
        (await after.LibraryMembers.IgnoreQueryFilters().CountAsync(m => m.UserId == samAccount.UserId)).ShouldBe(0);
        (await after.AuditEvents.CountAsync(e => e.Action == AccountAuditActions.Deleted)).ShouldBe(1);
    }

    private static DeleteAccount.Handler HandlerWith(IServiceProvider services, CollectionMaxxing.Application.Accounts.Devices.IDeviceAuthorizations devices) =>
        new(
            services.GetRequiredService<CollectionMaxxing.Application.Common.IAppDbContext>(),
            services.GetRequiredService<IAccountDeactivation>(),
            devices,
            services.GetRequiredService<IJobQueue>(),
            services.GetRequiredService<CollectionMaxxing.Application.Audit.IAuditLog>(),
            services.GetRequiredService<TimeProvider>());
}
