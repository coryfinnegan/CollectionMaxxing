using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Api.Tests.Scanning.ShelfRead;

// The reader does one picture at a time: a second request waits its turn, one that waits too long (or finds the queue full) is told the reader is busy,
// and a request that is dropped stops its read and leaves the queue, so the reader is free for the next.
public class ShelfReadQueueTests(ApiFixture api) : ShelfReadTest(api)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task<(HttpClient Client, Guid SessionId, Guid[] Snaps)> ArrangeAsync(int snaps)
    {
        var library = await CreateLibraryAsync();
        await CatalogAsync("Star Fox 64", ScanData.Upc(9401));
        await CatalogAsync("Wave Race 64", ScanData.Upc(9402));
        var client = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(client, library.Id);
        var ids = new List<Guid>();
        for (var i = 0; i < snaps; i++)
        {
            ids.Add(await IdentifyShelfSnapAsync(client, sessionId, "shelf_segment", Read(title: "Star Fox 64", kind: "game", year: 1996)));
        }

        Reader.Answer = _ => ScriptedReader.Found("Wave Race 64");
        return (client, sessionId, [.. ids]);
    }

    /// <summary>The first read is held inside the reader until <see cref="_release"/> is set (or the request is dropped); later reads answer at once.</summary>
    private void HoldTheFirstRead() => Reader.Hold = (call, token) => call == 1 ? _release.Task.WaitAsync(token) : Task.CompletedTask;

    private static async Task UntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Patience;
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(until, "the condition never came true");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task A_second_read_waits_its_turn_and_the_reader_is_never_asked_twice_at_once()
    {
        Settings["ShelfReader:QueueWaitSeconds"] = "30";
        var (client, sessionId, snaps) = await ArrangeAsync(2);
        using var _ = client;
        HoldTheFirstRead();
        var first = ReadShelfAsync(client, sessionId, snaps[0]);
        await UntilAsync(() => Reader.Calls == 1);

        var second = ReadShelfAsync(client, sessionId, snaps[1]);
        await Task.Delay(500);
        second.IsCompleted.ShouldBeFalse("it waits");
        Reader.Calls.ShouldBe(1);
        _release.SetResult();

        (await first).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await second).StatusCode.ShouldBe(HttpStatusCode.OK);
        Reader.Calls.ShouldBe(2);
        Reader.MostAtOnce.ShouldBe(1);
    }

    [Fact]
    public async Task A_read_that_waits_too_long_is_told_the_reader_is_busy()
    {
        Settings["ShelfReader:QueueWaitSeconds"] = "1";
        var (client, sessionId, snaps) = await ArrangeAsync(2);
        using var _ = client;
        HoldTheFirstRead();
        var first = ReadShelfAsync(client, sessionId, snaps[0]);
        await UntilAsync(() => Reader.Calls == 1);

        var second = await ReadShelfAsync(client, sessionId, snaps[1]).WaitAsync(Patience); // fails, never hangs, when the wait is ignored

        second.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        second.Headers.RetryAfter.ShouldNotBeNull();
        (await ProblemCodeAsync(second)).ShouldBe("scan.reader_busy");
        Reader.Calls.ShouldBe(1);
        _release.SetResult();
        (await first).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_read_that_finds_the_queue_full_is_told_at_once()
    {
        Settings["ShelfReader:QueueWaitSeconds"] = "30";
        Settings["ShelfReader:QueueMaxWaiting"] = "1";
        var (client, sessionId, snaps) = await ArrangeAsync(3);
        using var _ = client;
        HoldTheFirstRead();
        var first = ReadShelfAsync(client, sessionId, snaps[0]);
        await UntilAsync(() => Reader.Calls == 1);
        var second = ReadShelfAsync(client, sessionId, snaps[1]);
        await Task.Delay(500);

        var third = await ReadShelfAsync(client, sessionId, snaps[2]).WaitAsync(Patience);

        third.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await ProblemCodeAsync(third)).ShouldBe("scan.reader_busy");
        _release.SetResult();
        (await first).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await second).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_dropped_request_stops_the_read_and_frees_the_reader()
    {
        Settings["ShelfReader:QueueWaitSeconds"] = "30";
        var (client, sessionId, snaps) = await ArrangeAsync(2);
        using var _ = client;
        HoldTheFirstRead();
        using var dropped = new CancellationTokenSource();
        var first = ReadShelfAsync(client, sessionId, snaps[0], cancellationToken: dropped.Token);
        await UntilAsync(() => Reader.Calls == 1);

        await dropped.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () => await first);
        await UntilAsync(() => Reader.Cancelled == 1);
        var next = await ReadShelfAsync(client, sessionId, snaps[1]).WaitAsync(Patience);
        next.StatusCode.ShouldBe(HttpStatusCode.OK, "the reader was free again");
        (await CountCandidatesAsync(sessionId)).ShouldBe(3, "the dropped read kept nothing; the next added its row");
    }

    [Fact]
    public async Task A_request_dropped_while_it_waits_leaves_the_queue()
    {
        Settings["ShelfReader:QueueWaitSeconds"] = "30";
        Settings["ShelfReader:QueueMaxWaiting"] = "1";
        var (client, sessionId, snaps) = await ArrangeAsync(3);
        using var _ = client;
        HoldTheFirstRead();
        var first = ReadShelfAsync(client, sessionId, snaps[0]);
        await UntilAsync(() => Reader.Calls == 1);
        using var dropped = new CancellationTokenSource();
        var waiting = ReadShelfAsync(client, sessionId, snaps[1], cancellationToken: dropped.Token);
        await Task.Delay(500);

        await dropped.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(async () => await waiting);
        await Task.Delay(300);
        var third = ReadShelfAsync(client, sessionId, snaps[2]);
        await Task.Delay(500);

        third.IsCompleted.ShouldBeFalse("it took the place the dropped one left, and waits");
        _release.SetResult();
        (await first).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await third).StatusCode.ShouldBe(HttpStatusCode.OK);
        Reader.Calls.ShouldBe(2, "the dropped request never reached the reader");
    }
}
