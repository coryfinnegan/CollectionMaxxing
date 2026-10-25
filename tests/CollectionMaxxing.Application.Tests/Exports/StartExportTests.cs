using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Exports;
using CollectionMaxxing.Application.Jobs;
using CollectionMaxxing.TestSupport;

namespace CollectionMaxxing.Application.Tests.Exports;

public class StartExportTests
{
    /// <summary>Answers each enqueue from the script and counts the calls, so a test can tell "queued" from "phantom".</summary>
    private sealed class ScriptedQueue(Guid?[] answers) : IJobQueue
    {
        private readonly Queue<Guid?> _answers = new(answers);

        public int Calls { get; private set; }

        public Task<Guid?> EnqueueAsync(string type, object? payload, JobEnqueueOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(_answers.Dequeue());
        }
    }

    private sealed class ScriptedJobs(Guid?[] answers) : IExportJobs
    {
        private readonly Queue<Guid?> _answers = new(answers);

        public Task<Guid?> FindActiveAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult(_answers.Dequeue());

        public Task<ExportJobState?> FindAsync(Guid exportId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> ListIdsForUserAsync(Guid userId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteForUserAsync(Guid userId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteForUserAsync(Guid userId, IReadOnlyCollection<Guid> exportIds, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Starting_an_export_queues_it_and_returns_its_id()
    {
        var queue = new ScriptedQueue([Guid.CreateVersion7()]);
        var jobs = new ScriptedJobs([]);

        var started = await new StartExport.Handler(queue, jobs, new FakeIdGenerator()).HandleAsync(Guid.CreateVersion7(), CancellationToken.None);

        started.JobId.ShouldBe(new Guid(1, 0, 0, new byte[8]));
        started.Status.ShouldBe(ExportStatus.Queued);
        queue.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Asking_again_while_one_runs_returns_the_running_export()
    {
        var running = Guid.CreateVersion7();
        var queue = new ScriptedQueue([null]);
        var jobs = new ScriptedJobs([running]);

        var started = await new StartExport.Handler(queue, jobs, new FakeIdGenerator()).HandleAsync(Guid.CreateVersion7(), CancellationToken.None);

        started.JobId.ShouldBe(running);
        started.Status.ShouldBe(ExportStatus.Queued);
        queue.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task When_the_dedupe_hit_finishes_before_the_lookup_the_export_is_queued_for_real()
    {
        var queue = new ScriptedQueue([null, Guid.CreateVersion7()]);
        var jobs = new ScriptedJobs([null]);

        var started = await new StartExport.Handler(queue, jobs, new FakeIdGenerator()).HandleAsync(Guid.CreateVersion7(), CancellationToken.None);

        started.JobId.ShouldBe(new Guid(1, 0, 0, new byte[8]));
        started.Status.ShouldBe(ExportStatus.Queued);
        queue.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task When_the_queue_stays_contended_starting_throws_rather_than_handing_out_a_phantom_id()
    {
        var queue = new ScriptedQueue([null, null, null]);
        var jobs = new ScriptedJobs([null, null, null]);

        await Should.ThrowAsync<InvalidOperationException>(
            new StartExport.Handler(queue, jobs, new FakeIdGenerator()).HandleAsync(Guid.CreateVersion7(), CancellationToken.None));
    }
}
