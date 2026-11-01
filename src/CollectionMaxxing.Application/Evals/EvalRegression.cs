using System.Globalization;

namespace CollectionMaxxing.Application.Evals;

/// <summary>
/// The regression check (docs/09, "AI eval" gate; docs/16 §17): a run is compared with <c>evals/baseline.json</c>, and a metric is flagged only
/// when it moved the wrong way by more than run-to-run noise explains. Three kinds of number, three rules:
/// <list type="bullet">
/// <item>A <b>rate</b> (a percentage: recall, precision, top-1, top-3, the accuracies, <c>review_rate</c>, <c>auto_add_precision</c> and the
/// per-kind slices) may move 2 points, or two standard errors of a share of the items it is measured over, whichever is more:
/// <c>max(2, 2 x 100 x sqrt(p x (1 - p) / n))</c>, with <c>p</c> the baseline value and <c>n</c> the items in this run (<see cref="Denominators"/>).
/// A rate with no <c>n</c> keeps the plain 2 points.</item>
/// <item>A <b>time</b> (seconds to first results, to trustworthy) is a regression only when this run took more than
/// <see cref="MaxTimeRatio"/> times the baseline: a phone's speed moves with its heat and load.</item>
/// <item>A <b>raw count</b> (tier counts, provisionals, unreconciled photos, excess unreadable) is not judged; it stays in the metrics and
/// the result file, and the rates and the M3 gate carry the verdict. <c>duplicate_clean</c> (100 or 0) keeps the 2 points, so a flip is flagged.</item>
/// </list>
/// The baseline holds each engine row's own numbers under that row's name (<c>apple_model.single.top1</c>) and never a gate measure,
/// so moving the gate onto the combined row (ADR-032) changes what none of its keys mean: a key from a baseline taken before it
/// is compared with the same row as ever, and the combined row's keys are new to such a baseline, not a regression.
/// </summary>
public static class EvalRegression
{
    public const string Ok = "ok";
    public const string Regressed = "regressed";
    public const string NoBaseline = "no_baseline";

    /// <summary>The run was cancelled part way, so it is not compared with the baseline (and is never a baseline itself).</summary>
    public const string PartialRun = "partial_run";

    /// <summary>The baseline has numbers but the run shares none that are judged, so nothing was compared. That is not a pass.</summary>
    public const string NoOverlap = "no_overlap";

    /// <summary>
    /// The run's thresholds moved from the baseline's without a baseline note (docs/16 §17): nothing was compared, and that is not a pass.
    /// Re-run acceptance with a note, or update the baseline.
    /// </summary>
    public const string Incomplete = "incomplete";

    /// <summary>
    /// The least a rate may move the wrong way before it is flagged, and the allowance of a rate (or <c>duplicate_clean</c>) that has no sample
    /// size. It is part of the threshold fingerprint (<see cref="EvalThresholds"/>).
    /// </summary>
    public const double AllowedPoints = 2.0;

    /// <summary>
    /// A time is a regression only when it is more than this many times the baseline. Two runs of the same build on the same phone measured a shelf
    /// 1.68 times slower the second time (the model's text engine, 238 s then 399 s), and the others 1.05 to 1.34 times, so 1.5 flagged noise.
    /// </summary>
    public const double MaxTimeRatio = 2.0;

    /// <summary>How many standard errors of a share a rate may move before it is flagged.</summary>
    private const double StandardErrors = 2.0;

    private static readonly HashSet<string> LowerIsBetter =
    [
        "invented_pct", "refusal_pct", "timeout_pct", "review_rate",
    ];

    private static readonly HashSet<string> Times = ["time_to_first_results", "total_time_to_trustworthy"];

    private static readonly HashSet<string> Counts =
    [
        "t0_auto", "t1_auto", "t2_review", "provisional", "unreconciled_photos", "excess_unreadable",
    ];

    private static readonly HashSet<string> RateNames =
    [
        "recall", "precision", "top1", "top3", "review_rate", "auto_add_precision",
    ];

    /// <summary>
    /// Every comparable number in the engines' numbers, by a stable name such as <c>apple_model.single.top1</c>: percentages,
    /// per-tier counts, times in seconds and <c>duplicate_clean</c> as 100 or 0. The units differ, so <see cref="Check"/> judges
    /// each kind by its own rule.
    /// </summary>
    public static IReadOnlyDictionary<string, double> Flatten(IReadOnlyDictionary<string, EngineSummary> engines)
    {
        var metrics = new SortedDictionary<string, double>(StringComparer.Ordinal);
        foreach (var (engine, summary) in engines)
        {
            AddSlice(metrics, $"{engine}.shelf", summary.Shelf);
            AddSlice(metrics, $"{engine}.single", summary.Single);
        }

        return metrics;
    }

