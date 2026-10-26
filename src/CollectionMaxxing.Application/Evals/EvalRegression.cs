namespace CollectionMaxxing.Application.Evals;

/// <summary>
/// The regression check (docs/09, "AI eval" gate): no metric may move the wrong way by more than 2 points against
/// <c>evals/baseline.json</c>. Percentages only; latencies are judged by the M3 gate, not here.
/// </summary>
public static class EvalRegression
{
    public const string Ok = "ok";
    public const string Regressed = "regressed";
    public const string NoBaseline = "no_baseline";

    /// <summary>The run was cancelled part way, so it is not compared with the baseline (and is never a baseline itself).</summary>
    public const string PartialRun = "partial_run";

    /// <summary>The baseline has numbers but the run shares none of them, so nothing was compared. That is not a pass.</summary>
    public const string NoOverlap = "no_overlap";

    /// <summary>
    /// The run's thresholds moved from the baseline's without a baseline note (docs/16 §17): nothing was compared, and that is not a pass.
    /// Re-run acceptance with a note, or update the baseline.
    /// </summary>
    public const string Incomplete = "incomplete";

    /// <summary>A metric may move this many points the wrong way; more than this is flagged.</summary>
    public const double AllowedPoints = 2.0;

    private static readonly string[] LowerIsBetter =
    [
        "invented_pct", "refusal_pct", "timeout_pct", "review_rate", "t2_review", "provisional",
        "time_to_first_results", "total_time_to_trustworthy", "unreconciled_photos", "excess_unreadable",
    ];

    /// <summary>
    /// Every comparable number in the engines' numbers, by a stable name such as <c>apple_model.single.top1</c>: percentages,
    /// per-tier counts, times in seconds and <c>duplicate_clean</c> as 100 or 0. "Points" on a count or a time are its own units.
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

    /// <summary>Compares <paramref name="actual"/> with <paramref name="baseline"/> (null when there is none). A metric the baseline does not have is new, not a regression.</summary>
    public static RegressionCheck Check(
        IReadOnlyDictionary<string, double>? baseline,
        IReadOnlyDictionary<string, double> actual,
        bool partial = false,
        ThresholdAttestation? thresholds = null)
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
            if (!actual.TryGetValue(name, out var now))
            {
                continue;
            }

            compared++;
            var worse = LowerIsBetter.Any(suffix => name.EndsWith(suffix, StringComparison.Ordinal)) ? now - was : was - now;
            if (Math.Round(worse, 2) > AllowedPoints)
            {
                regressions.Add(new RegressionItem(name, was, now, Math.Round(worse, 2)));
            }
        }

        var status = regressions.Count > 0 ? Regressed : compared == 0 ? NoOverlap : Ok;
        return new RegressionCheck(status, compared, regressions);
    }
}
