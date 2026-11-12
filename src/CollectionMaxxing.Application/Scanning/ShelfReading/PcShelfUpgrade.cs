using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Application.ShelfReading;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Application.Scanning.ShelfReading;

/// <summary>
/// The one rule for how the server's own shelf read and the phone's reads of the same shelf make one shelf (ADR-033, #814): the server
/// first. The endpoint that reads a shelf snap on the server and <see cref="EvalCombination.OfServerRead"/> (the <c>server_vision</c> row)
/// both ask it, so the eval measures exactly what the app shows, and the rule can be retuned by a deploy and a re-ground with no app build.
/// </summary>
/// <remarks>
/// <para>
/// The server's reads (S) all stand, in the server's order: no print check, nothing dropped. Then each of Vision's reads (V), in its
/// order, stands only when all of these hold: it found a release; its read title has at least two words (<see cref="PrintSupport.TitleWords"/>,
/// so no stop words and no one-letter words); the family of its release's kind (a book; a film or a TV show; a game) is one some
/// server read on the photo names (its release's kind, else the kind it was read as); and it is not the same item as any server read
/// (<see cref="SameItem"/>). The reads only the phone's own model made (M) are left out: the server's model reads the same picture better,
/// and they never added a right item once it had. The caller leaves them out; this rule is never given them.
/// </para>
/// <para>
/// A server read is confirmed (two engines agree, so it may be added by itself) only when a Vision read names the same release (its
/// normalised title and year); otherwise it waits for the person. A Vision read the rule keeps is unconfirmed. Measured on the owner's
/// PC (20 shelf photos, 358 items): 315 right of 373 shown, against 287 of 400 under the rule this one replaced. Pure.
/// </para>
/// </remarks>
public static class PcShelfUpgrade
{
    /// <summary>The fewest title words a Vision read needs to stand beside the server's reads: one word is a fragment, a name, a logo.</summary>
    public const int MinVisionWords = 2;

    /// <summary>The release a read's best option names: its normalised title and year. Equal keys are the same release.</summary>
    public readonly record struct ReleaseKey(string Title, int? Year)
    {
        /// <summary>The key of a release, or null when its title normalises to nothing (there is no release to compare).</summary>
        public static ReleaseKey? Of(string? title, int? year) =>
            title is not null && TitleNormalizer.Normalize(title) is { Length: > 0 } normal ? new ReleaseKey(normal, year) : null;
    }

    /// <summary>
    /// One read of the shelf as the rule sees it: the title it was read as (the text it was searched with), the kind it was read as,
    /// and its best release (title, year and kind), each null when there is none. A read with no best release title found nothing.
    /// </summary>
    public sealed record ShelfRead(string? ReadTitle, WorkKind? ReadKind, string? BestTitle, int? BestYear, WorkKind? BestKind)
    {
        /// <summary>The release its best option names; null when it found none.</summary>
        public ReleaseKey? Release => ReleaseKey.Of(BestTitle, BestYear);
    }

    /// <summary>
    /// What the rule decided for one photo. <see cref="ServerConfirmed"/>: for each server read, whether a Vision read names its release
    /// (it may then be added by itself). <see cref="VisionKept"/>: for each Vision read, whether it stands beside the server's reads.
    /// Every server read stands; no Vision read the rule keeps is confirmed.
    /// </summary>
    public sealed record Decision(IReadOnlyList<bool> ServerConfirmed, IReadOnlyList<bool> VisionKept);

    /// <summary>Whether a read the server's reader made can be used at all: it answered, or it was cut short with what it had read. Otherwise nothing changes.</summary>
    public static bool IsUsable(ShelfReadStatus status) => status is ShelfReadStatus.Ok or ShelfReadStatus.CutShort;

    /// <summary>The rule over one photo's reads: <paramref name="server"/> (the server's, skipped reads left out) and <paramref name="vision"/> (Vision's).</summary>
    public static Decision Decide(IReadOnlyList<ShelfRead> server, IReadOnlyList<ShelfRead> vision)
    {
        var families = server.Select(read => FamilyOf(read.BestKind ?? read.ReadKind)).OfType<KindFamily>().ToHashSet();
        var confirmed = server.Select(read => read.Release is { } key && vision.Any(other => other.Release == key)).ToList();
        var kept = vision.Select(read =>
                read.BestTitle is not null
                && DistinctWords(read.ReadTitle).Count >= MinVisionWords
                && FamilyOf(read.BestKind) is { } family && families.Contains(family)
                && !server.Any(serverRead => SameItem(serverRead, read)))
            .ToList();
        return new Decision(confirmed, kept);
    }

    /// <summary>
    /// Whether a Vision read is the same item as a server read, with no place on the shelf to go on: their best releases' titles are
    /// the same once normalised (<see cref="TitleNormalizer.Normalize"/>, whatever the year), or at least two thirds of the Vision read's
    /// title words are among the server read's words (its read title and its best release's title).
    /// </summary>
    public static bool SameItem(ShelfRead server, ShelfRead vision)
    {
        if (server.BestTitle is { } serverTitle && vision.BestTitle is { } visionTitle
            && TitleNormalizer.Normalize(serverTitle) is { Length: > 0 } normal && normal == TitleNormalizer.Normalize(visionTitle))
        {
            return true;
        }

        var visionWords = DistinctWords(vision.ReadTitle);
        var serverWords = DistinctWords(server.ReadTitle);
        serverWords.UnionWith(PrintSupport.TitleWords(server.BestTitle));
        if (visionWords.Count == 0 || serverWords.Count == 0)
        {
            return false;
        }

        return visionWords.Count(serverWords.Contains) * 3 >= visionWords.Count * 2;
    }

    private static HashSet<string> DistinctWords(string? title) => [.. PrintSupport.TitleWords(title)];

    /// <summary>The kinds a shelf mixes as one: a book; a film or a TV show (both sit on the film shelf); a game; anything else.</summary>
    private enum KindFamily
    {
        Book,
        Screen,
        Game,
        Other,
    }

    private static KindFamily? FamilyOf(WorkKind? kind) => kind switch
    {
        WorkKind.Book => KindFamily.Book,
        WorkKind.Film or WorkKind.Tv => KindFamily.Screen,
        WorkKind.Game => KindFamily.Game,
        WorkKind.Other => KindFamily.Other,
        _ => null,
    };
}

/// <summary>
/// The server's skip of a phone row its own shelf read left out (<see cref="PcShelfUpgrade"/>): a Vision read the rule does not keep, or
/// a read only the phone's model made. The trace step <see cref="StepName"/> says so (<see cref="SkipReason.ServerDidNotSee"/>).
/// </summary>
public static class ServerDidNotSee
{
    public const string StepName = "server_read";
}
