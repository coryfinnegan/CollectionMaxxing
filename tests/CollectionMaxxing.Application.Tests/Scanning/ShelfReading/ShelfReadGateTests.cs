using CollectionMaxxing.Application.ShelfReading;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Application.Tests.Scanning.ShelfReading;

// One shelf read at a time: a free reader is taken at once, the others queue (a few, for a while), and anyone who leaves the queue gives their place back.
public class ShelfReadGateTests
{
    private static ShelfReadGate Gate(int waitSeconds = 30, int maxWaiting = 3) =>
        new(Options.Create(new ShelfReadQueueOptions { QueueWaitSeconds = waitSeconds, QueueMaxWaiting = maxWaiting }));

    [Fact]
    public async Task A_free_reader_is_taken_at_once_and_is_free_again_when_the_turn_is_disposed()
    {
        using var gate = Gate();

        var first = await gate.EnterAsync(CancellationToken.None);
        first.ShouldNotBeNull();
        first.Dispose();
        first.Dispose(); // twice is fine

        using var second = await gate.EnterAsync(CancellationToken.None);
        second.ShouldNotBeNull();
        var third = gate.EnterAsync(CancellationToken.None);
        (await Task.WhenAny(third, Task.Delay(50))).ShouldNotBe(third, "one turn at a time: a double dispose did not free two places");
    }

    [Fact]
    public async Task A_second_request_waits_for_the_first_to_finish()
    {
        using var gate = Gate();
        var first = await gate.EnterAsync(CancellationToken.None);
        var second = gate.EnterAsync(CancellationToken.None);
        await Task.Delay(100);
        second.IsCompleted.ShouldBeFalse();

        first!.Dispose();

        using var turn = await second.WaitAsync(TimeSpan.FromSeconds(5));
        turn.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_request_that_waits_past_the_limit_is_told_the_reader_is_busy()
    {
        using var gate = Gate(waitSeconds: 1);
        using var first = await gate.EnterAsync(CancellationToken.None);

        var second = await gate.EnterAsync(CancellationToken.None);

        second.ShouldBeNull();
    }

    [Fact]
    public async Task A_request_that_finds_the_queue_full_is_told_at_once()
    {
        using var gate = Gate(waitSeconds: 30, maxWaiting: 3);
        using var first = await gate.EnterAsync(CancellationToken.None);
        using var stop = new CancellationTokenSource();
        var waiting = Enumerable.Range(0, 3).Select(_ => gate.EnterAsync(stop.Token)).ToList();
        await Task.Delay(100);

        var fourth = await gate.EnterAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        fourth.ShouldBeNull();
        await stop.CancelAsync();
        foreach (var w in waiting)
        {
            await Should.ThrowAsync<OperationCanceledException>(async () => await w);
        }
    }

    [Fact]
    public async Task A_request_dropped_in_the_queue_gives_its_place_back()
    {
        using var gate = Gate(waitSeconds: 30, maxWaiting: 1);
        var first = await gate.EnterAsync(CancellationToken.None);
        using var dropped = new CancellationTokenSource();
        var waiting = gate.EnterAsync(dropped.Token);
        await Task.Delay(100);

        await dropped.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(async () => await waiting);
        var replacement = gate.EnterAsync(CancellationToken.None);
        await Task.Delay(100);

        replacement.IsCompleted.ShouldBeFalse("it queues in the place the dropped request left");
        first!.Dispose();
        using var turn = await replacement.WaitAsync(TimeSpan.FromSeconds(5));
        turn.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_request_already_dropped_does_not_take_the_reader()
    {
        using var gate = Gate();
        using var dropped = new CancellationTokenSource();
        await dropped.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () => await gate.EnterAsync(dropped.Token));

        using var turn = await gate.EnterAsync(CancellationToken.None);
        turn.ShouldNotBeNull();
    }
}
