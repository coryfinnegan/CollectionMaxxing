using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Scanning;

namespace CollectionMaxxing.Application.Scanning.Artwork;

/// <summary>
/// The close-call rule (docs/02, "Artwork comparison with Apple feature prints"): when does the server ask the phone to compare
/// artwork? Pure, so the rule is one place and easy to test.
/// </summary>
/// <remarks>
/// A close call is an item the text could not settle: it is Ambiguous, or it is Matched by a lead smaller than the margin
/// (<see cref="ArtScoringOptions.CloseCallMargin"/>). It also needs something for artwork to do: a profile that gives the art
/// term weight (a spine on a shelf has no cover to compare), a shot that shows a cover (a shelf segment shows spines, so the
/// phone would compare spine pixels against cover art — #519), and at least one other release to tell it from, unless
/// the verdict is Ambiguous (then even one picture can confirm or refute the lone alternative). A barcode never asks: an exact
/// code is not settled by a picture. Nothing is asked twice: once artwork has been reported, the item is no longer a close call.
/// </remarks>
public static class CloseCall
{
    /// <param name="verdict">What the text-based scoring concluded.</param>
    /// <param name="alternativeScores">The scores of the item's alternatives, best first.</param>
    /// <param name="artWeight">The art weight of the scoring profile the item was scored with.</param>
    /// <param name="margin">The lead under which a Matched item is still a close call.</param>
    /// <param name="alreadyCompared">The phone has already reported distances for this item.</param>
    /// <param name="shot">What the phone's framing says the shot is. Only an explicit shelf segment is text-only; a shot the
    /// phone did not classify keeps today's artwork behavior.</param>
    public static bool NeedsArt(
        ResolutionVerdict verdict, IReadOnlyList<double> alternativeScores, double artWeight, double margin, bool alreadyCompared,
        ShotKind shot = ShotKind.Unknown)
    {
        ArgumentNullException.ThrowIfNull(alternativeScores);

        if (shot == ShotKind.ShelfSegment)
        {
            return false;
        }

        if (alreadyCompared || !double.IsFinite(artWeight) || artWeight <= 0 || alternativeScores.Count == 0)
        {
            return false;
        }

        return verdict switch
        {
            ResolutionVerdict.Ambiguous => true,
            ResolutionVerdict.Matched => alternativeScores.Count >= 2 && Math.Round(alternativeScores[0] - alternativeScores[1], 9) < margin,
            _ => false,
        };
    }

    /// <summary>The art weight of a profile, for <see cref="NeedsArt"/>.</summary>
    public static double ArtWeight(ScoringOptions options, ScoringProfileKind profile)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Profile(profile).Weights.Art;
    }
}
