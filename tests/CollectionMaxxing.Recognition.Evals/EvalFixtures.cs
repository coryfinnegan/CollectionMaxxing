using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Scanning;
using CollectionMaxxing.Infrastructure.Evals;
using CollectionMaxxing.TestSupport.Evals;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Recognition.Evals;

/// <summary>Builders for grounded reads and for truth read back from a generated golden folder.</summary>
internal static class EvalFixtures
{
    public const string Apple = "apple_model";
    public const string Vision = "vision_pipeline";

    public static FileGoldenSet GoldenSetOf(SyntheticGolden golden, string? resultsPath = null) =>
        new(Options.Create(new EvalsOptions { Enabled = true, GoldenPath = golden.Golden, ResultsPath = resultsPath ?? golden.Results }));

    /// <summary>The truth of every picture in the folder, read the way the server reads it (files, then <see cref="TruthFiles.Parse"/>).</summary>
    public static Dictionary<string, TruthEntry> TruthsOf(SyntheticGolden golden)
    {
        var set = GoldenSetOf(golden);
        var truths = new Dictionary<string, TruthEntry>();
        foreach (var photo in set.List())
        {
            if (set.ReadTruth(photo.Id) is { } text && TruthFiles.Parse(photo.Id, photo.Set, text) is { } truth)
            {
                truths[photo.Id] = truth;
            }
        }

        return truths;
    }

    public static GroundedCandidate Release(string title, string? platform = "Nintendo 64", string kind = "game", string format = "game_cartridge", double score = 0.95) =>
        new(title, kind, format, platform, 1997, score);

    /// <summary>An item the live scorer matched to <paramref name="title"/>.</summary>
    public static GroundedItem Matched(
        string title, string? platform = "Nintendo 64", string readAs = "", string resolvedBy = "model", string? completeness = null,
        string kind = "game", string format = "game_cartridge") =>
        new(readAs.Length == 0 ? title : readAs, completeness, resolvedBy, "matched", [Release(title, platform, kind, format)]);

    /// <summary>An item the scorer could only narrow down to several releases (best first).</summary>
    public static GroundedItem Ambiguous(params GroundedCandidate[] releases) => new("ambiguous read", null, "model", "ambiguous", releases);

    public static GroundedItem NotFound(string readAs = "unknown words") => new(readAs, null, "model", "not_found", []);

    /// <summary>The item as the pipeline tiered it: T0/T1 auto-add, T2 reviewed by the person.</summary>
    public static GroundedItem Tiered(GroundedItem item, ScanTier tier) => item with { Tier = EnumText.ToText(tier) };

    public static GroundedItem T0(GroundedItem item) => Tiered(item, ScanTier.T0);

    public static GroundedItem T1(GroundedItem item) => Tiered(item, ScanTier.T1);

    public static GroundedItem T2(GroundedItem item) => Tiered(item, ScanTier.T2);

    /// <summary>The item with the box it was read in: a spine <paramref name="width"/> wide whose left edge is at <paramref name="x"/> (each 0 to 1).</summary>
    public static GroundedItem At(this GroundedItem item, double x, double width = 0.05) => item with { Box = new ReadBox(x, 0.1, width, 0.8) };

    /// <summary>A kept provisional guess (docs/16 §17): it must carry <c>IsGuess</c>, else it scores as invented.</summary>
    public static GroundedItem Kept(GroundedItem item, bool? isGuess = true) => item with { Provisional = true, IsGuess = isGuess };

    public static GroundedPhoto Photo(
        string id, string engine, IEnumerable<GroundedItem> items, long latencyMs = 1000, long? firstItemMs = 500,
        string outcome = "ok", string thermal = "nominal", string? fellBackFrom = null, int unreadable = 0,
        int capturedCount = 0, DateTimeOffset? capturedAt = null, DuplicateRescan? duplicateRescan = null) =>
        new(id, engine, fellBackFrom, outcome, [.. items], unreadable, latencyMs, firstItemMs, thermal, capturedCount, capturedAt, duplicateRescan);

    /// <summary>Duplicate re-scan evidence: owned hits, how many answered on-card, and unsolicited new items.</summary>
    public static DuplicateRescan Rescan(int owned, int answered, int unsolicited) => new(owned, answered, unsolicited);

    public static GroundedRun RunOf(params GroundedPhoto[] photos) =>
        new("run1", new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero), new EvalDevice("iPhone18,1", "26.0", "0.3.0"), "cfg-1", photos);

    public static SyntheticGolden.ShelfItem ShelfItem(string title, string mediaType = "game", string? platform = "Nintendo 64") => new(title, mediaType, platform, 1997);

    public static SliceMetrics ShelfOf(this ScoredEngines scored, string engine = Apple) => scored.Engines[engine].Shelf!;

    public static SliceMetrics SoloOf(this ScoredEngines scored, string engine = Apple) => scored.Engines[engine].Single!;
}
