using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Application.Evals;

/// <summary>One thing that went wrong in a run, for the report that stays on the PC (it has titles, so it is never committed).</summary>
public sealed record EvalMiss(string PhotoId, string Set, string Engine, string Kind, string? Truth, string? Read);

/// <summary>What scoring a run gives: per-engine numbers and the misses behind them.</summary>
public sealed record ScoredEngines(IReadOnlyDictionary<string, EngineSummary> Engines, IReadOnlyList<EvalMiss> Misses);

/// <summary>
/// Scores a grounded run against the truth files (docs/09, "AI evals"). Pure: no files, no database, no clock. A photo is scored
/// per engine; within a photo, items are matched to truth items greedily in reading order, a truth item being claimed once.
/// </summary>
/// <remarks>
/// A read item is <b>correct</b> when its best release has the truth item's title (normalised the way the live scorer does) and,
/// when the truth names a platform, the same platform and, on a single picture, the same format. <b>Top-3</b> also accepts the second and third releases. An
/// <b>invented title</b> is a Matched item whose best release has the title of no truth item of the photo (a right title on the
/// wrong console is a platform error, measured on its own). In a single-item picture only
/// the first item read is the answer; any others only count against precision.
/// </remarks>
public static class EvalScorer
{
    private const string Ok = "ok";
    private const string Refused = "refused";
    private const string Error = "error";
    private const string Timeout = "timeout";

    /// <summary>
    /// The model's time budget in milliseconds (the default of <c>engine.modelTimeoutMs</c>). A timed-out photo counts in the latency
    /// percentiles at its measured time, or at this budget if that is larger: the phone gave up at the budget, so it waited at least that long.
    /// </summary>
    public const long ModelTimeoutBudgetMs = 8000;

    private sealed record TruthItem(string Title, string? Platform, string? MediaType, string? Format, string Kind);

    private sealed class Tally
    {
        public int Photos;
        public int TruthItems;
        public int Predicted;
        public int Correct;
        public int Top1Hits;
        public int Top3Hits;
        public int TitleHits;
        public int PlatformChecks;
        public int PlatformHits;
        public int TypeHits;
        public int CompletenessChecks;
        public int CompletenessHits;
        public int Invented;
        public int Refusals;
        public int Failures;
        public int Timeouts;
        public int Fallbacks;
        public List<long> Latency { get; } = [];
        public List<long> FirstItem { get; } = [];
        public Dictionary<string, (int Items, int Top1, int Top3)> Kinds { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, (int Items, int Top1, int Top3)> Shots { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> ResolvedBy { get; } = new(StringComparer.Ordinal)
        {
            [ResolvedByText.Model] = 0,
            [ResolvedByText.VisionText] = 0,
            [ResolvedByText.Artwork] = 0,
            [ResolvedByText.FixIt] = 0,
        };
    }

    public static ScoredEngines Score(IReadOnlyList<GroundedPhoto> photos, IReadOnlyDictionary<string, TruthEntry> truths)
    {
        var misses = new List<EvalMiss>();
        var engines = new SortedDictionary<string, EngineSummary>(StringComparer.Ordinal);
        foreach (var byEngine in photos.Where(p => truths.ContainsKey(p.PhotoId)).GroupBy(p => p.Engine))
        {
            var entries = byEngine.Select(p => (Photo: p, Truth: truths[p.PhotoId])).ToList();
            var shelf = entries.Where(e => e.Truth.Set == GoldenSets.Shelf).ToList();
            var single = entries.Where(e => e.Truth.Set == GoldenSets.Single).ToList();
            engines[byEngine.Key] = new EngineSummary(
                shelf.Count == 0 ? null : Slice(shelf, misses),
                single.Count == 0 ? null : Slice(single, misses),
                byEngine.Last().ThermalState,
                LateLatencyP50(single.Select(e => e.Photo).ToList()));
        }

        return new ScoredEngines(engines, misses);
    }

    /// <summary>The nearest-rank percentile (the smallest value that at least <paramref name="percent"/> of the values are at or below). Null for no values.</summary>
    public static long? Percentile(IReadOnlyList<long> values, int percent)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var sorted = values.Order().ToList();
        var rank = (int)Math.Ceiling(percent / 100.0 * sorted.Count);
        return sorted[Math.Clamp(rank, 1, sorted.Count) - 1];
    }

    /// <summary>Rounded to two places; null when there is nothing to divide by.</summary>
    public static double? Pct(int part, int whole) => whole == 0 ? null : Math.Round(100.0 * part / whole, 2, MidpointRounding.AwayFromZero);

    private static long? LateLatencyP50(IReadOnlyList<GroundedPhoto> singlePhotos)
    {
        var ok = singlePhotos.Where(p => p.Outcome == Ok).Select(p => p.LatencyMs).ToList();
        if (ok.Count == 0)
        {
            return null;
        }

        // The last third of the run (at least one photo): the phone has been working a while by then.
        var late = ok.Skip(ok.Count - (int)Math.Ceiling(ok.Count / 3.0)).ToList();
        return Percentile(late, 50);
    }

