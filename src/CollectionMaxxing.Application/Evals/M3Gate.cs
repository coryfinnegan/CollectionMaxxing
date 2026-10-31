using System.Globalization;

namespace CollectionMaxxing.Application.Evals;

/// <summary>
/// The M3 gate (docs/02, "The M3 gate"): what the app shows must meet every bar below on the golden photos. That is the
/// combined row (<see cref="MeasuredEngine"/>, built by <see cref="EvalCombination"/>, ADR-032): on a shelf Vision's items plus the model's
/// unconfirmed additions, on a single cover the model's own read with the cover checks. The bars are the ones in docs/02.
/// Pure: the numbers come from <see cref="EvalScorer"/>. A measure with no photos behind it is <c>no_data</c>, which keeps
/// the verdict from saying <c>pass</c> on a golden set that does not cover everything yet.
/// </summary>
/// <remarks>
/// <para>
/// The model-only and Vision-only rows stay in the report for diagnosis, and two measures still look at them:
/// <c>model_timeouts</c> reads the model's own row and <c>top1.beats_vision_text</c> compares the model with the Vision text engine.
/// The second is information (<see cref="Info"/>): it is shown, and the verdict ignores it.
/// </para>
/// <para>
/// A run needs both a model and a Vision row for a photo to have a combined row (the phone reads every photo with all three engines).
/// A run without them has no combined row and every measure that reads it is <c>no_data</c>, so it is <c>incomplete</c>, never a pass.
/// </para>
/// </remarks>
public static class M3Gate
{
    /// <summary>The Apple model's own row. The gate does not measure it (ADR-032); it is read for the model's timeouts and for the comparison with Vision text.</summary>
    public const string Engine = "apple_model";
    public const string VisionEngine = "vision_pipeline";

    /// <summary>The row the gate measures: the combined row, which is what the app shows (ADR-032).</summary>
    public const string MeasuredEngine = EvalCombination.Engine;

    public const string Pass = "pass";
    public const string Fail = "fail";
    public const string NoData = "no_data";
    public const string Incomplete = "incomplete";

    /// <summary>A measure that is shown but is not part of the verdict.</summary>
    public const string Info = "info";

    /// <summary>p50 per single item, in milliseconds.</summary>
    public const long SingleP50Ms = 2500;

    /// <summary>The first card from a shelf, in milliseconds (p50 over shelf pictures).</summary>
    public const long ShelfFirstItemMs = 2000;

    /// <summary>The whole shelf, in milliseconds (p50 over shelf pictures).</summary>
    public const long ShelfTotalMs = 8000;

    /// <summary>Invented titles that survive grounding must be under this share of items (percent).</summary>
    public const double MaxInventedPct = 1.0;

    /// <summary>Guardrail refusals must be under this share of golden photos (percent).</summary>
    public const double MaxRefusalPct = 2.0;

    /// <summary>Auto-added items (T0 + T1, which no human saw) must be right at least this often (percent). Stricter than precision overall.</summary>
    public const double MinAutoAddPrecisionPct = 99.0;

    /// <param name="partial">The run was cancelled part way: the verdict is <c>incomplete</c> whatever the measures say.</param>
    public static GateTable Evaluate(IReadOnlyDictionary<string, EngineSummary> engines, bool partial = false)
    {
        engines.TryGetValue(MeasuredEngine, out var shown);
        engines.TryGetValue(Engine, out var model);
        engines.TryGetValue(VisionEngine, out var vision);
        var measures = new List<GateMeasure>();

        foreach (var (kind, target) in EvalKinds.Targets)
        {
            var (items, hits, _) = KindTops(shown, kind);
            var actual = EvalScorer.Pct(hits, items);
            measures.Add(Measure($"top1.{kind}", $">= {target.Top1:0.#}%", actual is null ? null : $"{actual:0.##}%", actual is { } a ? a >= target.Top1 : null));
        }

        foreach (var (kind, target) in EvalKinds.Targets)
        {
            var (items, _, hits) = KindTops(shown, kind);
            var actual = EvalScorer.Pct(hits, items);
            measures.Add(Measure($"top3.{kind}", $">= {target.Top3:0.#}%", actual is null ? null : $"{actual:0.##}%", actual is { } a ? a >= target.Top3 : null));
        }

        // Information, not a bar (ADR-032): it compares the model's own row with Vision's, shows the numbers, and the verdict
        // ignores it.
        var modelTop1 = OverallTop1(model);
        var visionTop1 = OverallTop1(vision);
        var beats = modelTop1 is { } at && visionTop1 is { } vt ? at > vt : (bool?)null;
        measures.Add(new GateMeasure(
            "top1.beats_vision_text",
            "information: the model against the Vision text engine; not part of the verdict",
            modelTop1 is null || visionTop1 is null ? null : $"{modelTop1:0.##}% vs {visionTop1:0.##}%",
            Info));

        // Speed is the combined row's: a shelf's total is Vision's read and then the model's (a failed model's wait included), its first
        // card is Vision's, and a single cover is the model's own time.
        measures.Add(Latency("speed.single_p50", SingleP50Ms, shown?.Single?.LatencyP50Ms));
        measures.Add(Latency("speed.shelf_first_item_p50", ShelfFirstItemMs, shown?.Shelf?.FirstItemP50Ms));
        measures.Add(Latency("speed.shelf_total_p50", ShelfTotalMs, shown?.Shelf?.LatencyP50Ms));

        var predicted = (shown?.Shelf?.PredictedItems ?? 0) + (shown?.Single?.PredictedItems ?? 0);
        var invented = (shown?.Shelf?.InventedTitles ?? 0) + (shown?.Single?.InventedTitles ?? 0);
        var inventedPct = EvalScorer.Pct(invented, predicted);
        measures.Add(Measure("invented_titles", $"< {MaxInventedPct:0.#}% of items", inventedPct is null ? null : $"{inventedPct:0.##}%", inventedPct is { } ip ? ip < MaxInventedPct : null));

        var autoAdds = (shown?.Shelf?.AutoAdds ?? 0) + (shown?.Single?.AutoAdds ?? 0);
        var correctAutoAdds = (shown?.Shelf?.CorrectAutoAdds ?? 0) + (shown?.Single?.CorrectAutoAdds ?? 0);
        var autoAddPct = EvalScorer.Pct(correctAutoAdds, autoAdds);
        measures.Add(Measure(
            "auto_add_precision",
            $">= {MinAutoAddPrecisionPct:0.#}% of auto-adds right",
            autoAddPct is null ? null : $"{autoAddPct:0.##}%",
            autoAddPct is { } ap ? ap >= MinAutoAddPrecisionPct : null));

        var photos = (shown?.Shelf?.Photos ?? 0) + (shown?.Single?.Photos ?? 0);
        var refusals = (shown?.Shelf?.Refusals ?? 0) + (shown?.Single?.Refusals ?? 0);
        var refusalPct = EvalScorer.Pct(refusals, photos);
        measures.Add(Measure("guardrail_refusals", $"< {MaxRefusalPct:0.#}% of photos", refusalPct is null ? null : $"{refusalPct:0.##}%", refusalPct is { } rp ? rp < MaxRefusalPct : null));

        measures.Add(ModelTimeouts(model));

        measures.Add(Thermals(shown));

        var verdict = partial ? Incomplete : measures.Any(m => m.Status == Fail) ? Fail : measures.Any(m => m.Status == NoData) ? Incomplete : Pass;
        return new GateTable(
            MeasuredEngine,
            verdict,
            measures,
            new VisionComparison(
                modelTop1,
                visionTop1,
                modelTop1 is { } a1 && visionTop1 is { } v1 ? Math.Round(a1 - v1, 2) : null,
                beats,
                model?.Single?.LatencyP50Ms,
                vision?.Single?.LatencyP50Ms));
    }

