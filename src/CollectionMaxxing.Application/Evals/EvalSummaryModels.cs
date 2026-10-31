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
    int Timeouts,
    double? TimeoutPct,
    int Fallbacks,
    long? LatencyP50Ms,
    long? LatencyP95Ms,
    long? FirstItemP50Ms,
    long? FirstItemP95Ms,
    IReadOnlyDictionary<string, KindMetrics> ByKind,
    IReadOnlyDictionary<string, int> ResolvedBy,
    IReadOnlyDictionary<string, KindMetrics> ByShotKind,
    int T0Auto = 0,
    int T1Auto = 0,
    int T2Review = 0,
    int ProvisionalItems = 0,
    int AutoAdds = 0,
    int CorrectAutoAdds = 0,
    double? ReviewRatePct = null,
    double? AutoAddPrecisionPct = null,
    bool? DuplicateClean = null,
    long? TimeToFirstResultsMs = null,
    long? TotalTimeToTrustworthyMs = null,
    int UnreconciledPhotos = 0,
    int ExcessUnreadable = 0);

public sealed record EngineSummary(
    SliceMetrics? Shelf,
    SliceMetrics? Single,
    string? ThermalAtEnd,
    long? LateSingleLatencyP50Ms);

public sealed record SkippedPhoto(string PhotoId, string Reason);

/// <summary>One line of the M3 gate table (docs/02): the measure, the bar, what the run measured, and pass / fail / no_data (or info, for a measure that is shown but not part of the verdict).</summary>
public sealed record GateMeasure(string Name, string Bar, string? Actual, string Status);

/// <summary>The Apple model engine on its own against the Vision text engine, on the pooled golden photos. Information only (ADR-032): it is not part of the verdict.</summary>
public sealed record VisionComparison(double? AppleTop1Pct, double? VisionTop1Pct, double? DeltaPoints, bool? AppleBeatsVision, long? AppleSingleP50Ms, long? VisionSingleP50Ms);

/// <summary>
/// The M3 verdict: <c>pass</c> (every measure met), <c>fail</c> (some measure missed) or <c>incomplete</c> (nothing missed, but something has no data yet).
/// <c>Engine</c> is the row the gate measured: <c>apple_vision</c>, the combined row, which is what the app shows (ADR-032). Result files from before that
/// gate name <c>apple_model</c> there, so a reader can tell which kind of gate a result file holds.
/// </summary>
public sealed record GateTable(string Engine, string Verdict, IReadOnlyList<GateMeasure> Measures, VisionComparison Comparison);

public sealed record RegressionItem(string Metric, double Baseline, double Actual, double Points);

/// <summary>The regression check against <c>evals/baseline.json</c>: <c>ok</c>, <c>regressed</c> (a metric moved the wrong way by more than 2 points) <c>no_baseline</c>, <c>no_overlap</c> (the baseline shares no metric with the run, so nothing was compared) or <c>partial_run</c> (the run was cancelled part way, so it is not compared).</summary>
public sealed record RegressionCheck(string Status, int Compared, IReadOnlyList<RegressionItem> Regressions);

/// <summary>
/// The summary of one run. This is what the result file and <c>GET /api/admin/evals/runs/latest</c> hold. <c>Partial</c> marks a run the phone
/// cancelled: <c>PhotosRun</c> of <c>PhotosInSet</c> golden photos (0 when not known) were run, the gate says incomplete and the regression check is skipped.
/// </summary>
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
    IReadOnlyDictionary<string, double> Metrics,
    bool Partial = false,
    int PhotosRun = 0,
    int PhotosInSet = 0,
    string? ThresholdHash = null,
    string? BaselineNote = null);

/// <summary>
/// The baseline the regression check compares to: the metrics, and the threshold fingerprint they were measured under
/// (null for baselines written before §17; those skip the threshold check).
/// </summary>
public sealed record EvalBaselineDoc(IReadOnlyDictionary<string, double> Metrics, string? ThresholdHash);

/// <summary>The response to <c>POST /api/admin/evals/runs</c>.</summary>
public sealed record EvalRunResult(string RunId, EvalSummary Summary);