    private static SliceMetrics Slice(List<(GroundedPhoto Photo, TruthEntry Truth)> entries, List<EvalMiss> misses)
    {
        var tally = new Tally();
        foreach (var (photo, truth) in entries)
        {
            ScorePhoto(photo, truth, tally, misses);
        }

        var kinds = tally.Kinds.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(
            k => k.Key, k => new KindMetrics(k.Value.Items, k.Value.Top1, k.Value.Top3, Pct(k.Value.Top1, k.Value.Items), Pct(k.Value.Top3, k.Value.Items)));
        var shots = tally.Shots.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(
            k => k.Key, k => new KindMetrics(k.Value.Items, k.Value.Top1, k.Value.Top3, Pct(k.Value.Top1, k.Value.Items), Pct(k.Value.Top3, k.Value.Items)));
        return new SliceMetrics(
            tally.Photos,
            tally.TruthItems,
            tally.Predicted,
            tally.Correct,
            tally.Top1Hits,
            tally.Top3Hits,
            RecallPct: Pct(tally.Top1Hits, tally.TruthItems),
            PrecisionPct: Pct(tally.Correct, tally.Predicted),
            Top1Pct: Pct(tally.Top1Hits, tally.TruthItems),
            Top3Pct: Pct(tally.Top3Hits, tally.TruthItems),
            TitleAccuracyPct: Pct(tally.TitleHits, tally.TruthItems),
            PlatformAccuracyPct: Pct(tally.PlatformHits, tally.PlatformChecks),
            TypeAccuracyPct: Pct(tally.TypeHits, tally.TitleHits),
            CompletenessAccuracyPct: Pct(tally.CompletenessHits, tally.CompletenessChecks),
            tally.Invented,
            InventedPct: Pct(tally.Invented, tally.Predicted),
            tally.Refusals,
            RefusalPct: Pct(tally.Refusals, tally.Photos),
            tally.Failures,
            tally.Timeouts,
            TimeoutPct: Pct(tally.Timeouts, tally.Photos),
            tally.Fallbacks,
            Percentile(tally.Latency, 50),
            Percentile(tally.Latency, 95),
            Percentile(tally.FirstItem, 50),
            Percentile(tally.FirstItem, 95),
            kinds,
            tally.ResolvedBy,
            shots);
    }

    private static List<TruthItem> TruthItemsOf(TruthEntry truth) => truth.Set == GoldenSets.Shelf
        ? [.. truth.Shelf!.Items.Select(i => new TruthItem(i.Title, i.Platform, i.MediaType.ToLowerInvariant(), null, EvalKinds.Key(i.MediaType)))]
        : [new TruthItem(truth.Single!.Title, truth.Single.Platform, null, truth.Single.Format.ToLowerInvariant(), truth.Single.Kind)];

