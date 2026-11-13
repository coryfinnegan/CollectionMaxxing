using System.Text.Json;
using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Infrastructure.Evals;
using CollectionMaxxing.TestSupport.Evals;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static CollectionMaxxing.Recognition.Evals.EvalFixtures;

namespace CollectionMaxxing.Recognition.Evals;

/// <summary>
/// The two rows the server makes by reading the golden photos itself (ADR-033): <c>server_model</c> and <c>server_vision</c>. They are outside the gate: nothing
/// that existed before them moves, the regression check never judges them, and they count neither as scored photos nor as a source of draft truth.
/// Neutral titles throughout.
/// </summary>
public sealed class ServerRowsTests : IDisposable
{
    private const string ServerModel = ServerEngines.Model;
    private const string ServerVision = ServerEngines.Vision;

    private readonly SyntheticGolden _golden = new();

    public void Dispose() => _golden.Dispose();

    private IReadOnlyDictionary<string, TruthEntry> ArrangeShelf(params string[] titles)
    {
        _golden.AddShelf("s1", titles.Select(t => ShelfItem(t)));
        return TruthsOf(_golden);
    }

    private static GroundedPhoto PhoneModel(params GroundedItem[] items) => Photo("s1", Apple, items, latencyMs: 4000, firstItemMs: 900);

    private static GroundedPhoto PhoneVision(params GroundedItem[] items) => Photo("s1", Vision, items, latencyMs: 700, firstItemMs: null);

    private static GroundedPhoto ServerRead(params GroundedItem[] items) => Photo("s1", ServerModel, items, latencyMs: 12000, firstItemMs: null);

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static GroundedItem VisionGuess(string title) => Kept(Matched(title, resolvedBy: "vision_text"));

    [Fact]
    public void Server_vision_is_the_server_first_combination_of_the_server_row_and_the_Vision_row()
    {
        var truths = ArrangeShelf("Quiet Harbor", "Lantern Row", "Paper Moon");
        var vision = PhoneVision(VisionGuess("Lantern Row"));
        var phone = PhoneModel(T1(Matched("Quiet Harbor")));
        var server = ServerRead(T1(Matched("Paper Moon")), T1(Matched("Zebra Opus")));

        var scored = EvalScorer.Score([phone, vision, server], truths);

        scored.ShelfOf(ServerModel).Top1Hits.ShouldBe(1);
        scored.ShelfOf(ServerVision).Top1Hits.ShouldBe(2, "the server's items and the Vision item beside them; the phone model's own read is left out (#814)");
        scored.ShelfOf(ServerVision).PredictedItems.ShouldBe(3);
    }

    [Fact]
    public void Every_row_that_existed_before_is_identical_with_and_without_the_server_rows()
    {
        var truths = ArrangeShelf("Quiet Harbor", "Lantern Row", "Paper Moon");
        GroundedPhoto[] phone = [PhoneModel(T1(Matched("Quiet Harbor"))), PhoneVision(VisionGuess("Lantern Row"))];

        var without = EvalScorer.Score(phone, truths);
        var with = EvalScorer.Score([.. phone, ServerRead(T1(Matched("Paper Moon")))], truths);

        with.Engines.Keys.Order().ToArray().ShouldBe(new[] { Apple, EvalCombination.Engine, ServerModel, ServerVision, Vision }.Order().ToArray());
        foreach (var (engine, summary) in without.Engines)
        {
            Json(with.Engines[engine]).ShouldBe(Json(summary), engine);
        }

        Json(with.Misses.Where(m => !ServerEngines.Is(m.Engine))).ShouldBe(Json(without.Misses));
    }

