using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Infrastructure.Evals;
using CollectionMaxxing.TestSupport.Evals;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Infrastructure.Tests.Evals;

/// <summary>
/// Another handle on a results file must not fail the eval run (#723). On Windows a move over a file fails while a reader holds it open without
/// delete sharing (what <c>File.ReadAllText</c> does), and a read fails while a move is replacing it; the stores wait out such a hold briefly.
/// On Linux a move over an open file always works, so these pass there without exercising the wait.
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
    public async Task Saving_replaces_latest_json_while_a_reader_holds_it_open_without_delete_sharing()
    {
        var results = Results();
        await results.SaveAsync(Summary("run1"), Run("run1"), "# one", CancellationToken.None);

        var (holder, held) = HoldThenRelease(Path.Combine(_golden.Results, "latest.json"), FileShare.Read, 400);
        held.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
        await results.SaveAsync(Summary("run2"), Run("run2"), "# two", CancellationToken.None);
        await holder;

        results.LoadLatestSummary()!.RunId.ShouldBe("run2");
    }

    [Fact]
    public async Task Saving_replaces_latest_run_json_while_it_is_held_open_exclusively()
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
    public async Task Loading_latest_json_succeeds_while_another_reader_holds_it_open_without_delete_sharing()
    {
        var results = Results();
        await results.SaveAsync(Summary("run1"), Run("run1"), "# one", CancellationToken.None);

        var (holder, held) = HoldThenRelease(Path.Combine(_golden.Results, "latest.json"), FileShare.Read, 300);
        held.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
        results.LoadLatestSummary()!.RunId.ShouldBe("run1");
        await holder;
    }
}