    private static void AddSlice(SortedDictionary<string, double> metrics, string prefix, SliceMetrics? slice)
    {
        if (slice is null)
        {
            return;
        }

        void Add(string name, double? value)
        {
            if (value is { } v)
            {
                metrics[$"{prefix}.{name}"] = v;
            }
        }

        Add("recall", slice.RecallPct);
        Add("precision", slice.PrecisionPct);
        Add("top1", slice.Top1Pct);
        Add("top3", slice.Top3Pct);
        Add("title_accuracy", slice.TitleAccuracyPct);
        Add("platform_accuracy", slice.PlatformAccuracyPct);
        Add("type_accuracy", slice.TypeAccuracyPct);
        Add("completeness_accuracy", slice.CompletenessAccuracyPct);
        Add("invented_pct", slice.InventedPct);
        Add("refusal_pct", slice.RefusalPct);
        Add("timeout_pct", slice.TimeoutPct);
        Add("review_rate", slice.ReviewRatePct);
        Add("auto_add_precision", slice.AutoAddPrecisionPct);
        Add("t0_auto", slice.T0Auto);
        Add("t1_auto", slice.T1Auto);
        Add("t2_review", slice.T2Review);
        Add("provisional", slice.ProvisionalItems);
        Add("duplicate_clean", slice.DuplicateClean is { } clean ? clean ? 100 : 0 : null);
        Add("time_to_first_results", slice.TimeToFirstResultsMs is { } first ? first / 1000.0 : null);
        Add("total_time_to_trustworthy", slice.TotalTimeToTrustworthyMs is { } total ? total / 1000.0 : null);
        Add("unreconciled_photos", slice.UnreconciledPhotos);
        Add("excess_unreadable", slice.ExcessUnreadable);
        foreach (var (kind, numbers) in slice.ByKind)
        {
            Add($"kind.{kind}.top1", numbers.Top1Pct);
            Add($"kind.{kind}.top3", numbers.Top3Pct);
        }

        foreach (var (shot, numbers) in slice.ByShotKind)
        {
            Add($"shotkind.{shot}.top1", numbers.Top1Pct);
            Add($"shotkind.{shot}.top3", numbers.Top3Pct);
        }
    }

    /// <summary>
    /// How many items each rate in <see cref="Flatten"/> is a share of in this run, by the same names: recall, top-1, top-3 and title accuracy over
    /// the truth items (plus the unreadable ones hidden beyond what the truth allows), precision and invented titles over the items read, the type accuracy
    /// over the items whose title was found, the platform and completeness accuracies over the items checked, review rate over the truth items,
    /// auto-add precision over the auto-adds, refusals and timeouts over the photos, and the per-kind and per-shot-kind numbers over their own truth
    /// items. A rate with nothing to divide by is not in the result (nor in <see cref="Flatten"/>). This is a property of the run, so it is never
    /// written to the baseline.
    /// </summary>
    public static IReadOnlyDictionary<string, int> Denominators(IReadOnlyDictionary<string, EngineSummary> engines)
    {
        var items = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (engine, summary) in engines)
        {
            AddSliceItems(items, $"{engine}.shelf", summary.Shelf);
            AddSliceItems(items, $"{engine}.single", summary.Single);
        }

