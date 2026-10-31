using CollectionMaxxing.Domain.Operations;
using CollectionMaxxing.Infrastructure.Jobs;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CollectionMaxxing.Infrastructure.Tests.Jobs;

/// <summary>
/// The jobs table is a queue, not a log: finished rows are removed after a while (a week for the ones that worked, a month
/// for the ones that failed, so a failure can still be looked at). The recurring scheduler uses old rows to remember which
/// period it already queued, so the newest row of each recurring job is never removed.
/// </summary>
public class PruneJobsTests(PostgresFixture db) : DatabaseTest(db)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private async Task<Guid> AddAsync(JobStatus status, TimeSpan age, string type = "test.ping", string? key = null)
    {
        var id = Guid.CreateVersion7();
        var at = Now - age;
        await using var context = Db.CreateContext();
        context.Jobs.Add(new Job
        {
            Id = id,
            Type = type,
            PayloadJson = key is null ? """{"key":null,"data":{}}""" : "{\"key\":\"" + key + "\",\"data\":{}}",
            Status = status,
            RunAfter = at,
            CreatedAt = at,
            LockedBy = status == JobStatus.Running ? "worker" : null,
            LockedUntil = status == JobStatus.Running ? at.AddMinutes(10) : null,
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }

    private async Task PruneAsync(FakeTimeProvider? time = null)
    {
        await using var context = Db.CreateContext();
        await new PruneJobs.Pruner(context, time ?? new FakeTimeProvider(Now), NullLogger<PruneJobs.Pruner>.Instance)
            .HandleAsync(new PruneJobsPayload(), TestContext.Current.CancellationToken);
    }

    private async Task<List<Guid>> RemainingAsync()
    {
        await using var context = Db.CreateContext();
        return await context.Jobs.AsNoTracking().Select(j => j.Id).ToListAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Succeeded_jobs_go_after_a_week_and_failed_ones_after_a_month()
    {
        var oldSucceeded = await AddAsync(JobStatus.Succeeded, TimeSpan.FromDays(8));
        var freshSucceeded = await AddAsync(JobStatus.Succeeded, TimeSpan.FromDays(6));
        var oldFailed = await AddAsync(JobStatus.Failed, TimeSpan.FromDays(31));
        var freshFailed = await AddAsync(JobStatus.Failed, TimeSpan.FromDays(10));

        await PruneAsync();

        (await RemainingAsync()).ShouldBe([freshSucceeded, freshFailed], ignoreOrder: true);
        (await RemainingAsync()).ShouldNotContain(oldSucceeded);
        (await RemainingAsync()).ShouldNotContain(oldFailed);
    }

    [Fact]
    public async Task Pending_and_running_jobs_are_never_pruned_however_old()
    {
        var waiting = await AddAsync(JobStatus.Pending, TimeSpan.FromDays(90));
        var running = await AddAsync(JobStatus.Running, TimeSpan.FromDays(90));
        var later = await AddAsync(JobStatus.Pending, TimeSpan.FromDays(-2)); // due in two days

        await PruneAsync();

        (await RemainingAsync()).ShouldBe([waiting, running, later], ignoreOrder: true);
    }

    [Fact]
    public async Task A_big_backlog_is_pruned_in_batches_until_it_is_gone()
    {
        await using (var context = Db.CreateContext())
        {
            var payload = """{"key":null,"data":{}}""";
            var at = Now - TimeSpan.FromDays(20);
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO jobs (id, type, payload, status, attempts, run_after, created_at)
                SELECT gen_random_uuid(), 'test.ping', {payload}::jsonb, 'succeeded', 1, {at}, {at}
                FROM generate_series(1, {PruneJobs.BatchSize * 2 + 500})
                """, TestContext.Current.CancellationToken);
        }

        var keep = await AddAsync(JobStatus.Succeeded, TimeSpan.FromDays(1));

        await PruneAsync();

        (await RemainingAsync()).ShouldBe([keep]);
    }

    [Fact]
    public async Task Pruning_twice_in_a_row_is_harmless()
    {
        var keep = await AddAsync(JobStatus.Succeeded, TimeSpan.FromDays(1));
        await AddAsync(JobStatus.Succeeded, TimeSpan.FromDays(9));

        await PruneAsync();
        await PruneAsync();

        (await RemainingAsync()).ShouldBe([keep]);
    }

    [Fact]
    public async Task The_scheduler_does_not_queue_a_long_period_again_after_a_prune()
    {
        // A job that recurs every 100 days: the week-old row is the only memory that this period already ran.
        var every = TimeSpan.FromDays(100);
        await using var harness = new JobHarness(Db, tickEvery: every);
        (await harness.Scheduler.EnqueueDueAsync(CancellationToken.None)).ShouldBe(1);
        await harness.Runner.RunNextAsync("w", CancellationToken.None);

        harness.Time.Advance(TimeSpan.FromDays(40));
        (harness.Time.GetUtcNow().UtcTicks / every.Ticks).ShouldBe(Now.UtcTicks / every.Ticks, "the test needs the same period");

        await PruneAsync(harness.Time);

        (await harness.Scheduler.EnqueueDueAsync(CancellationToken.None)).ShouldBe(0);
        (await JobHarness.JobsAsync(Db)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Older_runs_of_a_recurring_job_are_pruned_and_its_latest_run_stays()
    {
        await using var harness = new JobHarness(Db, tickEvery: TimeSpan.FromHours(1));
        await harness.Scheduler.EnqueueDueAsync(CancellationToken.None);
        await harness.Runner.RunNextAsync("w", CancellationToken.None);
        harness.Time.Advance(TimeSpan.FromDays(8));
        await harness.Scheduler.EnqueueDueAsync(CancellationToken.None);
        await harness.Runner.RunNextAsync("w", CancellationToken.None);
        harness.Time.Advance(TimeSpan.FromDays(8));
        var all = await JobHarness.JobsAsync(Db);
        all.Count.ShouldBe(2);

        await PruneAsync(harness.Time);

        // Both are older than a week, but only the earlier one is surplus.
        (await RemainingAsync()).ShouldBe([all[1].Id]);
    }

    [Fact]
    public void The_prune_is_a_recurring_job_with_a_handler()
    {
        var services = new ServiceCollection();
        services.AddJobs();
        services.AddJobPruning();

        using var provider = services.BuildServiceProvider();

        provider.GetServices<RecurringJobRegistration>().ShouldContain(r => r.Type == PruneJobs.JobType && r.Every == PruneJobs.Every);
        provider.GetServices<JobRegistration>().ShouldContain(r => r.Type == PruneJobs.JobType);
    }
}
