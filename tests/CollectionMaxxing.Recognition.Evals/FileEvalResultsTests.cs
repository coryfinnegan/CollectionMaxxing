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
    public async Task A_run_whose_report_file_was_locked_does_not_get_a_second_dated_file_when_the_save_is_retried()
    {
        _golden.AddSingle("c1", StarFox);
        var run = RunOf(Photo("c1", Apple, [Matched("Star Fox 64")]));
        var truths = TruthsOf(_golden);
        var evaluated = EvalReport.Evaluate(run, truths, null, "abc1234", []);
        Directory.CreateDirectory(_folder);
        var reportPath = Path.Combine(_folder, "report.md");
        File.WriteAllText(reportPath, "stale");
        var results = new FileEvalResults(Options.Create(new EvalsOptions { Enabled = true, ResultsPath = _folder }));

        using (new FileStream(reportPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // Windows answers a locked destination with either, depending on how the move is denied.
            var failed = await Record.ExceptionAsync(() => results.SaveAsync(evaluated.Summary, run, evaluated.ReportMarkdown, CancellationToken.None));
            (failed is IOException or UnauthorizedAccessException).ShouldBeTrue(failed?.ToString());
        }

        await results.SaveAsync(evaluated.Summary, run, evaluated.ReportMarkdown, CancellationToken.None);

        Directory.GetFiles(_folder, "*.json")
            .Where(f => Path.GetFileName(f) is not ("latest.json" or "latest-run.json"))
            .ShouldHaveSingleItem();
    }
}