    private static GateMeasure Measure(string name, string bar, string? actual, bool? met) =>
        new(name, bar, actual, met is null ? NoData : met.Value ? Pass : Fail);

    /// <summary>
    /// How often the model timed out, from the model's own row, not the combined one. When the model times out on a shelf the scanner
    /// keeps Vision's items, so that photo is Vision's read on the combined row (a fallback, not a timeout, see <see cref="EvalCombination"/>)
    /// and the rate there would hide a slow model. What the wait cost the person is already in the speed measures, which are the combined
    /// row's (the shelf total includes the wait for the model), so this is reported and never fails.
    /// </summary>
    private static GateMeasure ModelTimeouts(EngineSummary? model)
    {
        var photos = (model?.Shelf?.Photos ?? 0) + (model?.Single?.Photos ?? 0);
        var timeouts = (model?.Shelf?.Timeouts ?? 0) + (model?.Single?.Timeouts ?? 0);
        var timeoutPct = EvalScorer.Pct(timeouts, photos);
        return Measure("model_timeouts", "reported from the model's own row; counted in the speed measures", timeoutPct is null ? null : $"{timeoutPct:0.##}%", timeoutPct is null ? null : true);
    }

    private static GateMeasure Latency(string name, long barMs, long? actualMs) =>
        Measure(name, $"<= {barMs} ms", actualMs is null ? null : $"{actualMs} ms", actualMs is { } ms ? ms <= barMs : null);

    /// <summary>
    /// Fifteen minutes of continuous scanning must not drop the engine below the speed bar. A run is a few minutes of photos, so
    /// this checks what that can show: the phone ended no hotter than "fair", and the last third of the single pictures still met the bar.
    /// The combined row carries both numbers itself: its thermal state is that of the last photo's combined read (Vision's on a shelf,
    /// the last engine the phone reads on a photo; the model's on a single cover), and its late single latency is the model's own.
    /// </summary>
    private static GateMeasure Thermals(EngineSummary? shown)
    {
        const string bar = "ends nominal or fair; late photos still <= 2500 ms";
        if (shown?.ThermalAtEnd is not { } thermal)
        {
            return Measure("thermals", bar, null, null);
        }

        var cool = thermal is "nominal" or "fair";
        var lateOk = shown.LateSingleLatencyP50Ms is not { } late || late <= SingleP50Ms;
        var actual = string.Create(CultureInfo.InvariantCulture, $"{thermal}; late p50 {(shown.LateSingleLatencyP50Ms is { } l ? l + " ms" : "n/a")}");
        return Measure("thermals", bar, actual, cool && lateOk);
    }

    private static (int Items, int Top1Hits, int Top3Hits) KindTops(EngineSummary? engine, string kind)
    {
        var items = 0;
        var hits = 0;
        var top3 = 0;
        void Add(SliceMetrics? slice, string key)
        {
            if (slice?.ByKind.TryGetValue(key, out var k) == true)
            {
                items += k.Items;
                hits += k.Top1Hits;
                top3 += k.Top3Hits;
            }
        }

        Add(engine?.Single, kind);
        if (kind == EvalKinds.BookSpine)
        {
            Add(engine?.Shelf, "book"); // books on a shelf are spines
        }

        return (items, hits, top3);
    }

    /// <summary>Top-1 over every golden item of the engine, shelf and single together.</summary>
    private static double? OverallTop1(EngineSummary? engine)
    {
        var items = (engine?.Shelf?.TruthItems ?? 0) + (engine?.Single?.TruthItems ?? 0);
        var hits = (engine?.Shelf?.Top1Hits ?? 0) + (engine?.Single?.Top1Hits ?? 0);
        return EvalScorer.Pct(hits, items);
    }
}
