using System.Globalization;
using System.Text;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Application.Evals;

/// <summary>A scored run: the summary (safe to commit), the misses and the markdown report (both stay on the PC: they have titles).</summary>
public sealed record EvaluatedRun(EvalSummary Summary, IReadOnlyList<EvalMiss> Misses, string ReportMarkdown);

/// <summary>Turns a grounded run and the truth into the summary, the M3 gate table, the regression check and the report. Pure.</summary>
public static class EvalReport
{
    /// <summary>How many of the worst misses the report lists per engine.</summary>
    public const int MaxMissesPerEngine = 40;

    public static EvaluatedRun Evaluate(
        GroundedRun run,
        IReadOnlyDictionary<string, TruthEntry> truths,
        IReadOnlyDictionary<string, double>? baseline,
        string buildSha,
        IReadOnlyList<SkippedPhoto> skipped,
        int photosInSet = 0,
        ScoringThresholds? scoring = null,
        string? baselineThresholdHash = null)
    {
        var scored = EvalScorer.Score(run.Photos, truths);
        var metrics = EvalRegression.Flatten(scored.Engines);
        var thresholdHash = EvalThresholds.Fingerprint(scoring ?? new ScoringThresholds());
        var note = string.IsNullOrWhiteSpace(run.BaselineNote) ? null : run.BaselineNote.Trim();
        var summary = new EvalSummary(
            run.RunId,
            run.At.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            buildSha,
            run.ConfigVersion,
            run.Device,
            run.Photos.Count(p => truths.ContainsKey(p.PhotoId)),
            skipped,
            scored.Engines,
            M3Gate.Evaluate(scored.Engines, run.Partial),
            EvalRegression.Check(run.Partial ? null : baseline, metrics, run.Partial, new ThresholdAttestation(thresholdHash, baselineThresholdHash, note)),
            metrics,
            run.Partial,
            run.Photos.Where(p => truths.ContainsKey(p.PhotoId)).Select(p => p.PhotoId).Distinct(StringComparer.Ordinal).Count(),
            photosInSet,
            thresholdHash,
            note);
        return new EvaluatedRun(summary, scored.Misses, Markdown(summary, scored.Misses));
    }

    /// <summary>The report that stays on the PC: the gate table, the regression check and the worst misses by id (truth against what was read).</summary>
    public static string Markdown(EvalSummary summary, IReadOnlyList<EvalMiss> misses)
    {
        var text = new StringBuilder();
        text.Append("# Eval report ").Append(summary.Date).Append(" (").Append(summary.BuildSha).AppendLine(")").AppendLine();
        text.Append("Run ").Append(summary.RunId).Append(", config ").Append(summary.ConfigVersion).Append(", ")
            .Append(summary.Device.Model).Append(" / ").Append(summary.Device.OsVersion).Append(" / app ").Append(summary.Device.AppVersion)
            .AppendLine().AppendLine();
        text.Append("Scored ").Append(summary.ScoredPhotos).Append(" photos; skipped ").Append(summary.Skipped.Count).AppendLine(".").AppendLine();
        if (summary.Partial)
        {
            text.Append("**Partial run: ").Append(summary.PhotosRun).Append(summary.PhotosInSet > 0 ? " of " + summary.PhotosInSet : string.Empty)
                .AppendLine(" photos.** The phone cancelled it, so the gate says incomplete and it is not compared with the baseline or used as one.").AppendLine();
        }


        text.Append("## M3 gate: ").AppendLine(summary.Gate.Verdict.ToUpperInvariant()).AppendLine();
        text.AppendLine("| Measure | Bar | Measured | Result |").AppendLine("|---|---|---|---|");
        foreach (var measure in summary.Gate.Measures)
        {
            text.Append("| ").Append(measure.Name).Append(" | ").Append(measure.Bar).Append(" | ").Append(measure.Actual ?? "-").Append(" | ").Append(measure.Status).AppendLine(" |");
        }

        var comparison = summary.Gate.Comparison;
        text.AppendLine().Append("Apple model against Vision text, top-1: ")
            .Append(comparison.AppleTop1Pct?.ToString("0.##", CultureInfo.InvariantCulture) ?? "n/a").Append("% against ")
            .Append(comparison.VisionTop1Pct?.ToString("0.##", CultureInfo.InvariantCulture) ?? "n/a").AppendLine("%.").AppendLine();

        text.Append("## Regression check: ").AppendLine(summary.Regression.Status).AppendLine();
        if (summary.Regression.Status == EvalRegression.Incomplete)
        {
            text.AppendLine("The run's thresholds moved from the baseline's and no baseline note was sent: re-run acceptance with a note, or update the baseline.").AppendLine();
        }

        foreach (var item in summary.Regression.Regressions)
        {
            text.Append("- ").Append(item.Metric).Append(": ").Append(Number(item.Baseline)).Append(" -> ").Append(Number(item.Actual))
                .Append(" (").Append(Number(item.Points)).AppendLine(" points worse)");
        }

        foreach (var (engine, numbers) in summary.Engines)
        {
            text.AppendLine().Append("## ").AppendLine(engine).AppendLine();
            AppendSlice(text, "Shelf", numbers.Shelf);
            AppendSlice(text, "Single", numbers.Single);
            text.Append("Thermal state at the end: ").AppendLine(numbers.ThermalAtEnd ?? "n/a").AppendLine();

            var own = misses.Where(m => m.Engine == engine).OrderBy(m => Severity(m.Kind)).ThenBy(m => m.PhotoId, StringComparer.Ordinal).ToList();
            text.Append("### Worst misses (").Append(Math.Min(own.Count, MaxMissesPerEngine)).Append(" of ").Append(own.Count).AppendLine(")").AppendLine();
            foreach (var miss in own.Take(MaxMissesPerEngine))
            {
                text.Append("- `").Append(miss.PhotoId).Append("` (").Append(miss.Set).Append(", ").Append(miss.Kind).Append("): truth ")
                    .Append(miss.Truth is null ? "(none)" : "\"" + miss.Truth + "\"").Append(", read ")
                    .Append(miss.Read is null ? "(none)" : "\"" + miss.Read + "\"").AppendLine();
            }
        }

        return text.ToString();
    }