    private static void ScorePhoto(GroundedPhoto photo, TruthEntry truth, Tally tally, List<EvalMiss> misses)
    {
        var truthItems = TruthItemsOf(truth);
        var items = photo.Outcome == Ok ? photo.Items : [];
        tally.Photos++;
        tally.TruthItems += truthItems.Count;
        tally.Predicted += items.Count;
        tally.Refusals += photo.Outcome == Refused ? 1 : 0;
        tally.Failures += photo.Outcome == Error ? 1 : 0;
        tally.Fallbacks += photo.FellBackFrom is null ? 0 : 1;
        tally.Timeouts += photo.Outcome == Timeout ? 1 : 0;
        if (photo.Outcome == Timeout)
        {
            // The slowest reads must not vanish from p50/p95: a timeout counts at its measured time, or at the budget if larger.
            tally.Latency.Add(Math.Max(photo.LatencyMs, ModelTimeoutBudgetMs));
        }

        if (photo.Outcome == Ok)
        {
            tally.Latency.Add(photo.LatencyMs);
            if (photo.FirstItemMs is { } first)
            {
                tally.FirstItem.Add(first);
            }
        }

        // The items that may claim a truth item: all of them on a shelf, the first one on a single picture.
        var claimers = truth.Set == GoldenSets.Single ? items.Take(1).ToList() : items.ToList();
        var claimed = new bool[truthItems.Count];
        var pairOfItem = new Dictionary<int, (int Truth, bool Full)>();
        for (var i = 0; i < claimers.Count; i++)
        {
            if (claimers[i].Best is not { } best)
            {
                continue;
            }

            var match = FirstUnclaimed(truthItems, claimed, t => FullMatch(t, best)) ?? FirstUnclaimed(truthItems, claimed, t => TitleMatch(t, best));
            if (match is { } truthIndex)
            {
                claimed[truthIndex] = true;
                pairOfItem[i] = (truthIndex, FullMatch(truthItems[truthIndex], best));
            }
        }

        var top1 = new bool[truthItems.Count];
        var top3 = new bool[truthItems.Count];
        foreach (var (itemIndex, (truthIndex, full)) in pairOfItem)
        {
            var truthItem = truthItems[truthIndex];
            var best = claimers[itemIndex].Candidates[0];
            tally.TitleHits++;
            tally.TypeHits += TypeMatches(truthItem, best) ? 1 : 0;
            if (truthItem.Platform is not null && PlatformNames.Canonicalize(truthItem.Platform) is not null)
            {
                tally.PlatformChecks++;
                tally.PlatformHits += PlatformMatches(truthItem, best) ? 1 : 0;
            }

            if (full)
            {
                top1[truthIndex] = true;
                top3[truthIndex] = true;
                tally.Correct++;
                tally.ResolvedBy[claimers[itemIndex].ResolvedBy] = tally.ResolvedBy.GetValueOrDefault(claimers[itemIndex].ResolvedBy) + 1;
            }
        }

        // Top-3: what the top-1 pass settled, plus truth items that one of the other two releases of an unsettled item names.
        for (var i = 0; i < claimers.Count; i++)
        {
            if (pairOfItem.TryGetValue(i, out var pair) && pair.Full)
            {
                continue;
            }

            foreach (var candidate in claimers[i].Candidates)
            {
                if (FirstUnclaimed(truthItems, top3, t => FullMatch(t, candidate)) is { } hit)
                {
                    top3[hit] = true;
                    break;
                }
            }
        }

        if (truth.Set == GoldenSets.Single && items.Count > 0 && claimers.Count > 0)
        {
            tally.CompletenessChecks++;
            tally.CompletenessHits += string.Equals(claimers[0].ReadCompleteness, truth.Single!.Completeness, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        }

        var shot = truth.ShotKind ?? "unknown";
        for (var t = 0; t < truthItems.Count; t++)
        {
            var kind = truthItems[t].Kind;
            var current = tally.Kinds.GetValueOrDefault(kind);
            tally.Kinds[kind] = (current.Items + 1, current.Top1 + (top1[t] ? 1 : 0), current.Top3 + (top3[t] ? 1 : 0));
            var shotCurrent = tally.Shots.GetValueOrDefault(shot);
            tally.Shots[shot] = (shotCurrent.Items + 1, shotCurrent.Top1 + (top1[t] ? 1 : 0), shotCurrent.Top3 + (top3[t] ? 1 : 0));
            tally.Top1Hits += top1[t] ? 1 : 0;
            tally.Top3Hits += top3[t] ? 1 : 0;
            if (!top1[t])
            {
                misses.Add(new EvalMiss(
                    photo.PhotoId, truth.Set, photo.Engine, photo.Outcome == Ok ? "miss" : photo.Outcome, truthItems[t].Title, ReadSummary(photo, items)));
            }
        }

        foreach (var item in items)
        {
            if (item.Verdict == "matched" && item.Best is { } best && !truthItems.Any(t => TitleMatch(t, best)))
            {
                tally.Invented++;
                misses.Add(new EvalMiss(photo.PhotoId, truth.Set, photo.Engine, "invented", null, best.Title));
            }
        }
    }

    private static string ReadSummary(GroundedPhoto photo, IReadOnlyList<GroundedItem> items)
    {
        if (photo.Outcome != Ok)
        {
            return "(" + photo.Outcome + ")";
        }

        var read = items.Select(i => i.Best?.Title ?? i.ReadTitle).Where(t => !string.IsNullOrWhiteSpace(t)).Take(3).ToList();
        return read.Count == 0 ? "(nothing read)" : string.Join(" | ", read);
    }

    private static int? FirstUnclaimed(List<TruthItem> truthItems, bool[] claimed, Func<TruthItem, bool> test)
    {
        for (var t = 0; t < truthItems.Count; t++)
        {
            if (!claimed[t] && test(truthItems[t]))
            {
                return t;
            }
        }

        return null;
    }

    private static bool TitleMatch(TruthItem truth, GroundedCandidate candidate)
    {
        var wanted = TitleNormalizer.Normalize(truth.Title);
        return wanted.Length > 0 && wanted == TitleNormalizer.Normalize(candidate.Title);
    }

    private static bool PlatformMatches(TruthItem truth, GroundedCandidate candidate)
    {
        var wanted = PlatformNames.Canonicalize(truth.Platform);
        return wanted is null || (PlatformNames.Canonicalize(candidate.Platform) is { } found && found == wanted);
    }

    /// <summary>A single picture's truth names its format (a VHS is not the DVD of the same film); a release that does not say its format is not held against it.</summary>
    private static bool FormatAgrees(TruthItem truth, GroundedCandidate candidate) =>
        truth.Format is null || candidate.Format is null || string.Equals(truth.Format, candidate.Format, StringComparison.OrdinalIgnoreCase);

    private static bool FullMatch(TruthItem truth, GroundedCandidate candidate) =>
        TitleMatch(truth, candidate) && PlatformMatches(truth, candidate) && FormatAgrees(truth, candidate);

    private static bool TypeMatches(TruthItem truth, GroundedCandidate candidate) =>
        truth.Format is not null
            ? string.Equals(truth.Format, candidate.Format, StringComparison.OrdinalIgnoreCase)
            : truth.MediaType is null || string.Equals(truth.MediaType, candidate.Kind, StringComparison.OrdinalIgnoreCase);
}
