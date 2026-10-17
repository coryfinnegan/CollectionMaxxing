#pragma warning disable CA1720 // "Single" is the set's name in the result file, not the System.Single type.
namespace CollectionMaxxing.Application.Evals;

/// <summary>Top-1 and top-3 for one item kind. Percentages are 0 to 100 (a "point" in the 2-point rule is one of these).</summary>
public sealed record KindMetrics(int Items, int Top1Hits, int Top3Hits, double? Top1Pct, double? Top3Pct);

/// <summary>
/// The numbers for one engine on one set (shelf or single). No title, no file name and no path: aggregates and ids only, so the
/// result file can be committed. A percentage is null when there was nothing to divide by.
/// </summary>
public sealed record SliceMetrics(
    int Photos,
    int TruthItems,
    int PredictedItems,
    int CorrectItems,
    int Top1Hits,
    int Top3Hits,
    double? RecallPct,
    double? PrecisionPct,
    double? Top1Pct,
    double? Top3Pct,
    double? TitleAccuracyPct,
    double? PlatformAccuracyPct,
    double? TypeAccuracyPct,
    double? CompletenessAccuracyPct,
    int InventedTitles,
    double? InventedPct,
    int Refusals,
    double? RefusalPct,
    int Failures,
    int Fallbacks,
    long? LatencyP50Ms,
    long? LatencyP95Ms,
    long? FirstItemP50Ms,
    long? FirstItemP95Ms,
    IReadOnlyDictionary<string, KindMetrics> ByKind,
    IReadOnlyDictionary<string, int> ResolvedBy);

public sealed record EngineSummary(
    SliceMetrics? Shelf,
    SliceMetrics? Single,
    string? ThermalAtEnd,
    long? LateSingleLatencyP50Ms);

public sealed record SkippedPhoto(string PhotoId, string Reason);

/// <summary>One line of the M3 gate table (docs/02): the measure, the bar, what the run measured, and pass / fail / no_data.</summary>
public sealed record GateMeasure(string Name, string Bar, string? Actual, string Status);

/// <summary>The Apple model engine against the Vision text engine, on the pooled golden photos.</summary>
public sealed record VisionComparison(double? AppleTop1Pct, double? VisionTop1Pct, double? DeltaPoints, bool? AppleBeatsVision, long? AppleSingleP50Ms, long? VisionSingleP50Ms);

/// <summary>The M3 verdict: <c>pass</c> (every measure met), <c>fail</c> (some measure missed) or <c>incomplete</c> (nothing missed, but something has no data yet).</summary>
public sealed record GateTable(string Engine, string Verdict, IReadOnlyList<GateMeasure> Measures, VisionComparison Comparison);

public sealed record RegressionItem(string Metric, double Baseline, double Actual, double Points);

/// <summary>The regression check against <c>evals/baseline.json</c>: <c>ok</c>, <c>regressed</c> (a metric moved the wrong way by more than 2 points) <c>no_baseline</c>, or <c>no_overlap</c> (the baseline shares no metric with the run, so nothing was compared).</summary>
public sealed record RegressionCheck(string Status, int Compared, IReadOnlyList<RegressionItem> Regressions);

/// <summary>The summary of one run. This is what the result file and <c>GET /api/admin/evals/runs/latest</c> hold.</summary>
public sealed record EvalSummary(
    string RunId,
    string Date,
    string BuildSha,
    string ConfigVersion,
    EvalDevice Device,
    int ScoredPhotos,
    IReadOnlyList<SkippedPhoto> Skipped,
    IReadOnlyDictionary<string, EngineSummary> Engines,
    GateTable Gate,
    RegressionCheck Regression,
    IReadOnlyDictionary<string, double> Metrics);

/// <summary>The response to <c>POST /api/admin/evals/runs</c>.</summary>
public sealed record EvalRunResult(string RunId, EvalSummary Summary);
