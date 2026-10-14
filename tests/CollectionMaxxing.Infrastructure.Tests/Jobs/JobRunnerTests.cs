using CollectionMaxxing.Application.Jobs;
using CollectionMaxxing.Domain.Operations;
using CollectionMaxxing.Infrastructure.Jobs;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CollectionMaxxing.Infrastructure.Tests.Jobs;

public class JobRunnerTests(PostgresFixture db) : DatabaseTest(db)
{
    [Fact]
    public async Task Workers_racing_for_jobs_run_each_job_exactly_once()
    {
        await using var first = new JobHarness(Db);
        for (var i = 0; i < 20; i++)
        {
            await first.EnqueueAsync(JobHarness.PingType, new Ping($"job-{i}"));
        }

        var harnesses = new List<JobHarness> { first };
        for (var i = 0; i < 3; i++)
        {
            harnesses.Add(new JobHarness(Db, first.Time));
        }

        await Task.WhenAll(harnesses.Select((harness, index) => Task.Run(async () =>
        {
            harness.Log.Behaviour = (_, token) => Task.Delay(20, token);
            while (await harness.Runner.RunNextAsync($"worker-{index}", CancellationToken.None))
            {
            }
        })));

        var ran = harnesses.SelectMany(h => h.Log.Ran).ToList();
        ran.Count.ShouldBe(20);
        ran.Distinct().Count().ShouldBe(20);
        (await JobHarness.JobsAsync(Db)).ShouldAllBe(j => j.Status == JobStatus.Succeeded && j.Attempts == 1);
        foreach (var harness in harnesses.Skip(1))
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_failing_job_is_retried_with_growing_delays_then_marked_failed_without_secrets()
    {
        await using var harness = new JobHarness(Db, tune: o => o.Retry = new JobRetryPolicy { MaxAttempts = 3, BaseDelay = TimeSpan.FromSeconds(10) });
        harness.Log.Behaviour = (_, _) => throw new InvalidOperationException("upstream said no (password=hunter2)");
        await harness.EnqueueAsync(JobHarness.PingType, new Ping("x"));

        (await harness.Runner.RunNextAsync("w", CancellationToken.None)).ShouldBeTrue();
        var afterFirst = (await JobHarness.JobsAsync(Db)).Single();
        afterFirst.Status.ShouldBe(JobStatus.Pending);
        afterFirst.Attempts.ShouldBe(1);
        afterFirst.RunAfter.ShouldBe(harness.Time.GetUtcNow() + TimeSpan.FromSeconds(10));
        (await harness.Runner.RunNextAsync("w", CancellationToken.None)).ShouldBeFalse();

        harness.Time.Advance(TimeSpan.FromSeconds(10));
        (await harness.Runner.RunNextAsync("w", CancellationToken.None)).ShouldBeTrue();
        (await JobHarness.JobsAsync(Db)).Single().RunAfter.ShouldBe(harness.Time.GetUtcNow() + TimeSpan.FromSeconds(20));

        harness.Time.Advance(TimeSpan.FromSeconds(20));
        (await harness.Runner.RunNextAsync("w", CancellationToken.None)).ShouldBeTrue();
        var failed = (await JobHarness.JobsAsync(Db)).Single();
        failed.Status.ShouldBe(JobStatus.Failed);
        failed.Attempts.ShouldBe(3);
        failed.LastError.ShouldNotBeNull().ShouldContain("upstream said no");
        failed.LastError.ShouldNotContain("hunter2");
        failed.LockedBy.ShouldBeNull();
        (await harness.Runner.RunNextAsync("w", CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_job_whose_lock_expired_is_picked_up_again()
    {
        await using var harness = new JobHarness(Db, tune: o => o.LockDuration = TimeSpan.FromMinutes(5));
        await harness.EnqueueAsync(JobHarness.PingType, new Ping("orphan"));
        await using (var context = Db.CreateContext())
        {
            // A worker took it and then died: running, with a lock that ends in 5 minutes.
            var until = harness.Time.GetUtcNow() + TimeSpan.FromMinutes(5);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE jobs SET status = 'running', attempts = 1, locked_by = 'dead', locked_until = {until}");
        }

        (await harness.Runner.RunNextAsync("w", CancellationToken.None)).ShouldBeFalse();

        harness.Time.Advance(TimeSpan.FromMinutes(6));
        (await harness.Runner.RunNextAsync("w", CancellationToken.None)).ShouldBeTrue();
        harness.Log.Ran.ShouldBe(["orphan"]);
        var job = (await JobHarness.JobsAsync(Db)).Single();
        job.Status.ShouldBe(JobStatus.Succeeded);
        job.Attempts.ShouldBe(2);
    }

    [Fact]
    public async Task A_job_abandoned_on_its_last_attempt_is_failed_instead_of_run_again()
    {
        await using var harness = new JobHarness(Db, tune: o => o.Retry = new JobRetryPolicy { MaxAttempts = 2 });
        await harness.EnqueueAsync(JobHarness.PingType, new Ping("poison"));
        await using (var context = Db.CreateContext())
        {
            var past = harness.Time.GetUtcNow() - TimeSpan.FromMinutes(1);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE jobs SET status = 'running', attempts = 2, locked_by = 'dead', locked_until = {past}");
        }

        (await harness.Runner.RunNextAsync("w", CancellationToken.None)).ShouldBeTrue();

        harness.Log.Ran.ShouldBeEmpty();
        (await JobHarness.JobsAsync(Db)).Single().Status.ShouldBe(JobStatus.Failed);
    }

    [Fact]
    public async Task A_job_scheduled_for_later_waits_for_the_clock()
    {
        await using var harness = new JobHarness(Db);
        await harness.EnqueueAsync(JobHarness.PingType, new Ping("later"), new JobEnqueueOptions(RunAfter: harness.Time.GetUtcNow().AddHours(1)));

        (await harness.Runner.RunNextAsync("w", CancellationToken.None)).ShouldBeFalse();
        harness.Time.Advance(TimeSpan.FromMinutes(59));
        (await harness.Runner.RunNextAsync("w", CancellationToken.None)).ShouldBeFalse();
        harness.Time.Advance(TimeSpan.FromMinutes(1));
        (await harness.Runner.RunNextAsync("w", CancellationToken.None)).ShouldBeTrue();
        harness.Log.Ran.ShouldBe(["later"]);
    }

    [Fact]
    public async Task A_job_type_nobody_handles_fails_with_a_clear_error_after_its_retries()
    {
        await using var harness = new JobHarness(Db, tune: o => o.Retry = new JobRetryPolicy { MaxAttempts = 1 });
        await harness.EnqueueAsync("test.unknown", null);

        await harness.Runner.RunNextAsync("w", CancellationToken.None);

        var job = (await JobHarness.JobsAsync(Db)).Single();
        job.Status.ShouldBe(JobStatus.Failed);
        job.LastError.ShouldNotBeNull().ShouldContain("test.unknown");
    }

    [Fact]
    public async Task The_payload_reaches_the_handler()
    {
        await using var harness = new JobHarness(Db);
        await harness.EnqueueAsync(JobHarness.PingType, new Ping("hello"));

        await harness.Runner.RunNextAsync("w", CancellationToken.None);

        harness.Log.Ran.ShouldBe(["hello"]);
    }

    [Fact]
    public async Task The_worker_stops_cleanly_on_shutdown_and_hands_back_the_job_it_was_running()
    {
        await using var harness = new JobHarness(Db);
        var started = new TaskCompletionSource();
        harness.Log.Behaviour = async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        await harness.EnqueueAsync(JobHarness.PingType, new Ping("long"));
        var worker = harness.Get<IEnumerable<IHostedService>>().OfType<JobWorker>().Single();

        await worker.StartAsync(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        var job = (await JobHarness.JobsAsync(Db)).Single();
        job.Status.ShouldBe(JobStatus.Pending);
        job.Attempts.ShouldBe(0);
        job.LockedBy.ShouldBeNull();
    }
}
