using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Artwork;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Application.Tests.Scanning.Artwork;

public class CloseCallTests
{
    private const double Margin = 0.15;
    private const double CoverArt = 0.20;

    private static bool Needs(ResolutionVerdict verdict, double[] scores, double artWeight = CoverArt, double margin = Margin, bool compared = false) =>
        CloseCall.NeedsArt(verdict, scores, artWeight, margin, compared);

    [Fact]
    public void An_ambiguous_item_is_a_close_call()
    {
        Needs(ResolutionVerdict.Ambiguous, [0.80, 0.78, 0.70]).ShouldBeTrue();
    }

    [Fact]
    public void An_ambiguous_item_with_one_alternative_is_still_a_close_call_because_one_picture_can_confirm_it()
    {
        Needs(ResolutionVerdict.Ambiguous, [0.75]).ShouldBeTrue();
    }

    [Fact]
    public void A_matched_item_is_a_close_call_when_its_lead_is_under_the_margin()
    {
        Needs(ResolutionVerdict.Matched, [0.95, 0.88]).ShouldBeTrue();
        Needs(ResolutionVerdict.Matched, [1.0, 0.86]).ShouldBeTrue(); // 0.14 under 0.15
    }

    [Fact]
    public void A_matched_item_is_not_a_close_call_at_or_above_the_margin()
    {
        Needs(ResolutionVerdict.Matched, [1.0, 0.85]).ShouldBeFalse(); // exactly the margin
        Needs(ResolutionVerdict.Matched, [1.0, 0.40]).ShouldBeFalse();
    }

    [Fact]
    public void A_matched_item_with_nothing_to_tell_it_from_is_not_a_close_call()
    {
        Needs(ResolutionVerdict.Matched, [1.0]).ShouldBeFalse();
    }

    [Theory]
    [InlineData(ResolutionVerdict.NotFound)]
    [InlineData(ResolutionVerdict.Pending)]
    public void Not_found_and_pending_are_never_close_calls(ResolutionVerdict verdict)
    {
        Needs(verdict, [0.70, 0.69]).ShouldBeFalse();
        Needs(verdict, []).ShouldBeFalse();
    }

    [Fact]
    public void A_profile_that_gives_artwork_no_weight_never_asks_for_it()
    {
        Needs(ResolutionVerdict.Ambiguous, [0.80, 0.78], artWeight: 0).ShouldBeFalse();
        Needs(ResolutionVerdict.Matched, [0.95, 0.90], artWeight: 0).ShouldBeFalse();
    }

    [Fact]
    public void An_item_artwork_has_already_been_compared_for_is_not_asked_again()
    {
        Needs(ResolutionVerdict.Ambiguous, [0.80, 0.78], compared: true).ShouldBeFalse();
    }

    [Fact]
    public void A_wider_margin_makes_more_matches_close_calls_and_zero_makes_only_ambiguous_ones()
    {
        Needs(ResolutionVerdict.Matched, [1.0, 0.80], margin: 0.25).ShouldBeTrue();
        Needs(ResolutionVerdict.Matched, [0.95, 0.94], margin: 0.0).ShouldBeFalse();
        Needs(ResolutionVerdict.Ambiguous, [0.8, 0.79], margin: 0.0).ShouldBeTrue();
    }

    [Theory]
    [InlineData(ScoringProfileKind.Cover, true)]
    [InlineData(ScoringProfileKind.LooseCartridge, true)]
    [InlineData(ScoringProfileKind.BareLabel, true)]
    [InlineData(ScoringProfileKind.ShelfSpine, false)]
    [InlineData(ScoringProfileKind.BarcodeProduct, false)]
    public void Only_profiles_with_an_art_weight_can_use_artwork(ScoringProfileKind profile, bool usesArt)
    {
        (CloseCall.ArtWeight(new ScoringOptions(), profile) > 0).ShouldBe(usesArt);
    }
}
