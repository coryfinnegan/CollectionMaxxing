using CollectionMaxxing.Application.Jobs;
using CollectionMaxxing.Domain.Operations;
using CollectionMaxxing.TestSupport;

namespace CollectionMaxxing.Infrastructure.Tests.Jobs;

public class JobQueueTests(PostgresFixture db) : DatabaseTest(db)
{
    [Fact]
    public async Task Enqueueing_the_same_key_twice_while_pending_creates_one_job()
    {
        await using var harness = new JobHarness(Db);
        var options = new JobEnqueueOptions(DedupeKey: "refresh:42");

        var first = await harness.EnqueueAsync(JobHarness.PingType, new Ping("a"), options);
        var second = await harness.EnqueueAsync(JobHarness.PingType, new Ping("b"), options);

        first.ShouldNotBeNull();
        second.ShouldBeNull();
        (await JobHarness.JobsAsync(Db)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Enqueueing_the_same_key_at_the_same_moment_from_many_places_creates_one_job()
    {
        await using var harness = new JobHarness(Db);
        var options = new JobEnqueueOptions(DedupeKey: "refresh:42");

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => harness.EnqueueAsync(JobHarness.PingType, new Ping("a"), options))));

        results.Count(id => id is not null).ShouldBe(1);
        (await JobHarness.JobsAsync(Db)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_key_is_free_again_once_its_job_has_finished()
    {
        await using var harness = new JobHarness(Db);
        var options = new JobEnqueueOptions(DedupeKey: "refresh:42");
        await harness.EnqueueAsync(JobHarness.PingType, new Ping("a"), options);
        await harness.Runner.RunNextAsync("w", CancellationToken.None);

        var again = await harness.EnqueueAsync(JobHarness.PingType, new Ping("b"), options);

        again.ShouldNotBeNull();
        (await JobHarness.JobsAsync(Db)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Different_keys_and_no_key_never_collide()
    {
        await using var harness = new JobHarness(Db);

        await harness.EnqueueAsync(JobHarness.PingType, new Ping("a"), new JobEnqueueOptions(DedupeKey: "one"));
        await harness.EnqueueAsync(JobHarness.PingType, new Ping("b"), new JobEnqueueOptions(DedupeKey: "two"));
        await harness.EnqueueAsync(JobHarness.PingType, new Ping("c"));
        await harness.EnqueueAsync(JobHarness.PingType, new Ping("d"));

        (await JobHarness.JobsAsync(Db)).Count.ShouldBe(4);
    }

    [Fact]
    public async Task A_recurring_job_is_queued_once_per_period_however_many_workers_run()
    {
        var period = TimeSpan.FromHours(1);
        await using var one = new JobHarness(Db, tickEvery: period);
        await using var two = new JobHarness(Db, one.Time, tickEvery: period);

        var queued = await Task.WhenAll(one.Scheduler.EnqueueDueAsync(CancellationToken.None), two.Scheduler.EnqueueDueAsync(CancellationToken.None));
        queued.Sum().ShouldBe(1);

        // Same period, even after the first one finished.
        await one.Runner.RunNextAsync("w", CancellationToken.None);
        one.Time.Advance(TimeSpan.FromMinutes(1));
        (await two.Scheduler.EnqueueDueAsync(CancellationToken.None)).ShouldBe(0);
        (await JobHarness.JobsAsync(Db)).Count.ShouldBe(1);

        // Next period.
        one.Time.Advance(period);
        var next = await Task.WhenAll(one.Scheduler.EnqueueDueAsync(CancellationToken.None), two.Scheduler.EnqueueDueAsync(CancellationToken.None));
        next.Sum().ShouldBe(1);
        (await JobHarness.JobsAsync(Db)).Count.ShouldBe(2);
        (await JobHarness.JobsAsync(Db)).ShouldAllBe(j => j.Type == JobHarness.TickType);
    }

    [Fact]
    public async Task A_recurring_job_runs_its_handler()
    {
        await using var harness = new JobHarness(Db, tickEvery: TimeSpan.FromHours(1));

        await harness.Scheduler.EnqueueDueAsync(CancellationToken.None);
        await harness.Runner.RunNextAsync("w", CancellationToken.None);

        harness.Log.Ran.ShouldBe(["tick"]);
        (await JobHarness.JobsAsync(Db)).Single().Status.ShouldBe(JobStatus.Succeeded);
    }
}