    [Fact]
    public void The_gate_the_regression_result_the_metrics_and_the_scored_photos_are_identical_with_and_without_the_server_rows()
    {
        var truths = ArrangeShelf("Quiet Harbor", "Lantern Row", "Paper Moon");
        GroundedPhoto[] phone = [PhoneModel(T1(Matched("Quiet Harbor"))), PhoneVision(VisionGuess("Lantern Row"))];
        var baseline = EvalRegression.Flatten(EvalScorer.Score(phone, truths).Engines);

        var without = EvalReport.Evaluate(RunOf(phone), truths, baseline, "abc1234", []);
        var with = EvalReport.Evaluate(RunOf([.. phone, ServerRead(T1(Matched("Paper Moon")))]), truths, baseline, "abc1234", [], serverReader: "model m, prompt hash h");

        Json(with.Summary.Gate).ShouldBe(Json(without.Summary.Gate));
        Json(with.Summary.Regression).ShouldBe(Json(without.Summary.Regression));
        with.Summary.ScoredPhotos.ShouldBe(without.Summary.ScoredPhotos);
        with.Summary.PhotosRun.ShouldBe(without.Summary.PhotosRun);
        foreach (var (metric, value) in without.Summary.Metrics)
        {
            with.Summary.Metrics[metric].ShouldBe(value, metric);
        }

        with.Summary.Metrics.Keys.ShouldContain(k => k.StartsWith("server_model.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ServerModel)]
    [InlineData(ServerVision)]
    public void The_regression_check_never_judges_a_server_row(string engine)
    {
        var baseline = new Dictionary<string, double> { [$"{engine}.shelf.top1"] = 90, [$"{engine}.shelf.recall"] = 90, [$"{engine}.shelf.invented_pct"] = 1, ["apple_model.shelf.top1"] = 80 };
        var actual = new Dictionary<string, double> { [$"{engine}.shelf.top1"] = 10, [$"{engine}.shelf.recall"] = 10, [$"{engine}.shelf.invented_pct"] = 60, ["apple_model.shelf.top1"] = 80 };

        var check = EvalRegression.Check(baseline, actual);

        check.Regressions.ShouldBeEmpty();
        check.Status.ShouldBe("ok");
    }

    [Fact]
    public void A_read_cut_short_scores_as_a_timeout_with_its_items_kept()
    {
        var truths = ArrangeShelf("Quiet Harbor", "Lantern Row");

        var scored = EvalScorer.Score([PhoneVision(), ServerRead(T1(Matched("Quiet Harbor"))) with { TimedOut = true }], truths);

        var row = scored.ShelfOf(ServerModel);
        row.Timeouts.ShouldBe(1);
        row.Top1Hits.ShouldBe(1);
    }

    [Fact]
    public void The_report_says_the_server_rows_are_outside_the_gate_and_which_model_and_prompt_read_them()
    {
        var truths = ArrangeShelf("Quiet Harbor");

        var report = EvalReport.Evaluate(
            RunOf(PhoneModel(T1(Matched("Quiet Harbor"))), PhoneVision(), ServerRead(T1(Matched("Quiet Harbor")))),
            truths, null, "abc1234", [], serverReader: "model example-vl, prompt hash 0123456789ab").ReportMarkdown;

        report.ShouldContain("## server_model");
        report.ShouldContain("## server_vision");
        report.ShouldContain("Outside the gate");
        report.ShouldContain("model example-vl, prompt hash 0123456789ab");
    }

    [Fact]
    public void A_report_of_a_run_the_server_did_not_read_has_no_server_section()
    {
        var truths = ArrangeShelf("Quiet Harbor");

        var report = EvalReport.Evaluate(RunOf(PhoneModel(T1(Matched("Quiet Harbor"))), PhoneVision()), truths, null, "abc1234", []).ReportMarkdown;

        report.ShouldNotContain("server_");
        report.ShouldNotContain("Outside the gate");
    }

    [Fact]
    public async Task No_draft_truth_file_is_made_from_a_server_row()
    {
        _golden.AddShelf("s1", null); // no truth file: a draft is written for it
        var set = GoldenSetOf(_golden);
        var scoring = new EvalRunScoring(
            set, new FileEvalResults(Options.Create(new EvalsOptions { Enabled = true, ResultsPath = _golden.Results })), null!,
            Options.Create(new EvalsOptions()), new FixedScoring(), NullLogger<EvalRunScoring>.Instance);
        var skipped = new List<SkippedPhoto>();
        var truthSet = scoring.Prepare(["s1"], skipped);
        var phone = PhoneModel(T1(Matched("Quiet Harbor")));
        var server = ServerRead(T1(Matched("Paper Moon")), T1(Matched("Lantern Row")), T1(Matched("Salt Road"))); // more matches than the phone's
        var run = new EvalRunToScore("run1", new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero), new EvalDevice("iPhone18,1", "26.0", "0.3.0"), "cfg-1", false, null, [server, phone]);

        var scored = await scoring.ScoreAsync(run, truthSet, skipped, CancellationToken.None);

        scored.IsSuccess.ShouldBeTrue();
        var draft = File.ReadAllText(Path.Combine(_golden.Golden, "shelf", "s1.truth.draft.json"));
        draft.ShouldContain("Quiet Harbor");
        draft.ShouldNotContain("Paper Moon");
    }

    private sealed class FixedScoring : IOptionsMonitor<ScoringOptions>
    {
        public ScoringOptions CurrentValue { get; } = new();

        public ScoringOptions Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<ScoringOptions, string?> listener) => new Nop();

        private sealed class Nop : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
