using System.Globalization;

namespace CollectionMaxxing.Application.Evals;

/// <summary>
/// The M3 gate (docs/02, "The M3 gate"): the Apple model engine ships as primary only if, on the golden photos, it meets every bar
/// below. Pure: the numbers come from <see cref="EvalScorer"/>. A measure with no photos behind it is <c>no_data</c>, which keeps
/// the verdict from saying <c>pass</c> on a golden set that does not cover everything yet.
/// </summary>
public static class M3Gate
{
    public const string Engine = "apple_model";
    public const string VisionEngine = "vision_pipeline";

    public const string Pass = "pass";
    public const string Fail = "fail";
    public const string NoData = "no_data";
    public const string Incomplete = "incomplete";

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

    public static GateTable Evaluate(IReadOnlyDictionary<string, EngineSummary> engines)
    {
        engines.TryGetValue(Engine, out var apple);
        engines.TryGetValue(VisionEngine, out var vision);
        var measures = new List<GateMeasure>();

        foreach (var (kind, target) in EvalKinds.Targets)
        {
            var (items, hits, _) = KindTops(apple, kind);
            var actual = EvalScorer.Pct(hits, items);
            measures.Add(Measure($"top1.{kind}", $">= {target.Top1:0.#}%", actual is null ? null : $"{actual:0.##}%", actual is { } a ? a >= target.Top1 : null));
        }

        foreach (var (kind, target) in EvalKinds.Targets)
        {
            var (items, _, hits) = KindTops(apple, kind);
            var actual = EvalScorer.Pct(hits, items);
            measures.Add(Measure($"top3.{kind}", $">= {target.Top3:0.#}%", actual is null ? null : $"{actual:0.##}%", actual is { } a ? a >= target.Top3 : null));
        }

        var appleTop1 = OverallTop1(apple);
        var visionTop1 = OverallTop1(vision);
        var beats = appleTop1 is { } at && visionTop1 is { } vt ? at > vt : (bool?)null;
        measures.Add(Measure(
            "top1.beats_vision_text", "> Vision text engine", appleTop1 is null || visionTop1 is null ? null : $"{appleTop1:0.##}% vs {visionTop1:0.##}%", beats));

        measures.Add(Latency("speed.single_p50", SingleP50Ms, apple?.Single?.LatencyP50Ms));
        measures.Add(Latency("speed.shelf_first_item_p50", ShelfFirstItemMs, apple?.Shelf?.FirstItemP50Ms));
        measures.Add(Latency("speed.shelf_total_p50", ShelfTotalMs, apple?.Shelf?.LatencyP50Ms));

        var predicted = (apple?.Shelf?.PredictedItems ?? 0) + (apple?.Single?.PredictedItems ?? 0);
        var invented = (apple?.Shelf?.InventedTitles ?? 0) + (apple?.Single?.InventedTitles ?? 0);
        var inventedPct = EvalScorer.Pct(invented, predicted);
        measures.Add(Measure("invented_titles", $"< {MaxInventedPct:0.#}% of items", inventedPct is null ? null : $"{inventedPct:0.##}%", inventedPct is { } ip ? ip < MaxInventedPct : null));

        var photos = (apple?.Shelf?.Photos ?? 0) + (apple?.Single?.Photos ?? 0);
        var refusals = (apple?.Shelf?.Refusals ?? 0) + (apple?.Single?.Refusals ?? 0);
        var refusalPct = EvalScorer.Pct(refusals, photos);
        measures.Add(Measure("guardrail_refusals", $"< {MaxRefusalPct:0.#}% of photos", refusalPct is null ? null : $"{refusalPct:0.##}%", refusalPct is { } rp ? rp < MaxRefusalPct : null));

        // Timeouts already count in the speed measures above; the rate is shown so a slow engine is not hidden behind its p50.
        var timeouts = (apple?.Shelf?.Timeouts ?? 0) + (apple?.Single?.Timeouts ?? 0);
        var timeoutPct = EvalScorer.Pct(timeouts, photos);
        measures.Add(Measure("model_timeouts", "reported; counted in the speed measures", timeoutPct is null ? null : $"{timeoutPct:0.##}%", timeoutPct is null ? null : true));

        measures.Add(Thermals(apple));

        var verdict = measures.Any(m => m.Status == Fail) ? Fail : measures.Any(m => m.Status == NoData) ? Incomplete : Pass;
        return new GateTable(
            Engine,
            verdict,
            measures,
            new VisionComparison(
                appleTop1,
                visionTop1,
                appleTop1 is { } a1 && visionTop1 is { } v1 ? Math.Round(a1 - v1, 2) : null,
                beats,
                apple?.Single?.LatencyP50Ms,
                vision?.Single?.LatencyP50Ms));
    }

    private static GateMeasure Measure(string name, string bar, string? actual, bool? met) =>
        new(name, bar, actual, met is null ? NoData : met.Value ? Pass : Fail);

    private static GateMeasure Latency(string name, long barMs, long? actualMs) =>
        Measure(name, $"<= {barMs} ms", actualMs is null ? null : $"{actualMs} ms", actualMs is { } ms ? ms <= barMs : null);

    /// <summary>
    /// Fifteen minutes of continuous scanning must not drop the engine below the speed bar. A run is a few minutes of photos, so
    /// this checks what that can show: the phone ended no hotter than "fair", and the last third of the single pictures still met the bar.
    /// </summary>
    private static GateMeasure Thermals(EngineSummary? apple)
    {
        const string bar = "ends nominal or fair; late photos still <= 2500 ms";
        if (apple?.ThermalAtEnd is not { } thermal)
        {
            return Measure("thermals", bar, null, null);
        }

        var cool = thermal is "nominal" or "fair";
        var lateOk = apple.LateSingleLatencyP50Ms is not { } late || late <= SingleP50Ms;
        var actual = string.Create(CultureInfo.InvariantCulture, $"{thermal}; late p50 {(apple.LateSingleLatencyP50Ms is { } l ? l + " ms" : "n/a")}");
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