    private static int Severity(string kind) => kind switch
    {
        "invented" => 0,
        "unreconciled" => 0,
        "refused" => 1,
        "timeout" => 2,
        "error" => 2,
        _ => 3,
    };

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static void AppendSlice(StringBuilder text, string name, SliceMetrics? slice)
    {
        if (slice is null)
        {
            return;
        }

        text.Append("- ").Append(name).Append(": ").Append(slice.Photos).Append(" photos, recall ").Append(Percent(slice.RecallPct))
            .Append(", precision ").Append(Percent(slice.PrecisionPct)).Append(", top-1 ").Append(Percent(slice.Top1Pct))
            .Append(", top-3 ").Append(Percent(slice.Top3Pct)).Append(", invented ").Append(slice.InventedTitles)
            .Append(", refusals ").Append(slice.Refusals).Append(", timeouts ").Append(slice.Timeouts).Append(" (").Append(Percent(slice.TimeoutPct)).Append("), p50 ").Append(slice.LatencyP50Ms?.ToString(CultureInfo.InvariantCulture) ?? "n/a")
            .Append(" ms, p95 ").Append(slice.LatencyP95Ms?.ToString(CultureInfo.InvariantCulture) ?? "n/a").AppendLine(" ms");
        text.Append("  review ").Append(Percent(slice.ReviewRatePct)).Append(", auto-add precision ").Append(Percent(slice.AutoAddPrecisionPct))
            .Append(" (").Append(slice.CorrectAutoAdds).Append(" of ").Append(slice.AutoAdds).Append("), tiers T0 ").Append(slice.T0Auto)
            .Append(" / T1 ").Append(slice.T1Auto).Append(" / T2 ").Append(slice.T2Review).Append(" / provisional ").Append(slice.ProvisionalItems)
            .Append(", duplicates ").Append(slice.DuplicateClean is { } clean ? clean ? "clean" : "dirty" : "n/a")
            .Append(", first results ").Append(Millis(slice.TimeToFirstResultsMs)).Append(", trustworthy ").Append(Millis(slice.TotalTimeToTrustworthyMs))
            .Append(", unreconciled ").Append(slice.UnreconciledPhotos).Append(", excess unreadable ").AppendLine(slice.ExcessUnreadable.ToString(CultureInfo.InvariantCulture));
    }

    private static string Millis(long? value) => value is { } v ? v.ToString(CultureInfo.InvariantCulture) + " ms" : "n/a";

    private static string Percent(double? value) => value is { } v ? Number(v) + "%" : "n/a";
}
