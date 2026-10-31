using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Infrastructure.Evals;
using CollectionMaxxing.TestSupport.Evals;
using Microsoft.Extensions.Options;
using static CollectionMaxxing.Recognition.Evals.EvalFixtures;

namespace CollectionMaxxing.Recognition.Evals;

/// <summary>A cancelled run (EVAL-1b sends it with <c>partial: true</c>) is reported as "N of M photos" and never counts as a pass or a baseline.</summary>
public sealed class PartialRunTests : IDisposable
{
    private readonly SyntheticGolden _golden = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cm-partial-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _golden.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static readonly SyntheticGolden.SingleItem StarFox = new("Star Fox 64", "game_cartridge", "cart_only", "loose_cartridge", "Nintendo 64");

    private (GroundedRun Run, IReadOnlyDictionary<string, TruthEntry> Truths) Arrange(bool partial, bool right = true)
    {
        _golden.AddSingle("c1", StarFox);
        _golden.AddSingle("c2", StarFox);
        _golden.AddSingle("c3", StarFox);
        var title = right ? "Star Fox 64" : "Something Else Entirely";
        // The phone reads every photo with both engines, and the gate measures the row built from the two (a single cover is the model's read).
        var run = RunOf(Photo("c1", Apple, [Matched(title)]), Photo("c1", Vision, [Matched(title)])) with { Partial = partial };
        return (run, TruthsOf(_golden));
    }

    [Fact]
    public void A_partial_run_is_reported_as_N_of_M_photos_and_its_summary_says_partial()
    {
        var (run, truths) = Arrange(partial: true);

        var evaluated = EvalReport.Evaluate(run, truths, null, "abc1234", [], photosInSet: 3);

        evaluated.Summary.Partial.ShouldBeTrue();
        evaluated.Summary.PhotosRun.ShouldBe(1);
        evaluated.Summary.PhotosInSet.ShouldBe(3);
        evaluated.ReportMarkdown.ShouldContain("Partial run: 1 of 3 photos");
    }

    [Fact]
    public void A_run_without_the_flag_is_not_partial_and_the_report_does_not_say_so()
    {
        var (run, truths) = Arrange(partial: false);

        var evaluated = EvalReport.Evaluate(run, truths, null, "abc1234", [], photosInSet: 3);

        evaluated.Summary.Partial.ShouldBeFalse();
        evaluated.ReportMarkdown.ShouldNotContain("Partial run");
    }

    [Fact]
    public void The_gate_answers_incomplete_for_a_partial_run_even_when_every_measure_it_has_is_met()
    {
        var (run, truths) = Arrange(partial: true);
        var whole = EvalReport.Evaluate(run with { Partial = false }, truths, null, "abc1234", []).Summary.Gate;

        var gate = EvalReport.Evaluate(run, truths, null, "abc1234", []).Summary.Gate;

        gate.Verdict.ShouldBe(M3Gate.Incomplete);
        gate.Measures.Select(m => (m.Name, m.Status)).ShouldBe(whole.Measures.Select(m => (m.Name, m.Status))); // the measures are still shown as measured
    }

    [Fact]
    public void The_gate_for_a_partial_run_is_never_fail_either()
    {
        var (run, truths) = Arrange(partial: true, right: false);
        EvalReport.Evaluate(run with { Partial = false }, truths, null, "abc1234", []).Summary.Gate.Verdict.ShouldBe(M3Gate.Fail);

        EvalReport.Evaluate(run, truths, null, "abc1234", []).Summary.Gate.Verdict.ShouldBe(M3Gate.Incomplete);
    }

    [Fact]
    public void The_regression_check_ignores_a_partial_run()
    {
        var (run, truths) = Arrange(partial: true, right: false);
        var baseline = new Dictionary<string, double> { ["apple_model.single.top1"] = 100 };

        EvalReport.Evaluate(run with { Partial = false }, truths, baseline, "abc1234", []).Summary.Regression.Status.ShouldBe(EvalRegression.Regressed);
        var check = EvalReport.Evaluate(run, truths, baseline, "abc1234", []).Summary.Regression;

        check.Status.ShouldBe(EvalRegression.PartialRun);
        check.Compared.ShouldBe(0);
        check.Regressions.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_summary_file_of_a_partial_run_is_refused_as_a_baseline()
    {
        var (run, truths) = Arrange(partial: true);
        Directory.CreateDirectory(_folder);
        var baselinePath = Path.Combine(_folder, "baseline.json");
        var evaluated = EvalReport.Evaluate(run, truths, null, "abc1234", []);
        var options = Options.Create(new EvalsOptions { Enabled = true, ResultsPath = _folder, BaselinePath = baselinePath });
        var results = new FileEvalResults(options);

        await results.SaveAsync(evaluated.Summary, run, evaluated.ReportMarkdown, CancellationToken.None);
        File.Copy(Path.Combine(_folder, "latest.json"), baselinePath);
        results.LoadBaseline().ShouldBeNull();

        // The same summary of a complete run is a fine baseline.
        var whole = EvalReport.Evaluate(run with { Partial = false }, truths, null, "abc1234", []);
        await results.SaveAsync(whole.Summary, run with { Partial = false }, whole.ReportMarkdown, CancellationToken.None);
        File.Copy(Path.Combine(_folder, "latest.json"), baselinePath, overwrite: true);
        results.LoadBaseline().ShouldNotBeNull().ShouldNotBeEmpty();
    }
}
