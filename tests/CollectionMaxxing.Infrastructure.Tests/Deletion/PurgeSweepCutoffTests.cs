using CollectionMaxxing.Application.Accounts.Deletion;
using CollectionMaxxing.Application.Jobs;
using Microsoft.Extensions.Time.Testing;

namespace CollectionMaxxing.Infrastructure.Tests.Deletion;

// LIB-2d fix round (PR #106): the sweep's own 30-day cutoff, pinned on what the sweep enqueues (not on PurgeAccount's own guard).
public class PurgeSweepCutoffTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A backlog that answers like the real one: the users deleted on or before the cutoff it is given.</summary>
    private sealed class FakeBacklog(params (Guid UserId, DateTimeOffset DeletedAt)[] users) : IPurgeBacklog
    {
        public Task<IReadOnlyList<Guid>> UsersAwaitingPurgeAsync(DateTimeOffset deletedBefore, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Guid>>(users.Where(u => u.DeletedAt <= deletedBefore).Select(u => u.UserId).ToList());
    }

    private sealed class RecordingQueue : IJobQueue
    {
        public List<PurgeAccountPayload> Queued { get; } = [];

        public Task<Guid?> EnqueueAsync(string type, object? payload, JobEnqueueOptions? options = null, CancellationToken cancellationToken = default)
        {
            type.ShouldBe(PurgeAccount.JobType);
            Queued.Add((PurgeAccountPayload)payload!);
            return Task.FromResult<Guid?>(Guid.NewGuid());
        }
    }

    private static async Task<List<PurgeAccountPayload>> SweepAsync(params (Guid UserId, DateTimeOffset DeletedAt)[] users)
    {
        var queue = new RecordingQueue();
        await new PurgeSweep.Handler(new FakeBacklog(users), queue, new FakeTimeProvider(Now)).HandleAsync(new PurgeSweep.Payload(), CancellationToken.None);
        return queue.Queued;
    }

    [Fact]
    public async Task A_user_deleted_29_days_and_23_hours_ago_is_not_queued_by_the_sweep()
    {
        var queued = await SweepAsync((Guid.NewGuid(), Now - TimeSpan.FromDays(29) - TimeSpan.FromHours(23)));

        queued.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_user_deleted_30_days_ago_is_queued_by_the_sweep()
    {
        var due = Guid.NewGuid();

        var queued = await SweepAsync((due, Now - TimeSpan.FromDays(30)), (Guid.NewGuid(), Now - TimeSpan.FromDays(10)));

        queued.ShouldHaveSingleItem().UserId.ShouldBe(due);
    }
}
