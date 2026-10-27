using System.Text.Json;
using System.Text.RegularExpressions;
using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.TestSupport.Evals;
using static CollectionMaxxing.Recognition.Evals.EvalFixtures;

namespace CollectionMaxxing.Recognition.Evals;

/// <summary>
/// The #348 acceptance path end to end, on sample data: the committed ground-truth template, filled with sample
/// titles, parses as truth; a synthetic run over it scores through <see cref="EvalScorer"/>/<see cref="EvalReport"/>
/// into the full metric set (recall, precision, time-to-first-results, total-time-to-trustworthy, review_rate,
/// duplicate_clean, auto_add_precision); the PASS/FAIL lines read off those harness numbers, never hand-computed.
/// </summary>
public sealed class AcceptanceHarnessPathTests : IDisposable
{
    private static readonly JsonDocumentOptions Commented = new() { CommentHandling = JsonCommentHandling.Skip };

    private readonly SyntheticGolden _golden = new();

    public void Dispose() => _golden.Dispose();

    /// <summary>The committed template with every placeholder replaced by an obvious sample title.</summary>
    private static string FilledTemplate()
    {
        var path = Path.Combine(RepoRoot(), "evals", "acceptance", "shelf-truth.template.json");
        File.Exists(path).ShouldBeTrue($"The acceptance template is missing: {path}.");
        var text = File.ReadAllText(path);
        text.ShouldContain("SLOT-01");
        return Regex.Replace(text.Replace("SLOT - ", string.Empty), @"SLOT-(\d+)", m => $"Acceptance Sample {m.Groups[1].Value}");
    }

    private Dictionary<string, TruthEntry> AcceptanceTruths()
    {
        _golden.AddShelf("accept-shelf", truth: null);
        _golden.AddFile("shelf", "accept-shelf.truth.json", FilledTemplate());
        return TruthsOf(_golden);
    }

    private static GroundedPhoto AcceptancePhoto(IEnumerable<GroundedItem> items, DuplicateRescan? rescan = null)
    {
        var read = items.ToList();
        return Photo(
            "accept-shelf", Apple, read,
            latencyMs: 120_000, firstItemMs: 5_000,
            capturedCount: read.Count,
            capturedAt: new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero),
            duplicateRescan: rescan);
    }

    private IReadOnlyDictionary<string, double> AcceptanceMetrics(GroundedPhoto photo)
    {
        _golden.AddShelf("accept-shelf", truth: null);
        _golden.AddFile("shelf", "accept-shelf.truth.json", FilledTemplate());
        var evaluated = EvalReport.Evaluate(RunOf(photo), TruthsOf(_golden), baseline: null, "test", skipped: [], photosInSet: 1);
        evaluated.Summary.ScoredPhotos.ShouldBe(1);
        return evaluated.Summary.Metrics;
    }

    [Fact]
    public void The_filled_template_parses_as_acceptance_truth_with_the_required_composition()
    {
        var truth = AcceptanceTruths()["accept-shelf"].Shelf!;

        truth.Items.Count.ShouldBeInRange(25, 40);
        using var document = JsonDocument.Parse(FilledTemplate(), Commented);
        var root = document.RootElement;
        root.GetProperty("_acceptance").GetProperty("seriesSlots").GetArrayLength().ShouldBeGreaterThanOrEqualTo(3);
        root.GetProperty("_acceptance").GetProperty("omnibusSlot").GetInt32().ShouldBeInRange(1, truth.Items.Count);
        root.GetProperty("_acceptance").GetProperty("wornSpineSlot").GetInt32().ShouldBeInRange(1, truth.Items.Count);
    }

    [Fact]
    public void The_full_metric_set_comes_out_of_the_harness_for_a_filled_template()
    {
        // 27 correct auto-adds, one reviewed read, one kept provisional, one miss, clean duplicate evidence.
        var reads = Enumerable.Range(1, 27).Select(n => T1(Matched($"Acceptance Sample {n:00}"))).ToList();
        reads.Add(T2(Matched("Acceptance Sample 28")));
        reads.Add(Kept(Matched("Acceptance Sample 29")));
        var metrics = AcceptanceMetrics(AcceptancePhoto(reads, Rescan(30, 30, 0)));

        metrics["apple_model.shelf.recall"].ShouldBe(96.67);
        metrics["apple_model.shelf.precision"].ShouldBe(100);
        metrics["apple_model.shelf.time_to_first_results"].ShouldBe(120);
        metrics["apple_model.shelf.total_time_to_trustworthy"].ShouldBe(120);
        metrics["apple_model.shelf.review_rate"].ShouldBe(10);
        metrics["apple_model.shelf.duplicate_clean"].ShouldBe(100);
        metrics["apple_model.shelf.auto_add_precision"].ShouldBe(100);
    }

    [Fact]
    public void The_acceptance_lines_read_pass_from_a_passing_run()
    {
        var reads = Enumerable.Range(1, 27).Select(n => T1(Matched($"Acceptance Sample {n:00}"))).ToList();
        reads.Add(T2(Matched("Acceptance Sample 28")));
        reads.Add(Kept(Matched("Acceptance Sample 29")));
        var metrics = AcceptanceMetrics(AcceptancePhoto(reads, Rescan(30, 30, 0)));

        metrics["apple_model.shelf.recall"].ShouldBeGreaterThanOrEqualTo(90, "PASS needs recall >= 90%.");
        metrics["apple_model.shelf.precision"].ShouldBeGreaterThanOrEqualTo(95, "PASS needs precision >= 95%.");
        metrics["apple_model.shelf.review_rate"].ShouldBeLessThanOrEqualTo(25, "PASS needs review burden <= 25%.");
        metrics["apple_model.shelf.auto_add_precision"].ShouldBe(100, "Any silent false positive fails the run.");
        metrics["apple_model.shelf.duplicate_clean"].ShouldBe(100, "PASS needs duplicates clean.");
        // The barcode baseline for the same shelf (procedure §2): 30 items at ~10 s/item.
        metrics["apple_model.shelf.total_time_to_trustworthy"].ShouldBeLessThan(300, "PASS needs faster than barcode.");
    }

    [Fact]
    public void The_acceptance_lines_read_fail_when_recall_misses()
    {
        var reads = Enumerable.Range(1, 20).Select(n => T1(Matched($"Acceptance Sample {n:00}")));
        var metrics = AcceptanceMetrics(AcceptancePhoto(reads, Rescan(30, 30, 0)));

        metrics["apple_model.shelf.recall"].ShouldBe(66.67);
        (metrics["apple_model.shelf.recall"] < 75).ShouldBeTrue("Recall under 75% is a FAIL dimension.");
    }

    [Fact]
    public void The_acceptance_lines_read_fail_on_any_silent_false_positive()
    {
        // 29 right auto-adds and one invented title the pipeline auto-added: no human saw it.
        var reads = Enumerable.Range(1, 28).Select(n => T1(Matched($"Acceptance Sample {n:00}"))).ToList();
        reads.Add(T1(Matched("Acceptance Sample 99")));
        reads.Add(T1(Matched("Acceptance Sample 30")));
        var metrics = AcceptanceMetrics(AcceptancePhoto(reads, Rescan(30, 30, 0)));

        metrics["apple_model.shelf.precision"].ShouldBeGreaterThanOrEqualTo(95);
        metrics["apple_model.shelf.auto_add_precision"].ShouldBeLessThan(100, "A silent false positive fails even when overall precision passes.");
    }

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
