using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Infrastructure.Evals;
using CollectionMaxxing.TestSupport.Evals;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Infrastructure.Tests.Evals;

/// <summary>
/// Another handle on a results file must not fail the eval run (#723). On Windows a move over a file fails while ANY other handle is open on it
/// (whatever sharing that handle asked for), so a save waits out the hold with a bounded retry. A read that does not share delete access fails for
/// the instant a replace holds the file, so reads open with delete sharing and also retry. Linux allows all of this, so the wait-based tests pass
/// there without exercising it; the delete-sharing test fails on Windows only.
/// </summary>
public sealed class FileEvalResultsSharingTests : IDisposable
{
    private readonly SyntheticGolden _golden = new();

    public void Dispose() => _golden.Dispose();

    private FileEvalResults Results() => new(Options.Create(new EvalsOptions { Enabled = true, ResultsPath = _golden.Results }));

    private static EvalSummary Summary(string runId) => new(
        runId, "2026-11-06", "abc1234", "cfg-1", new EvalDevice("iPhone18,1", "26.0", "0.3.0"), 1, [], new Dictionary<string, EngineSummary>(),
        new GateTable("apple", "pass", [], null!), new RegressionCheck("ok", 0, []), new Dictionary<string, double>());

    private static GroundedRun Run(string runId) => new(runId, new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero), new EvalDevice("iPhone18,1", "26.0", "0.3.0"), "cfg-1", []);

    private static (Task Holder, ManualResetEventSlim Held) HoldThenRelease(string path, FileShare sharing, int holdMs)
    {
        var held = new ManualResetEventSlim();
        var holder = Task.Run(async () =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, sharing);
            held.Set();
            await Task.Delay(holdMs);
        });
        return (holder, held);
    }

    [Fact]
    public async Task Saving_waits_out_a_reader_that_holds_latest_json_for_a_moment_and_then_replaces_it()
    {
        var results = Results();
        await results.SaveAsync(Summary("run1"), Run("run1"), "# one", CancellationToken.None);

        var (holder, held) = HoldThenRelease(Path.Combine(_golden.Results, "latest.json"), FileShare.ReadWrite | FileShare.Delete, 400);
        held.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
        await results.SaveAsync(Summary("run2"), Run("run2"), "# two", CancellationToken.None);
        await holder;

        results.LoadLatestSummary()!.RunId.ShouldBe("run2");
    }

    [Fact]
    public async Task Saving_waits_out_an_exclusive_hold_on_latest_run_json_and_then_replaces_it()
    {
        var results = Results();
        await results.SaveAsync(Summary("run1"), Run("run1"), "# one", CancellationToken.None);

        var (holder, held) = HoldThenRelease(Path.Combine(_golden.Results, "latest-run.json"), FileShare.None, 400);
        held.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
        await results.SaveAsync(Summary("run2"), Run("run2"), "# two", CancellationToken.None);
        await holder;

        results.LoadLatestRun()!.RunId.ShouldBe("run2");
    }

    [Fact]
    public async Task Loading_latest_json_works_while_another_handle_holds_it_with_delete_access()
    {
        var results = Results();
        await results.SaveAsync(Summary("run1"), Run("run1"), "# one", CancellationToken.None);

        // What a replace holds: a handle with delete access. A reader that does not share delete is refused by it.
        using var replacing = new FileStream(
            Path.Combine(_golden.Results, "latest.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.DeleteOnClose);

        results.LoadLatestSummary()!.RunId.ShouldBe("run1");
    }

    [Fact]
    public async Task Loading_latest_json_waits_out_an_exclusive_hold_and_then_reads_it()
    {
        var results = Results();
        await results.SaveAsync(Summary("run1"), Run("run1"), "# one", CancellationToken.None);

        var (holder, held) = HoldThenRelease(Path.Combine(_golden.Results, "latest.json"), FileShare.None, 300);
        held.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
        results.LoadLatestSummary()!.RunId.ShouldBe("run1");
        await holder;
    }
}
