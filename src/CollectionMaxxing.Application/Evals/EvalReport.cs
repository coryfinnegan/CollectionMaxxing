using System.Globalization;
using System.Text;

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
        IReadOnlyList<SkippedPhoto> skipped)
    {
        var scored = EvalScorer.Score(run.Photos, truths);
        var metrics = EvalRegression.Flatten(scored.Engines);
        var summary = new EvalSummary(
            run.RunId,
            run.At.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            buildSha,
            run.ConfigVersion,
            run.Device,
            run.Photos.Count(p => truths.ContainsKey(p.PhotoId)),
            skipped,
            scored.Engines,
            M3Gate.Evaluate(scored.Engines),
            EvalRegression.Check(baseline, metrics),
            metrics);
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
        "refused" => 1,
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
            .Append(", refusals ").Append(slice.Refusals).Append(", p50 ").Append(slice.LatencyP50Ms?.ToString(CultureInfo.InvariantCulture) ?? "n/a")
            .Append(" ms, p95 ").Append(slice.LatencyP95Ms?.ToString(CultureInfo.InvariantCulture) ?? "n/a").AppendLine(" ms");
    }

    private static string Percent(double? value) => value is { } v ? Number(v) + "%" : "n/a";
}