        return items;
    }

    private static void AddSliceItems(SortedDictionary<string, int> items, string prefix, SliceMetrics? slice)
    {
        if (slice is null)
        {
            return;
        }

        void Add(string name, int count)
        {
            if (count > 0)
            {
                items[$"{prefix}.{name}"] = count;
            }
        }

        // Unreadable hidden beyond what the truth allows are missed readable items: the per-truth rates divide by truth plus them (EvalScorer).
        var readable = slice.TruthItems + slice.ExcessUnreadable;
        Add("recall", readable);
        Add("top1", readable);
        Add("top3", readable);
        Add("title_accuracy", readable);
        Add("precision", slice.PredictedItems);
        Add("invented_pct", slice.PredictedItems);
        Add("type_accuracy", slice.TitleHits);
        Add("platform_accuracy", slice.PlatformChecks);
        Add("completeness_accuracy", slice.CompletenessChecks);
        Add("refusal_pct", slice.Photos);
        Add("timeout_pct", slice.Photos);
        Add("review_rate", slice.TruthItems);
        Add("auto_add_precision", slice.AutoAdds);
        foreach (var (kind, numbers) in slice.ByKind)
        {
            Add($"kind.{kind}.top1", numbers.Items);
            Add($"kind.{kind}.top3", numbers.Items);
        }

        foreach (var (shot, numbers) in slice.ByShotKind)
        {
            Add($"shotkind.{shot}.top1", numbers.Items);
            Add($"shotkind.{shot}.top3", numbers.Items);
        }
    }

    /// <summary>Whether <see cref="Check"/> judges the metric. The raw counts are not judged (and not counted as compared).</summary>
    public static bool IsJudged(string metric) => !Counts.Contains(Leaf(metric));

    /// <summary>Whether the metric is a rate: a percentage that is a share of some number of items.</summary>
    public static bool IsRate(string metric)
    {
        var leaf = Leaf(metric);
        return RateNames.Contains(leaf) || leaf.EndsWith("_pct", StringComparison.Ordinal) || leaf.EndsWith("_accuracy", StringComparison.Ordinal);
    }

    private static bool IsTime(string metric) => Times.Contains(Leaf(metric));

    private static string Leaf(string metric) => metric[(metric.LastIndexOf('.') + 1)..];

    /// <summary>
    /// Compares <paramref name="actual"/> with <paramref name="baseline"/> (null when there is none). A metric the baseline does not have is new, not a regression.
    /// <paramref name="denominators"/> are this run's sample sizes (<see cref="Denominators"/>); without them every rate is judged by the plain 2 points.
    /// </summary>
    public static RegressionCheck Check(
        IReadOnlyDictionary<string, double>? baseline,
        IReadOnlyDictionary<string, double> actual,
        bool partial = false,
        ThresholdAttestation? thresholds = null,
        IReadOnlyDictionary<string, int>? denominators = null)
    {
        if (partial)
        {
            return new RegressionCheck(PartialRun, 0, []);
        }

        if (baseline is null || baseline.Count == 0)
        {
            return new RegressionCheck(NoBaseline, 0, []);
        }

        if (thresholds is { BaselineHash: { } want, RunHash: var have, BaselineNote: var note }
            && want != have
            && string.IsNullOrWhiteSpace(note))
        {
            return new RegressionCheck(Incomplete, 0, []);
        }

        var regressions = new List<RegressionItem>();
        var compared = 0;
        foreach (var (name, was) in baseline.OrderBy(b => b.Key, StringComparer.Ordinal))
        {
            if (!IsJudged(name) || !actual.TryGetValue(name, out var now))
            {
                continue;
            }

            compared++;
            if (IsTime(name))
            {
                if (now > was * MaxTimeRatio)
                {
                    regressions.Add(new RegressionItem(name, was, now, Math.Round(now - was, 2), Math.Round(was * (MaxTimeRatio - 1), 2)));
                }

                continue;
            }

            var worse = Math.Round(LowerIsBetter.Contains(Leaf(name)) ? now - was : was - now, 2);
            var allowed = Math.Round(AllowedFor(name, was, now, denominators), 2);
            if (worse > allowed)
            {
                regressions.Add(new RegressionItem(name, was, now, worse, allowed));
            }
        }

        var status = regressions.Count > 0 ? Regressed : compared == 0 ? NoOverlap : Ok;
        return new RegressionCheck(status, compared, regressions);
    }

    /// <summary>
    /// How many points a rate may move the wrong way: two standard errors of a share <c>p</c> of <c>n</c> items, but never less than
    /// <see cref="AllowedPoints"/>. <c>p</c> is the baseline value; a baseline of exactly 0 or 100 has no spread to measure, so this run's own value stands
    /// in for it (a baseline of 100 over three auto-adds says little about a run of three that got two right). A rate with no <c>n</c>, and anything
    /// that is not a rate (<c>duplicate_clean</c>), keeps the plain floor.
    /// </summary>
    private static double AllowedFor(string metric, double was, double now, IReadOnlyDictionary<string, int>? denominators)
    {
        if (!IsRate(metric)
            || denominators is null
            || !denominators.TryGetValue(metric, out var items)
            || items <= 0
            || !double.IsFinite(was)
            || !double.IsFinite(now))
        {
            return AllowedPoints;
        }

        var share = Math.Clamp(was / 100.0, 0.0, 1.0);
        if (share is 0.0 or 1.0)
        {
            share = Math.Clamp(now / 100.0, 0.0, 1.0);
        }

        var noise = StandardErrors * 100.0 * Math.Sqrt(share * (1.0 - share) / items);
        return Math.Max(AllowedPoints, noise);
    }

    /// <summary>What a flagged item says in the report: how far the metric moved the wrong way, and how far it was allowed to ("dropped 6, allowed 5.13").</summary>
    public static string Describe(RegressionItem item)
    {
        if (IsTime(item.Metric))
        {
            return $"{Number(item.Points)} s slower, allowed {Number(item.Allowed)} s";
        }

        return $"{(LowerIsBetter.Contains(Leaf(item.Metric)) ? "rose" : "dropped")} {Number(item.Points)}, allowed {Number(item.Allowed)}";
    }

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
