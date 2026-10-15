using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Accounts.Deletion;
using CollectionMaxxing.Application.Exports;
using CollectionMaxxing.Application.Jobs;
using CollectionMaxxing.Domain.Operations;
using CollectionMaxxing.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using WebClient = CollectionMaxxing.Api.Tests.Auth.WebClient;

namespace CollectionMaxxing.Api.Tests.Deletion;

// LIB-2d (issue #99), items 2 to 6: the 30-day promise holds when a purge job dies, when the delete was retried, and for exports and invite labels.
public class DeletionHardeningTests(ApiFixture api) : DeletionTest(api)
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

    private async Task QueueSweepAndRunAsync()
    {
        await App.Services.GetRequiredService<RecurringJobScheduler>().EnqueueDueAsync(CancellationToken.None);
        await RunDueJobsAsync();
    }

    private async Task<List<Job>> JobsOfTypeAsync(string type)
    {
        await using var context = Db.CreateContext();
        return await context.Jobs.AsNoTracking().Where(j => j.Type == type).OrderBy(j => j.CreatedAt).ToListAsync();
    }

    // ---- 2. a purge job that died is queued again by a daily sweep -----------------------------------------------------

    [Fact]
    public async Task A_dead_purge_job_is_queued_again_by_the_sweep_once_the_30_days_are_over()
    {
        var (browser, owner) = await SetUpOwnerAsync();
        var libraryId = owner.LibraryId!.Value;
        var seeded = await SeedLibraryAsync(libraryId, owner.UserId);
        (await DeleteAccountAsync(browser)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // The purge job used up its five attempts and was left failed: nothing would ever run it again.
        await using (var context = Db.CreateContext())
        {
            await context.Jobs.Where(j => j.Type == PurgeAccount.JobType).ExecuteUpdateAsync(set => set
                .SetProperty(j => j.Status, JobStatus.Failed)
                .SetProperty(j => j.Attempts, 5)
                .SetProperty(j => j.LastError, "disk went away"));
        }

        App.Time.Advance(31 * Day);

        await QueueSweepAndRunAsync();

        (await RowsOfLibraryAsync(libraryId)).ShouldBeEmpty("the sweep queued the purge again and it ran");
        seeded.BlobPaths.ShouldAllBe(path => !FileExists(path));
        (await PersonalRowsAsync(owner.UserId)).ShouldBeEmpty();
        var user = await UserRowAsync(owner.UserId);
        user.DisplayName.ShouldBe("Removed user");
    }

    [Fact]
    public async Task The_sweep_leaves_accounts_that_are_not_yet_due_or_already_purged_alone()
    {
        var (browser, owner) = await SetUpOwnerAsync();
        var (sam, samAccount) = await AddMemberAsync(browser, owner.LibraryId!.Value, "viewer", "Sam");
        (await DeleteAccountAsync(sam)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var (pat, patAccount) = await AddMemberAsync(browser, owner.LibraryId!.Value, "viewer", "Pat");
        (await DeleteAccountAsync(pat)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await PurgeJobsAsync()).Count.ShouldBe(2);

        App.Time.Advance(29 * Day);
        await QueueSweepAndRunAsync();
        (await PurgeJobsAsync()).Count.ShouldBe(2, "on day 29 the sweep queues nothing");

        App.Time.Advance(2 * Day);
        await RunDueJobsAsync();
        await QueueSweepAndRunAsync();
        var jobs = await PurgeJobsAsync();
        jobs.Count.ShouldBe(2, "both were purged by their own jobs, and the sweep did not queue them a second time");
        jobs.ShouldAllBe(j => j.Status == JobStatus.Succeeded);
        (await PersonalRowsAsync(samAccount.UserId)).ShouldBeEmpty();
        (await PersonalRowsAsync(patAccount.UserId)).ShouldBeEmpty();
    }

    // ---- 3. a delete that was retried: the earlier job must not fire early and then forget -----------------------------

    [Fact]
    public async Task A_purge_job_that_fires_before_the_account_is_due_queues_itself_again_for_the_right_day()
    {
        var (browser, owner) = await SetUpOwnerAsync();
        var libraryId = owner.LibraryId!.Value;
        await SeedLibraryAsync(libraryId, owner.UserId);

        // A first attempt at deleting queued the purge and then failed before the account was marked deleted.
        await using (var scope = App.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IJobQueue>().EnqueueAsync(
                PurgeAccount.JobType,
                new PurgeAccountPayload(owner.UserId),
                new JobEnqueueOptions(App.Time.GetUtcNow() + PurgeAccount.Retention, PurgeAccount.DedupeKey(owner.UserId)));
        }

        // Ten days later the user tries again. The dedupe key means the earlier job (due in 20 days) is the one that stays.
        App.Time.Advance(10 * Day);
        (await browser.LoginAsync()).StatusCode.ShouldBe(HttpStatusCode.OK); // the session has lapsed in ten days
        (await DeleteAccountAsync(browser)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var deletedAt = (await UserRowAsync(owner.UserId)).DeletedAt!.Value;

        App.Time.Advance(20 * Day + TimeSpan.FromSeconds(1)); // the early job is due, the account is not
        await RunDueJobsAsync();
        (await RowsOfLibraryAsync(libraryId)).ShouldNotBeEmpty("the account was deleted 20 days ago, not 30");
        var pending = (await PurgeJobsAsync()).Where(j => j.Status == JobStatus.Pending).ToList();
        pending.ShouldHaveSingleItem("the early job queued itself again").RunAfter.ShouldBe(deletedAt.AddDays(30));

        App.Time.Advance(10 * Day);
        await RunDueJobsAsync();
        (await RowsOfLibraryAsync(libraryId)).ShouldBeEmpty("it is purged on the day it is due");
    }

    // ---- 5. exports ------------------------------------------------------------------------------------------------------

    private static string[] ExportFiles(string folder) => Directory.Exists(folder) ? Directory.GetFiles(folder) : [];

    private async Task<Guid> ExportAsync(WebClient browser)
    {
        // The hourly cleanup would remove these files after a day anyway; the test is about the purge, so it keeps them.
        App.Services.GetRequiredService<ExportSettings>().Retention = TimeSpan.FromDays(365);
        var started = await browser.GetAsync("/api/v1/me/export");
        started.StatusCode.ShouldBe(HttpStatusCode.Accepted, await BodyOfAsync(started));
        var id = (await ReadAsync(started)).GetProperty("jobId").GetGuid();
        await RunDueJobsAsync();
        var status = await browser.GetAsync($"/api/v1/me/export/{id}");
        (await ReadAsync(status)).GetProperty("status").GetString().ShouldBe("ready");
        return id;
    }

    [Fact]
    public async Task A_purged_users_export_files_and_export_jobs_are_removed_and_other_peoples_are_not()
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var (sam, samAccount) = await AddMemberAsync(owner, ownerAccount.LibraryId!.Value, "editor", "Sam");
        var ownersExport = await ExportAsync(owner);
        var samsExport = await ExportAsync(sam);
        File.Exists(Path.Combine(ExportsRoot, samsExport.ToString("N") + ".zip")).ShouldBeTrue();
        ExportFiles(ExportsRoot).Length.ShouldBe(2);

        (await DeleteAccountAsync(sam)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        App.Time.Advance(30 * Day + TimeSpan.FromSeconds(1));
        await RunDueJobsAsync();

        File.Exists(Path.Combine(ExportsRoot, samsExport.ToString("N") + ".zip")).ShouldBeFalse("Sam's export file is gone");
        File.Exists(Path.Combine(ExportsRoot, ownersExport.ToString("N") + ".zip")).ShouldBeTrue("the owner's export is not Sam's to lose");
        var exportJobs = await JobsOfTypeAsync("exports.build");
        exportJobs.ShouldHaveSingleItem("only the owner's export job is left");
        exportJobs.ShouldAllBe(j => !j.PayloadJson.Contains(samAccount.UserId.ToString(), StringComparison.OrdinalIgnoreCase));
        exportJobs.ShouldAllBe(j => j.PayloadJson.Contains(ownerAccount.UserId.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_sole_owners_export_goes_with_the_library()
    {
        var (browser, owner) = await SetUpOwnerAsync();
        await SeedLibraryAsync(owner.LibraryId!.Value, owner.UserId);
        await ExportAsync(browser);
        ExportFiles(ExportsRoot).ShouldNotBeEmpty();

        (await DeleteAccountAsync(browser)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        App.Time.Advance(30 * Day + TimeSpan.FromSeconds(1));
        await RunDueJobsAsync();

        ExportFiles(ExportsRoot).ShouldBeEmpty();
        (await JobsOfTypeAsync("exports.build")).ShouldBeEmpty();
    }

    // ---- 6. invite labels --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_label_and_the_id_on_the_invite_a_purged_member_used_are_cleared_in_the_library_that_carries_on()
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var libraryId = ownerAccount.LibraryId!.Value;
        var (token, created) = await CreateInviteAsync(owner, libraryId, "editor", "Sam the plumber");
        var inviteId = created.GetProperty("invite").GetProperty("id").GetGuid();
        var (sam, registered) = await JoinAsync(token, "Sam");
        registered.StatusCode.ShouldBe(HttpStatusCode.Created);
        await using (var before = Db.CreateContext())
        {
            var invite = await before.LibraryInvites.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.Id == inviteId);
            invite.Name.ShouldBe("Sam the plumber");
            invite.AcceptedBy.ShouldNotBeNull();
        }

        (await DeleteAccountAsync(sam)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        App.Time.Advance(30 * Day + TimeSpan.FromSeconds(1));
        await RunDueJobsAsync();

        await using var context = Db.CreateContext();
        var after = await context.LibraryInvites.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.Id == inviteId);
        after.Name.ShouldNotContain("plumber");
        after.AcceptedBy.ShouldBeNull();
        after.AcceptedAt.ShouldNotBeNull("the invite was still used: only who is gone");
    }

    // ---- 4. the invite race ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_invite_cannot_be_accepted_into_the_library_of_an_owner_who_has_already_been_deactivated()
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var libraryId = ownerAccount.LibraryId!.Value;
        var (token, _) = await CreateInviteAsync(owner, libraryId, "viewer", "Guest");
        var guest = App.NewBrowser();
        var options = await StartJoinAsync(guest, token, "Guest");
        options.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The owner's delete got as far as deactivating the account, but has not revoked the invites yet.
        await using (var context = Db.CreateContext())
        {
            await context.Users.Where(u => u.Id == ownerAccount.UserId).ExecuteUpdateAsync(set => set.SetProperty(u => u.DeletedAt, App.Time.GetUtcNow()));
        }

        var registered = await guest.RegisterAsync(options, invite: token);

        ((int)registered.StatusCode).ShouldBeInRange(400, 499);
        (await CountMembersAsync(libraryId)).ShouldBe(1, "the library must not gain a member it would keep without an owner");
    }

    [Fact]
    public async Task An_owner_whose_library_gains_a_member_just_before_the_deactivation_is_refused_and_stays_active()
    {
        var (browser, owner) = await SetUpOwnerAsync();
        var libraryId = owner.LibraryId!.Value;
        await using var scope = App.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var jobs = new HookedJobQueue(
            services.GetRequiredService<IJobQueue>(),
            async () =>
            {
                // An invite is accepted after the "does the library have members?" check and before the account is deactivated.
                var late = await CollectionMaxxing.TestSupport.TestData.CreateUserAsync(Db);
                await using var context = Db.CreateContext();
                context.LibraryMembers.Add(new CollectionMaxxing.Domain.Libraries.LibraryMember(libraryId, late, CollectionMaxxing.Domain.Libraries.LibraryRole.Viewer, App.Time.GetUtcNow()));
                await context.SaveChangesAsync();
            });
        var handler = DeletionHandler(services, jobs);

        var result = await handler.HandleAsync(owner.UserId, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("library_has_members");
        (await UserRowAsync(owner.UserId)).DeletedAt.ShouldBeNull("the owner is not deleted, so the library never has no owner");
        (await browser.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static DeleteAccount.Handler DeletionHandler(IServiceProvider services, IJobQueue jobs) =>
        new(
            services.GetRequiredService<CollectionMaxxing.Application.Common.IAppDbContext>(),
            services.GetRequiredService<IAccountDeactivation>(),
            services.GetRequiredService<CollectionMaxxing.Application.Accounts.Devices.IDeviceAuthorizations>(),
            jobs,
            services.GetRequiredService<CollectionMaxxing.Application.Audit.IAuditLog>(),
            services.GetRequiredService<TimeProvider>());

    /// <summary>Queues jobs for real, and runs <paramref name="beforeEnqueue"/> first: a way to make something happen at an exact point of a handler.</summary>
    private sealed class HookedJobQueue(IJobQueue inner, Func<Task> beforeEnqueue) : IJobQueue
    {
        public async Task<Guid?> EnqueueAsync(string type, object? payload, JobEnqueueOptions? options = null, CancellationToken cancellationToken = default)
        {
            await beforeEnqueue();
            return await inner.EnqueueAsync(type, payload, options, cancellationToken);
        }
    }
}
