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

    private static JobToEnqueue Keyed(string label, string? key = null, DateTimeOffset? runAfter = null) =>
        new(new Ping(label), key is null && runAfter is null ? null : new JobEnqueueOptions(runAfter, key));

    [Fact]
    public async Task Enqueueing_many_jobs_queues_each_and_answers_with_their_ids_in_the_order_given()
    {
        await using var harness = new JobHarness(Db);

        var ids = await harness.EnqueueManyAsync(JobHarness.PingType, [Keyed("a", "k:a"), Keyed("b"), Keyed("c", "k:c")]);

        ids.Count.ShouldBe(3);
        ids.ShouldAllBe(id => id != null);
        var stored = await JobHarness.JobsAsync(Db);
        stored.Select(j => j.Id).ShouldBe(ids.Select(id => id!.Value), ignoreOrder: true);
        stored.ShouldAllBe(j => j.Type == JobHarness.PingType && j.Status == JobStatus.Pending);
    }

    [Fact]
    public async Task Enqueueing_many_jobs_with_nothing_to_queue_answers_with_nothing()
    {
        await using var harness = new JobHarness(Db);

        (await harness.EnqueueManyAsync(JobHarness.PingType, [])).ShouldBeEmpty();
        (await JobHarness.JobsAsync(Db)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Enqueueing_many_jobs_skips_a_key_that_is_already_pending_and_says_so_in_that_place()
    {
        await using var harness = new JobHarness(Db);
        await harness.EnqueueAsync(JobHarness.PingType, new Ping("old"), new JobEnqueueOptions(DedupeKey: "k:b"));

        var ids = await harness.EnqueueManyAsync(JobHarness.PingType, [Keyed("a", "k:a"), Keyed("b", "k:b"), Keyed("c")]);

        ids.Select(id => id is not null).ToArray().ShouldBe([true, false, true]);
        (await JobHarness.JobsAsync(Db)).Count.ShouldBe(3);
    }

    [Fact]
    public async Task Enqueueing_many_jobs_with_one_key_twice_queues_the_first_only()
    {
        await using var harness = new JobHarness(Db);

        var ids = await harness.EnqueueManyAsync(JobHarness.PingType, [Keyed("first", "k:x"), Keyed("second", "k:x"), Keyed("third", "k:x")]);

        ids.Select(id => id is not null).ToArray().ShouldBe([true, false, false]);
        (await JobHarness.JobsAsync(Db)).Count.ShouldBe(1);
        await harness.Runner.RunNextAsync("w", CancellationToken.None);
        harness.Log.Ran.ShouldBe(["first"]);
    }

    [Fact]
    public async Task Jobs_enqueued_together_run_in_the_order_they_were_given()
    {
        await using var harness = new JobHarness(Db);
        var labels = Enumerable.Range(0, 12).Select(i => $"job-{i:D2}").ToList();

        await harness.EnqueueManyAsync(JobHarness.PingType, [.. labels.Select(l => Keyed(l, $"k:{l}"))]);
        while (await harness.Runner.RunNextAsync("w", CancellationToken.None))
        {
        }

        harness.Log.Ran.ShouldBe(labels);
    }

    [Fact]
    public async Task A_job_given_a_later_run_time_waits_for_it_though_queued_in_a_batch()
    {
        await using var harness = new JobHarness(Db);
        var later = harness.Time.GetUtcNow().AddMinutes(30);

        await harness.EnqueueManyAsync(JobHarness.PingType, [Keyed("later", runAfter: later), Keyed("now")]);
        await harness.Runner.RunNextAsync("w", CancellationToken.None);
        (await harness.Runner.RunNextAsync("w", CancellationToken.None)).ShouldBeFalse();
        harness.Time.Advance(TimeSpan.FromMinutes(31));
        await harness.Runner.RunNextAsync("w", CancellationToken.None);

        harness.Log.Ran.ShouldBe(["now", "later"]);
    }

    [Fact]
    public async Task Batches_that_share_keys_and_name_them_in_opposite_orders_queue_each_key_once_and_never_deadlock()
    {
        await using var harness = new JobHarness(Db);
        var forwards = Enumerable.Range(0, 20).Select(i => Keyed($"f{i}", $"k:{i}")).ToArray();
        var backwards = forwards.Reverse().Select(j => Keyed("b", ((JobEnqueueOptions)j.Options!).DedupeKey)).ToArray();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(i => Task.Run(() => harness.EnqueueManyAsync(JobHarness.PingType, i % 2 == 0 ? forwards : backwards))));

        results.SelectMany(r => r).Count(id => id is not null).ShouldBe(20);
        (await JobHarness.JobsAsync(Db)).Count.ShouldBe(20);
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
