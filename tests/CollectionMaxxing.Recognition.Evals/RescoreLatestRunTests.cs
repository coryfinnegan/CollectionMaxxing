using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Infrastructure.Evals;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Recognition.Evals;

/// <summary>
/// Re-scores the latest uploaded run from the PC's files (docs/09, "How it runs"): the owner's real golden folder, the grounded run
/// the server kept, the baseline. It runs only with <c>RUN_EVALS=1</c> and the <c>Category=Eval</c> filter; without them the
/// project file leaves it out by category (it is not marked Skip). Settings come from the environment, named like the server's
/// own: <c>Evals__GoldenPath</c>, <c>Evals__ResultsPath</c>, and optionally <c>Evals__BaselinePath</c> (default: the repo's
/// <c>evals/baseline.json</c>) and <c>Evals__BuildSha</c>.
/// </summary>
[Trait("Category", "Eval")]
public sealed class RescoreLatestRunTests
{
    [Fact]
    public async Task The_latest_uploaded_run_scores_again_from_the_files_and_does_not_regress_the_baseline()
    {
        Environment.GetEnvironmentVariable("RUN_EVALS").ShouldBe("1", "Set RUN_EVALS=1 to run the evals against the files on this PC.");
        var options = new EvalsOptions
        {
            Enabled = true,
            GoldenPath = Required("Evals__GoldenPath"),
            ResultsPath = Required("Evals__ResultsPath"),
            BaselinePath = Environment.GetEnvironmentVariable("Evals__BaselinePath") ?? Path.Combine(RepoRoot(), "evals", "baseline.json"),
            BuildSha = Environment.GetEnvironmentVariable("Evals__BuildSha"),
        };
        var golden = new FileGoldenSet(Options.Create(options));
        var results = new FileEvalResults(Options.Create(options));
        golden.IsAvailable.ShouldBeTrue("Evals__GoldenPath is not a folder.");

        var run = results.LoadLatestRun();
        run.ShouldNotBeNull("No uploaded run yet: tap Run evals on the phone first.");

        var truths = new Dictionary<string, TruthEntry>();
        var skipped = new List<SkippedPhoto>();
        foreach (var photo in golden.List())
        {
            if (golden.ReadTruth(photo.Id) is { } text && TruthFiles.Parse(photo.Id, photo.Set, text) is { } truth)
            {
                truths[photo.Id] = truth;
            }
            else
            {
                skipped.Add(new SkippedPhoto(photo.Id, "no_truth"));
            }
        }

        var evaluated = EvalReport.Evaluate(run, truths, results.LoadBaseline(), BuildSha.Resolve(options), skipped);
        await results.SaveAsync(evaluated.Summary, run, evaluated.ReportMarkdown, CancellationToken.None);

        evaluated.Summary.ScoredPhotos.ShouldBeGreaterThan(0, "None of the uploaded photos has a truth file.");
        evaluated.Summary.Regression.Regressions.Select(r => $"{r.Metric}: {r.Baseline} -> {r.Actual}").ShouldBeEmpty();
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException($"Set {name}.");

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CollectionMaxxing.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Can't find the repository root (CollectionMaxxing.slnx).");
    }
}
