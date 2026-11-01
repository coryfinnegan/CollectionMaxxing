using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Scanning;

namespace CollectionMaxxing.Application.Evals;

/// <summary>
/// One thing that went wrong in a run, for the report that stays on the PC (it has titles, so it is never committed). <c>CoverCue</c> is what
/// the phone's cover cue saw on the picture (null when it sent none): it is what explains a wrong format, so the report prints it beside the miss.
/// </summary>
public sealed record EvalMiss(string PhotoId, string Set, string Engine, string Kind, string? Truth, string? Read, string? CoverCue = null);

/// <summary>What scoring a run gives: per-engine numbers and the misses behind them.</summary>
public sealed record ScoredEngines(IReadOnlyDictionary<string, EngineSummary> Engines, IReadOnlyList<EvalMiss> Misses);

/// <summary>
/// Scores a grounded run against the truth files (docs/09, "AI evals"). Pure: no files, no database, no clock. A photo is scored
/// per engine; within a photo, items are matched to truth items greedily in reading order, a truth item being claimed once.
/// </summary>
/// <remarks>
/// A read item is <b>correct</b> when its best release has the truth item's title (normalised the way the live scorer does) and,
/// when the truth names a platform, the same platform and, on a single picture, the same format and a kind of work that format can be (a VHS is not the novel of
/// the same name); on a shelf, a kind of work the truth item's media type can be (a shelf's film is not the book of the same title). <b>Top-3</b> also accepts the second and third releases. An
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
    private const string Matched = "matched";

    /// <summary>How alike a read must be to a missed truth title before the report names it as the closest read.</summary>
    private const double CloseRead = 0.5;

    private static readonly string Tier0 = EnumText.ToText(ScanTier.T0);
    private static readonly string Tier1 = EnumText.ToText(ScanTier.T1);
    private static readonly string Tier2 = EnumText.ToText(ScanTier.T2);
    private static readonly string UnknownFormat = EnumText.ToText(ReleaseFormat.Unknown);

    private static readonly HashSet<string> FilmKinds = new(StringComparer.OrdinalIgnoreCase) { EnumText.ToText(WorkKind.Film), EnumText.ToText(WorkKind.Tv) };
    private static readonly HashSet<string> GameKinds = new(StringComparer.OrdinalIgnoreCase) { EnumText.ToText(WorkKind.Game) };
    private static readonly HashSet<string> BookKinds = new(StringComparer.OrdinalIgnoreCase) { EnumText.ToText(WorkKind.Book) };

    /// <summary>The kinds of work a release in each physical format can be. A format not listed (<c>other</c>, <c>unknown</c>, anything new) fits any kind.</summary>
    private static readonly Dictionary<string, HashSet<string>> KindsByFormat = new(StringComparer.OrdinalIgnoreCase)
    {
        [EnumText.ToText(ReleaseFormat.Vhs)] = FilmKinds,
        [EnumText.ToText(ReleaseFormat.Betamax)] = FilmKinds,
        [EnumText.ToText(ReleaseFormat.Laserdisc)] = FilmKinds,
        [EnumText.ToText(ReleaseFormat.Dvd)] = FilmKinds,
        [EnumText.ToText(ReleaseFormat.Bluray)] = FilmKinds,
        [EnumText.ToText(ReleaseFormat.UhdBluray)] = FilmKinds,
        [EnumText.ToText(ReleaseFormat.GameCartridge)] = GameKinds,
        [EnumText.ToText(ReleaseFormat.GameDisc)] = GameKinds,
        [EnumText.ToText(ReleaseFormat.GameCard)] = GameKinds,
        [EnumText.ToText(ReleaseFormat.Hardcover)] = BookKinds,
        [EnumText.ToText(ReleaseFormat.Paperback)] = BookKinds,
        [EnumText.ToText(ReleaseFormat.MassMarket)] = BookKinds,
        [EnumText.ToText(ReleaseFormat.BoardBook)] = BookKinds,
        [EnumText.ToText(ReleaseFormat.SpiralBound)] = BookKinds,
    };

    /// <summary>The kinds of work a shelf truth item of each media type can be. <c>other</c> and any media type not listed fit any kind.</summary>
    private static readonly Dictionary<string, HashSet<string>> KindsByMediaType = new(StringComparer.OrdinalIgnoreCase)
    {
        [EnumText.ToText(WorkKind.Book)] = BookKinds,
        [EnumText.ToText(WorkKind.Film)] = FilmKinds,
        [EnumText.ToText(WorkKind.Tv)] = FilmKinds,
        [EnumText.ToText(WorkKind.Game)] = GameKinds,
    };

    /// <summary>
    /// The model's time budget in milliseconds (the default of <c>engine.modelTimeoutMs</c>). A timed-out photo counts in the latency
    /// percentiles at its measured time, or at this budget if that is larger: the phone gave up at the budget, so it waited at least that long.
    /// </summary>
    public const long ModelTimeoutBudgetMs = 8000;

    private sealed record TruthItem(string Title, string? Platform, string? MediaType, string? Format, string Kind, IReadOnlyList<string>? AlsoTitled = null);

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
        public int T0Auto;
        public int T1Auto;
        public int T2Review;
        public int ProvisionalItems;
        public int AutoAdds;
        public int CorrectAutoAdds;
        public int TouchTruth;
        public int UnreconciledPhotos;
        public int ExcessUnreadable;
        public int DuplicateOwned;
        public int DuplicateAnswered;
        public int DuplicateUnsolicited;
        public bool HasDuplicateEvidence;
        public DateTimeOffset? FirstCapture;
        public DateTimeOffset? FirstReady;
        public DateTimeOffset? LastReady;
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

        // The combination of the model and Vision (report only) is scored like an engine the phone read, from the same rows.
        var everything = photos.Concat(EvalCombination.Of(photos, truths));
        foreach (var byEngine in everything.Where(p => truths.ContainsKey(p.PhotoId)).GroupBy(p => p.Engine))
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
        // Unreadable hidden beyond what the truth allows are missed readable items: the per-truth rates divide by truth plus them.
        var readable = tally.TruthItems + tally.ExcessUnreadable;
        return new SliceMetrics(
            tally.Photos,
            tally.TruthItems,
            tally.Predicted,
            tally.Correct,
            tally.Top1Hits,
            tally.Top3Hits,
            RecallPct: Pct(tally.Top1Hits, readable),
            PrecisionPct: Pct(tally.Correct, tally.Predicted),
            Top1Pct: Pct(tally.Top1Hits, readable),
            Top3Pct: Pct(tally.Top3Hits, readable),
            TitleAccuracyPct: Pct(tally.TitleHits, readable),
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
            shots,
            tally.T0Auto,
            tally.T1Auto,
            tally.T2Review,
            tally.ProvisionalItems,
            tally.AutoAdds,
            tally.CorrectAutoAdds,
            ReviewRatePct: Pct(tally.TouchTruth, tally.TruthItems),
            AutoAddPrecisionPct: Pct(tally.CorrectAutoAdds, tally.AutoAdds),
            DuplicateClean: tally.HasDuplicateEvidence
                ? tally.DuplicateUnsolicited == 0 && tally.DuplicateOwned > 0 && tally.DuplicateAnswered == tally.DuplicateOwned
                : null,
            TimeToFirstResultsMs: SpanMs(tally.FirstCapture, tally.FirstReady),
            TotalTimeToTrustworthyMs: SpanMs(tally.FirstCapture, tally.LastReady),
            tally.UnreconciledPhotos,
            tally.ExcessUnreadable,
            tally.TitleHits,
            tally.PlatformChecks,
            tally.CompletenessChecks);
    }

    private static long? SpanMs(DateTimeOffset? from, DateTimeOffset? to) =>
        from is { } start && to is { } end ? (long)Math.Round((end - start).TotalMilliseconds, MidpointRounding.AwayFromZero) : null;

    /// <summary>An auto-commit: tiered T0 or T1 by the pipeline. A provisional is kept for a human, never auto-added, whatever else it claims.</summary>
    private static bool IsAutoAdd(GroundedItem item) =>
        !item.Provisional && (TierOf(item) == Tier0 || TierOf(item) == Tier1);

    /// <summary>
    /// The item's tier with the server's first gate applied. The phone claims T1 for a confident read, but the server commits
    /// only what it matched (<c>CandidateResolver</c>), so a read it narrowed down or found nothing for counts as T2. The
    /// server's other gates (owned copies, series volumes, the artwork re-score) are not modelled here. The second gate is the scanner's:
    /// a read only the picture model made, where Vision found nothing (<see cref="GroundedItem.Unconfirmed"/>), is reviewed, never
    /// committed by itself, so it is T2 at most however exactly it matched.
    /// </summary>
    private static string? TierOf(GroundedItem item) =>
        (item.Tier == Tier0 || item.Tier == Tier1) && (item.Verdict != Matched || item.Unconfirmed) ? Tier2 : item.Tier;

    private static List<TruthItem> TruthItemsOf(TruthEntry truth) => truth.Set == GoldenSets.Shelf
        ? [.. truth.Shelf!.Items.Select(i => new TruthItem(i.Title, i.Platform, i.MediaType.ToLowerInvariant(), null, EvalKinds.Key(i.MediaType), i.AlsoTitled))]
        : [new TruthItem(truth.Single!.Title, truth.Single.Platform, null, truth.Single.Format.ToLowerInvariant(), truth.Single.Kind, truth.Single.AlsoTitled)];

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

        foreach (var item in items)
        {
            if (item.Provisional)
            {
                tally.ProvisionalItems++;
            }
            else if (TierOf(item) == Tier0)
            {
                tally.T0Auto++;
            }
            else if (TierOf(item) == Tier1)
            {
                tally.T1Auto++;
            }
            else if (TierOf(item) == Tier2)
            {
                tally.T2Review++;
            }

            tally.AutoAdds += IsAutoAdd(item) ? 1 : 0;
        }

        if (photo.CapturedAt is { } captured)
        {
            tally.FirstCapture = tally.FirstCapture is { } seen && seen <= captured ? seen : captured;
        }

        if (photo.DuplicateRescan is { } rescan)
        {
            tally.HasDuplicateEvidence = true;
            tally.DuplicateOwned += rescan.OwnedHits;
            tally.DuplicateAnswered += rescan.AnsweredOnCard;
            tally.DuplicateUnsolicited += rescan.UnsolicitedAdds;
        }

        // Unreadable past what the truth allows hides readable items (a single picture allows none).
        var allowed = truth.Set == GoldenSets.Shelf ? truth.Shelf!.Unreadable : 0;
        tally.ExcessUnreadable += Math.Max(0, photo.UnreadableCount - allowed);

        if (photo.Outcome == Ok && photo.CapturedCount > 0)
        {
            // Reported unreadable + resolved + provisional must equal what was captured; reads sharing a segment count once.
            var segments = photo.UnreadableCount
                + items.Count(i => i.SegmentId is null)
                + items.Where(i => i.SegmentId is not null).Select(i => i.SegmentId!).Distinct(StringComparer.Ordinal).Count();
            var gap = Math.Abs(photo.CapturedCount - segments);
            if (gap > 0)
            {
                tally.UnreconciledPhotos++;
                tally.Invented += gap;
                tally.Predicted += gap;
                misses.Add(new EvalMiss(
                    photo.PhotoId, truth.Set, photo.Engine, "unreconciled", null,
                    $"{gap} of {photo.CapturedCount} captured segments unaccounted for"));
            }
        }

        // The items that may claim a truth item: all of them on a shelf, the first one on a single picture.
        var claimers = truth.Set == GoldenSets.Single ? items.Take(1).ToList() : items.ToList();
        var claimed = new bool[truthItems.Count];
        var pairOfItem = new Dictionary<int, (int Truth, bool Full)>();
        for (var i = 0; i < claimers.Count; i++)
        {
            if (claimers[i].Best is not { } best || IsUnguessedProvisional(claimers[i]))
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
                tally.CorrectAutoAdds += IsAutoAdd(claimers[itemIndex]) ? 1 : 0;
                tally.ResolvedBy[claimers[itemIndex].ResolvedBy] = tally.ResolvedBy.GetValueOrDefault(claimers[itemIndex].ResolvedBy) + 1;
            }
        }

        // Top-3: what the top-1 pass settled, plus truth items that one of the other two releases of an unsettled item names.
        for (var i = 0; i < claimers.Count; i++)
        {
            if (IsUnguessedProvisional(claimers[i]))
            {
                continue;
            }

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
        // A truth item needs no touch only when a correct auto-add claimed it; everything else (a miss, a T2 row, a provisional, a wrong auto-add) is a touch.
        var clean = new HashSet<int>();
        foreach (var (itemIndex, (truthIndex, full)) in pairOfItem)
        {
            if (full && IsAutoAdd(claimers[itemIndex]))
            {
                clean.Add(truthIndex);
            }
        }

        for (var t = 0; t < truthItems.Count; t++)
        {
            var kind = truthItems[t].Kind;
            var current = tally.Kinds.GetValueOrDefault(kind);
            tally.Kinds[kind] = (current.Items + 1, current.Top1 + (top1[t] ? 1 : 0), current.Top3 + (top3[t] ? 1 : 0));
            var shotCurrent = tally.Shots.GetValueOrDefault(shot);
            tally.Shots[shot] = (shotCurrent.Items + 1, shotCurrent.Top1 + (top1[t] ? 1 : 0), shotCurrent.Top3 + (top3[t] ? 1 : 0));
            tally.Top1Hits += top1[t] ? 1 : 0;
            tally.Top3Hits += top3[t] ? 1 : 0;
            tally.TouchTruth += clean.Contains(t) ? 0 : 1;
            if (!top1[t])
            {
                var others = pairOfItem.Where(p => p.Value.Full).Select(p => (object)claimers[p.Key]).ToHashSet(ReferenceEqualityComparer.Instance);
                misses.Add(new EvalMiss(
                    photo.PhotoId, truth.Set, photo.Engine, photo.Outcome == Ok ? "miss" : photo.Outcome, truthItems[t].Title,
                    items.Count == 0 ? ReadSummary(photo, items, truthItems[t].Title) : ReadSummary(photo, [.. items.Where(i => !others.Contains(i))], truthItems[t].Title, readSomething: true),
                    EvalCoverCue.Clean(photo.CoverCue)));
            }
        }

        if (photo.Outcome == Ok && photo.CapturedAt is { } stamped && top1.Any(t => t))
        {
            var ready = stamped.AddMilliseconds(photo.LatencyMs);
            tally.FirstReady = tally.FirstReady is { } first && first <= ready ? first : ready;
            tally.LastReady = tally.LastReady is { } last && last >= ready ? last : ready;
        }

        foreach (var item in items)
        {
            if (IsUnguessedProvisional(item))
            {
                tally.Invented++;
                misses.Add(new EvalMiss(photo.PhotoId, truth.Set, photo.Engine, "invented", null, item.Best?.Title ?? item.ReadTitle, EvalCoverCue.Clean(photo.CoverCue)));
            }
            else if (item.Verdict == Matched && item.Best is { } best && !truthItems.Any(t => TitleMatch(t, best)))
            {
                tally.Invented++;
                misses.Add(new EvalMiss(photo.PhotoId, truth.Set, photo.Engine, "invented", null, best.Title, EvalCoverCue.Clean(photo.CoverCue)));
            }
        }
    }

    /// <summary>A provisional with a guessed title must carry <c>IsGuess</c>; without it the guess scores as invented.</summary>
    private static bool IsUnguessedProvisional(GroundedItem item) => item.Provisional && item.IsGuess != true;

    /// <summary>
    /// The read closest to a missed truth title, as "best release [what was read, verdict]", so the report says whether the engine
    /// never read the item or read it and the catalogue let it down. Reads that are another truth item's right answer are left out.
    /// </summary>
    private static string ReadSummary(GroundedPhoto photo, IReadOnlyList<GroundedItem> items, string truthTitle, bool readSomething = false)
    {
        if (photo.Outcome != Ok)
        {
            return "(" + photo.Outcome + ")";
        }

        if (items.Count == 0)
        {
            return readSomething ? "(nothing close)" : "(nothing read)";
        }

        var closest = items
            .Select(i => (Item: i, Similarity: Math.Max(TitleNormalizer.Similarity(truthTitle, i.ReadTitle), TitleNormalizer.Similarity(truthTitle, i.Best?.Title))))
            .MaxBy(c => c.Similarity);
        if (closest.Similarity < CloseRead)
        {
            return "(nothing close)";
        }

        var read = $"[{closest.Item.ReadTitle}, {closest.Item.Verdict}]";
        return closest.Item.Best is { } best ? $"{best.Title} {read}" : read;
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

    /// <summary>
    /// The candidate's title is the truth title or one of the full titles the truth lists as also naming the item
    /// (compared after normalising). Nothing looser: a real subtitle that is not listed is a different title. The one
    /// thing normalising sets aside is a subtitle that is only a genre label, which is not a subtitle the cover or the
    /// catalogue carries (see <see cref="GenreSubtitle"/>): a candidate stored as "Harbor Light: A Novel" counts for a
    /// truth of "Harbor Light". A rate measured before that was so counts those as misses, so comparing with such a
    /// baseline overstates the gain by however many items were stored with a label.
    /// </summary>
    private static bool TitleMatch(TruthItem truth, GroundedCandidate candidate)
    {
        var found = TitleNormalizer.Normalize(candidate.Title);
        return found.Length > 0 && (found == TitleNormalizer.Normalize(truth.Title) || (truth.AlsoTitled?.Any(t => found == TitleNormalizer.Normalize(t)) ?? false));
    }

    private static bool PlatformMatches(TruthItem truth, GroundedCandidate candidate)
    {
        var wanted = PlatformNames.Canonicalize(truth.Platform);
        return wanted is null || (PlatformNames.Canonicalize(candidate.Platform) is { } found && found == wanted);
    }

    /// <summary>A single picture's truth names its format (a VHS is not the DVD of the same film); a release that does not say its format (none, or <c>unknown</c>) is not held against it.</summary>
    private static bool FormatAgrees(TruthItem truth, GroundedCandidate candidate) =>
        truth.Format is null || candidate.Format is null
        || string.Equals(candidate.Format, UnknownFormat, StringComparison.OrdinalIgnoreCase)
        || string.Equals(truth.Format, candidate.Format, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What kind of item the truth says it is, and the candidate has to be. A single picture's format says it: a VHS is a film (or a
    /// TV show), never the novel with the same title, and a game cartridge is never a film. A shelf truth item says it with its media
    /// type: book fits book, game fits game, film and tv fit each other (a shelf label cannot always tell a TV tape from a film),
    /// and <c>other</c> fits anything. A release with no kind is not held against it; a format or media type not listed fits every kind.
    /// </summary>
    private static bool KindFits(TruthItem truth, GroundedCandidate candidate)
    {
        if (candidate.Kind is null)
        {
            return true;
        }

        if (truth.Format is not null)
        {
            return !KindsByFormat.TryGetValue(truth.Format, out var byFormat) || byFormat.Contains(candidate.Kind);
        }

        return truth.MediaType is null || !KindsByMediaType.TryGetValue(truth.MediaType, out var byMediaType) || byMediaType.Contains(candidate.Kind);
    }

    private static bool FullMatch(TruthItem truth, GroundedCandidate candidate) =>
        TitleMatch(truth, candidate) && PlatformMatches(truth, candidate) && FormatAgrees(truth, candidate) && KindFits(truth, candidate);

    private static bool TypeMatches(TruthItem truth, GroundedCandidate candidate) =>
        truth.Format is not null
            ? string.Equals(truth.Format, candidate.Format, StringComparison.OrdinalIgnoreCase)
            : truth.MediaType is null || string.Equals(truth.MediaType, candidate.Kind, StringComparison.OrdinalIgnoreCase);
}
