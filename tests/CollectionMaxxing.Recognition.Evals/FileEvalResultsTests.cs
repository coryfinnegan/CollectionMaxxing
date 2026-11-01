using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Infrastructure.Evals;
using CollectionMaxxing.TestSupport.Evals;
using Microsoft.Extensions.Options;
using static CollectionMaxxing.Recognition.Evals.EvalFixtures;

namespace CollectionMaxxing.Recognition.Evals;

/// <summary>A run id writes at most one dated result file, even when a later write in the same save failed and the save was retried (#606).</summary>
public sealed class FileEvalResultsTests : IDisposable
{
    private readonly SyntheticGolden _golden = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cm-file-eval-results-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _golden.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static readonly SyntheticGolden.SingleItem StarFox = new("Star Fox 64", "game_cartridge", "cart_only", "loose_cartridge", "Nintendo 64");

    [Fact]
    public async Task A_run_whose_report_file_could_not_be_written_does_not_get_a_second_dated_file_when_the_save_is_retried()
    {
        _golden.AddSingle("c1", StarFox);
        var run = RunOf(Photo("c1", Apple, [Matched("Star Fox 64")]));
        var truths = TruthsOf(_golden);
        var evaluated = EvalReport.Evaluate(run, truths, null, "abc1234", []);
        Directory.CreateDirectory(_folder);
        var reportPath = Path.Combine(_folder, "report.md");
        // A directory in report.md's place makes the move onto it fail the same way on every OS, unlike a file lock.
        Directory.CreateDirectory(reportPath);

        var failed = await Record.ExceptionAsync(() => results(_folder).SaveAsync(evaluated.Summary, run, evaluated.ReportMarkdown, CancellationToken.None));
        failed.ShouldNotBeNull();

        Directory.Delete(reportPath);
        await results(_folder).SaveAsync(evaluated.Summary, run, evaluated.ReportMarkdown, CancellationToken.None);

        Directory.GetFiles(_folder, "*.json")
            .Where(f => Path.GetFileName(f) is not ("latest.json" or "latest-run.json"))
            .ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_run_whose_latest_summary_file_could_not_be_written_does_not_get_a_second_dated_file_when_the_save_is_retried()
    {
        _golden.AddSingle("c1", StarFox);
        var run = RunOf(Photo("c1", Apple, [Matched("Star Fox 64")]));
        var truths = TruthsOf(_golden);
        var evaluated = EvalReport.Evaluate(run, truths, null, "abc1234", []);
        Directory.CreateDirectory(_folder);
        var latestPath = Path.Combine(_folder, "latest.json");
        // The dated file is written and already exists by the time this write fails: a directory in latest.json's place leaves the dated write
        // from this same attempt behind, the exact setup that numbered a second dated file up before UniqueDatedPath became run-id aware.
        Directory.CreateDirectory(latestPath);

        var failed = await Record.ExceptionAsync(() => results(_folder).SaveAsync(evaluated.Summary, run, evaluated.ReportMarkdown, CancellationToken.None));
        failed.ShouldNotBeNull();

        Directory.Delete(latestPath);
        await results(_folder).SaveAsync(evaluated.Summary, run, evaluated.ReportMarkdown, CancellationToken.None);

        Directory.GetFiles(_folder, "*.json")
            .Where(f => Path.GetFileName(f) is not ("latest.json" or "latest-run.json"))
            .ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_second_run_on_the_same_day_with_the_same_sha_still_gets_its_own_dated_file()
    {
        _golden.AddSingle("c1", StarFox);
        var firstRun = RunOf(Photo("c1", Apple, [Matched("Star Fox 64")]));
        var secondRun = firstRun with { RunId = "run2" };
        var truths = TruthsOf(_golden);
        var firstEvaluated = EvalReport.Evaluate(firstRun, truths, null, "abc1234", []);
        var secondEvaluated = EvalReport.Evaluate(secondRun, truths, null, "abc1234", []);
        Directory.CreateDirectory(_folder);

        await results(_folder).SaveAsync(firstEvaluated.Summary, firstRun, firstEvaluated.ReportMarkdown, CancellationToken.None);
        await results(_folder).SaveAsync(secondEvaluated.Summary, secondRun, secondEvaluated.ReportMarkdown, CancellationToken.None);

        Directory.GetFiles(_folder, "*.json")
            .Where(f => Path.GetFileName(f) is not ("latest.json" or "latest-run.json"))
            .Count().ShouldBe(2);
    }

    private static FileEvalResults results(string folder) => new(Options.Create(new EvalsOptions { Enabled = true, ResultsPath = folder }));
}
