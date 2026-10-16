using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Items;

namespace CollectionMaxxing.Application.Scanning.Identify;

/// <summary>
/// Picks the scoring profile (docs/02 section 6) for one item the phone read: what it is (a loose cartridge, a bare tape, a
/// spine on a shelf, a cover) decides which signals deserve the weight. Pure and table-driven, so every rule is one row below
/// and the first row that fits wins. The phone's say-so about <em>what</em> the item is only tunes the weights; it never decides
/// the match (the scorer's thresholds do).
/// </summary>
public static class ScoringProfileSelector
{
    /// <summary>From this many items in one picture, items read as spines on a shelf (a few books on a table are covers).</summary>
    public const int ShelfMinimum = 4;

    private sealed record Rule(string Name, Func<ItemCompleteness?, WorkKind?, ReleaseFormat?, int, bool> Applies, ScoringProfileKind Profile);

    // Order matters: what the item physically is beats how many items share the picture (a pile of 8 loose carts is not a shelf).
    private static readonly Rule[] Rules =
    [
        new("a bare tape: its label is all there is",
            (completeness, _, format, _) => IsTape(format) && completeness is ItemCompleteness.CartOnly or ItemCompleteness.DiscOnly,
            ScoringProfileKind.BareLabel),
        new("a bare disc: its label is all there is",
            (completeness, _, _, _) => completeness is ItemCompleteness.DiscOnly,
            ScoringProfileKind.BareLabel),
        new("a loose cartridge: the shell says the platform",
            (completeness, _, format, _) => completeness is ItemCompleteness.CartOnly
                || (IsCartridge(format) && completeness is null or ItemCompleteness.NotApplicable),
            ScoringProfileKind.LooseCartridge),
        new("one of many in a picture: a spine on a shelf",
            (_, _, _, itemsInSnap) => itemsInSnap >= ShelfMinimum,
            ScoringProfileKind.ShelfSpine),
    ];

    /// <summary>The profile for an item, given what the phone said it is and how many items were in the same picture.</summary>
    public static ScoringProfileKind Choose(ItemCompleteness? completeness, WorkKind? kind, ReleaseFormat? format, int itemsInSnap)
    {
        foreach (var rule in Rules)
        {
            if (rule.Applies(completeness, kind, format, itemsInSnap))
            {
                return rule.Profile;
            }
        }

        return ScoringProfileKind.Cover;
    }

    private static bool IsTape(ReleaseFormat? format) => format is ReleaseFormat.Vhs or ReleaseFormat.Betamax;

    private static bool IsCartridge(ReleaseFormat? format) => format is ReleaseFormat.GameCartridge or ReleaseFormat.GameCard;
}
