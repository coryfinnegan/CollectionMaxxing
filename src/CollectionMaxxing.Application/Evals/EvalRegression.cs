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

    /// <summary>A metric may move this many points the wrong way; more than this is flagged.</summary>
    public const double AllowedPoints = 2.0;

    private static readonly string[] LowerIsBetter = ["invented_pct", "refusal_pct"];

    /// <summary>Every percentage in the engines' numbers, by a stable name such as <c>apple_model.single.top1</c>.</summary>
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
        foreach (var (kind, numbers) in slice.ByKind)
        {
            Add($"kind.{kind}.top1", numbers.Top1Pct);
            Add($"kind.{kind}.top3", numbers.Top3Pct);
        }
    }

    /// <summary>Compares <paramref name="actual"/> with <paramref name="baseline"/> (null when there is none). A metric the baseline does not have is new, not a regression.</summary>
    public static RegressionCheck Check(IReadOnlyDictionary<string, double>? baseline, IReadOnlyDictionary<string, double> actual)
    {
        if (baseline is null || baseline.Count == 0)
        {
            return new RegressionCheck(NoBaseline, 0, []);
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

        return new RegressionCheck(regressions.Count == 0 ? Ok : Regressed, compared, regressions);
    }
}
