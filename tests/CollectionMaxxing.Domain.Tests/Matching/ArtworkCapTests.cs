using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Domain.Tests.Matching;

/// <summary>Artwork distances refine a score; they never lift a release past the caps (SCAN-2b).</summary>
public class ArtworkCapTests
{
    // The platform barely counts, so a perfect picture would carry the score past the cap if the cap did not hold.
    private static readonly ScoringProfile LightPlatform = new(
        "LightPlatform", new ScoringWeights(0.30, 0.05, 0.02, 0.03, 0, 0, 0.60), new ScoringThresholds());

    private static CandidateRelease Release(string key, string title, string? platform) =>
        new() { Key = key, Title = title, Kind = WorkKind.Game, Format = ReleaseFormat.GameCartridge, Platform = platform, Year = 1999 };

    [Fact]
    public void A_perfect_artwork_match_cannot_lift_a_wrong_platform_past_the_cap()
    {
        var read = new ReadItem
        {
            Title = "Wave Race 64",
            Kind = WorkKind.Game,
            Format = ReleaseFormat.GameCartridge,
            Platform = "N64",
            Year = 1999,
            ArtDistances = new Dictionary<string, double> { ["wrong"] = 0.0, ["other"] = 1.4 },
        };

        var result = MatchScorer.Evaluate(
            read, [Release("wrong", "Wave Race 64", "GameCube"), Release("other", "Wave Race 64", "GameCube")], LightPlatform);

        result.Ranked[0].Score.ShouldBe(LightPlatform.Thresholds.PlatformMismatchCap);
        result.Ranked[0].HasKnownConflict.ShouldBeTrue();
        result.Outcome.ShouldNotBe(MatchOutcome.Matched);
    }

    [Fact]
    public void A_perfect_artwork_match_cannot_lift_a_different_title_year_past_the_cap()
    {
        var read = new ReadItem
        {
            Title = "Madden NFL 2005",
            Kind = WorkKind.Game,
            Format = ReleaseFormat.GameCartridge,
            ArtDistances = new Dictionary<string, double> { ["old"] = 0.0, ["older"] = 1.4 },
        };

        var result = MatchScorer.Evaluate(
            read, [Release("old", "Madden NFL 2004", null), Release("older", "Madden NFL 2003", null)], LightPlatform);

        result.Ranked[0].Score.ShouldBeLessThanOrEqualTo(LightPlatform.Thresholds.TitleYearMismatchCap);
        result.Outcome.ShouldNotBe(MatchOutcome.Matched);
    }

    [Fact]
    public void A_distance_for_a_release_that_is_not_in_the_set_changes_nothing()
    {
        var withStranger = new ReadItem
        {
            Title = "Wave Race 64",
            Kind = WorkKind.Game,
            ArtDistances = new Dictionary<string, double> { ["stranger"] = 0.0 },
        };
        var without = new ReadItem { Title = "Wave Race 64", Kind = WorkKind.Game };
        CandidateRelease[] pool = [Release("a", "Wave Race 64", null), Release("b", "Wave Race 64 Special", null)];

        var a = MatchScorer.Evaluate(withStranger, pool, LightPlatform);
        var b = MatchScorer.Evaluate(without, pool, LightPlatform);

        a.Ranked.Select(r => r.Release.Key).ShouldBe(b.Ranked.Select(r => r.Release.Key));
        a.Outcome.ShouldBe(b.Outcome);
    }
}
