using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Identifiers;

namespace CollectionMaxxing.Domain.Tests.Matching;

// Locked acceptance tests for CAT-1b (issue #48): a wrong release must never come back Matched. Do not weaken, skip or
// delete. They sit beside, and never replace, the CAT-1 tests in MatchScorerTests and OutcomeBoundaryTests.
public class WrongMatchHoleTests
{
    private static readonly ScoringOptions Options = new();

    private static ScoringProfile Profile(ScoringProfileKind kind) => Options.Profile(kind);

    // ---- 1. a year inside a title is part of its identity ------------------------------------

    private static CandidateRelease Madden(int editionYear, int releasedIn) =>
        Release($"madden-{editionYear}", $"Madden NFL {editionYear}", WorkKind.Game, ReleaseFormat.GameDisc, "PlayStation 2", releasedIn);

    private static readonly ReadItem Madden2005 = new() { Title = "Madden NFL 2005", Platform = "PS2", Format = ReleaseFormat.GameDisc };

    [Fact]
    public void Madden_NFL_2005_ranks_the_2005_edition_first_and_matches_it()
    {
        var result = MatchScorer.Evaluate(
            Madden2005, [Madden(2004, 2003), Madden(2006, 2005), Madden(2005, 2004)], Profile(ScoringProfileKind.BarcodeProduct));

        result.Ranked[0].Release.Key.ShouldBe("madden-2005");
        result.Outcome.ShouldBe(MatchOutcome.Matched);
    }

    [Fact]
    public void Madden_NFL_2005_read_against_only_the_2004_edition_is_not_Matched()
    {
        foreach (var kind in Enum.GetValues<ScoringProfileKind>())
        {
            MatchScorer.Evaluate(Madden2005, [Madden(2004, 2003)], Profile(kind))
                .Outcome.ShouldNotBe(MatchOutcome.Matched, kind.ToString());
        }
    }

    [Fact]
    public void Madden_NFL_2005_read_against_only_the_2006_edition_is_not_Matched()
    {
        MatchScorer.Evaluate(Madden2005, [Madden(2006, 2005)], Profile(ScoringProfileKind.BarcodeProduct))
            .Outcome.ShouldNotBe(MatchOutcome.Matched);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Different_years_in_the_two_titles_can_never_score_as_a_sure_thing(ScoringProfileKind kind)
    {
        var profile = Profile(kind);
        var read = new ReadItem
        {
            Title = "Madden NFL 2005",
            Platform = "PS2",
            Format = ReleaseFormat.GameDisc,
            Year = 2004,
            Creator = "EA Sports",
            Edition = "Greatest Hits",
            ArtDistances = new Dictionary<string, double> { ["r"] = 0.0 },
        };
        var wrongEdition = Release(
            "r", "Madden NFL 2004", WorkKind.Game, ReleaseFormat.GameDisc, "PlayStation 2", 2004, ["EA Sports"], "Greatest Hits");

        MatchScorer.Score(read, wrongEdition, profile).ShouldBeLessThan(profile.Thresholds.MatchedScore);
        MatchScorer.Evaluate(read, [wrongEdition], profile).Outcome.ShouldNotBe(MatchOutcome.Matched);
    }

    [Fact]
    public void A_year_in_the_title_never_fills_in_for_the_release_year()
    {
        var barcode = Profile(ScoringProfileKind.BarcodeProduct);
        var sameTitle = Madden(2005, 2004);

        // With no year read, the release year plays no part in the score at all, and the year in the title is not a
        // year read.
        var released2004 = MatchScorer.Score(Madden2005, sameTitle, barcode);
        var released2005 = MatchScorer.Score(Madden2005, sameTitle with { Year = 2005 }, barcode);
        var releasedUnknown = MatchScorer.Score(Madden2005, sameTitle with { Year = null }, barcode);

        released2005.ShouldBe(released2004);
        releasedUnknown.ShouldBe(released2004);
    }

    [Fact]
    public void A_printed_release_year_is_still_compared_with_the_release_year()
    {
        var read = Madden2005 with { Year = 2004 };
        var barcode = Profile(ScoringProfileKind.BarcodeProduct);

        MatchScorer.Score(read, Madden(2005, 2004), barcode).ShouldBeGreaterThan(MatchScorer.Score(read, Madden(2005, 2009), barcode));
    }

    [Fact]
    public void A_year_in_only_one_of_the_titles_is_still_forgiven_as_before()
    {
        // CAT-1 behaviour (locked in MatchScorerTests): "Alien 1979" finds "Alien". Only two years that disagree count.
        var read = new ReadItem { Title = "Alien 1979", Format = ReleaseFormat.Dvd };
        var alien = Release("alien", "Alien", WorkKind.Film, ReleaseFormat.Dvd, year: 1979);

        MatchScorer.Evaluate(read, [alien], Profile(ScoringProfileKind.BarcodeProduct)).Outcome.ShouldBe(MatchOutcome.Matched);
    }

    [Fact]
    public void The_same_year_in_both_titles_matches()
    {
        var read = new ReadItem { Title = "Alien 1979", Format = ReleaseFormat.Dvd };
        var alien = Release("alien", "Alien (1979)", WorkKind.Film, ReleaseFormat.Dvd, year: 1979);

        MatchScorer.Evaluate(read, [alien], Profile(ScoringProfileKind.BarcodeProduct)).Outcome.ShouldBe(MatchOutcome.Matched);
    }

    // A two digit "year" is just a sequel number to the normaliser, and a number that differs is already penalised, so
    // FIFA 13/14/15 need no special rule. These pin that.
    private static CandidateRelease Fifa(int number) =>
        Release($"fifa-{number}", $"FIFA {number}", WorkKind.Game, ReleaseFormat.GameDisc, "PlayStation 3", 2000 + number - 1);

    private static readonly ReadItem Fifa14 = new() { Title = "FIFA 14", Platform = "PS3", Format = ReleaseFormat.GameDisc };

    [Fact]
    public void FIFA_14_ranks_the_14_edition_first_and_matches_it()
    {
        var result = MatchScorer.Evaluate(Fifa14, [Fifa(13), Fifa(15), Fifa(14)], Profile(ScoringProfileKind.BarcodeProduct));

        result.Ranked[0].Release.Key.ShouldBe("fifa-14");
        result.Outcome.ShouldBe(MatchOutcome.Matched);
    }

    [Fact]
    public void FIFA_14_read_against_only_the_13_edition_is_not_Matched()
    {
        MatchScorer.Evaluate(Fifa14, [Fifa(13)], Profile(ScoringProfileKind.BarcodeProduct)).Outcome.ShouldNotBe(MatchOutcome.Matched);
    }

    // ---- 2. a candidate with no art distance is neutral, not exempt --------------------------

    [Fact]
    public void An_uncompared_twin_of_a_release_with_a_weak_art_distance_is_not_Matched()
    {
        var read = new ReadItem
        {
            Title = "Alien",
            Format = ReleaseFormat.Dvd,
            Year = 1979,
            ArtDistances = new Dictionary<string, double> { ["right"] = 1.2 },
        };
        var right = Release("right", "Alien", WorkKind.Film, ReleaseFormat.Dvd, year: 1979);
        var twin = Release("twin", "Alien", WorkKind.Film, ReleaseFormat.Dvd, year: 1979);

        var result = MatchScorer.Evaluate(read, [twin, right], Profile(ScoringProfileKind.Cover));

        result.Ranked.Single(r => r.Release.Key == "twin").Score.ShouldBeLessThan(Options.Thresholds.MatchedScore);
        result.Outcome.ShouldNotBe(MatchOutcome.Matched);
    }

    [Fact]
    public void A_candidate_without_a_distance_scores_the_same_as_one_at_the_neutral_distance_of_1_0()
    {
        // d = 1 is cosine 0.5, the neutral visual score.
        var read = new ReadItem
        {
            Title = "Alien",
            Format = ReleaseFormat.Dvd,
            Year = 1979,
            ArtDistances = new Dictionary<string, double> { ["compared"] = 1.0 },
        };
        var compared = Release("compared", "Alien", WorkKind.Film, ReleaseFormat.Dvd, year: 1979);
        var uncompared = Release("uncompared", "Alien", WorkKind.Film, ReleaseFormat.Dvd, year: 1979);

        var result = MatchScorer.Evaluate(read, [compared, uncompared], Profile(ScoringProfileKind.Cover));

        result.Ranked[0].Score.ShouldBe(result.Ranked[1].Score);
        result.Ranked[0].Score.ShouldBeLessThan(1.0);
    }

    [Fact]
    public void A_perfect_picture_still_beats_the_candidate_that_was_not_compared()
    {
        var read = new ReadItem
        {
            Title = "Alien",
            Format = ReleaseFormat.Dvd,
            Year = 1979,
            ArtDistances = new Dictionary<string, double> { ["seen"] = 0.0 },
        };
        var seen = Release("seen", "Alien", WorkKind.Film, ReleaseFormat.Dvd, year: 1979);
        var unseen = Release("unseen", "Alien", WorkKind.Film, ReleaseFormat.Dvd, year: 1979);

        var result = MatchScorer.Evaluate(read, [unseen, seen], Profile(ScoringProfileKind.Cover));

        result.Ranked[0].Release.Key.ShouldBe("seen");
        result.Outcome.ShouldBe(MatchOutcome.Matched);
    }

    [Fact]
    public void Distances_that_are_unusable_or_for_nobody_in_the_evaluation_leave_the_art_term_out_for_everyone()
    {
        var a = Release("a", "Alien", WorkKind.Film, ReleaseFormat.Dvd, year: 1979);
        var b = Release("b", "Alien", WorkKind.Film, ReleaseFormat.Dvd, year: 1979);
        var cover = Profile(ScoringProfileKind.Cover);

        foreach (var distances in new Dictionary<string, double>[]
        {
            new() { ["a"] = double.NaN },
            new() { ["a"] = -1.0 },
            new() { ["somebody-else"] = 1.3 },
            new(),
        })
        {
            var read = new ReadItem { Title = "Alien", Format = ReleaseFormat.Dvd, Year = 1979, ArtDistances = distances };

            MatchScorer.Evaluate(read, [a, b], cover).Ranked.ShouldAllBe(r => r.Score == 1.0);
        }
    }

    [Fact]
    public void A_profile_that_ignores_art_is_unaffected_by_distances_on_other_candidates()
    {
        var read = new ReadItem
        {
            Title = "Alien",
            Format = ReleaseFormat.Dvd,
            Year = 1979,
            ArtDistances = new Dictionary<string, double> { ["a"] = 1.3 },
        };
        var a = Release("a", "Alien", WorkKind.Film, ReleaseFormat.Dvd, year: 1979);
        var b = Release("b", "Alien", WorkKind.Film, ReleaseFormat.Dvd, year: 1979);

        MatchScorer.Evaluate(read, [a, b], Profile(ScoringProfileKind.ShelfSpine)).Ranked.ShouldAllBe(r => r.Score == 1.0);
    }

    // ---- 3. thin evidence --------------------------------------------------------------------

    [Fact]
    public void A_title_only_read_with_a_single_candidate_is_at_most_Ambiguous()
    {
        var read = new ReadItem { Title = "The Legend of Zelda" };
        var zelda = Release("zelda", "The Legend of Zelda", WorkKind.Game, ReleaseFormat.GameCartridge, "NES", 1986);

        foreach (var kind in Enum.GetValues<ScoringProfileKind>())
        {
            var result = MatchScorer.Evaluate(read, [zelda], Profile(kind));

            result.Ranked[0].Score.ShouldBe(1.0);
            result.Outcome.ShouldBe(MatchOutcome.Ambiguous, kind.ToString());
        }
    }

    [Fact]
    public void A_title_and_year_read_where_the_release_year_is_unknown_is_at_most_Ambiguous()
    {
        var read = new ReadItem { Title = "Alien", Year = 1979 };
        var alien = Release("alien", "Alien", WorkKind.Film, ReleaseFormat.Dvd, year: null);

        MatchScorer.Evaluate(read, [alien], Profile(ScoringProfileKind.BarcodeProduct)).Outcome.ShouldBe(MatchOutcome.Ambiguous);
    }

    [Fact]
    public void A_term_the_release_cannot_answer_is_not_a_compared_signal()
    {
        // The read has a title, a platform and a format, but the release has no platform and no format: one signal.
        var read = new ReadItem { Title = "Super Mario 64", Platform = "N64", Format = ReleaseFormat.GameCartridge };
        var bare = Release("r", "Super Mario 64");

        MatchScorer.Evaluate(read, [bare], Profile(ScoringProfileKind.BarcodeProduct)).Outcome.ShouldBe(MatchOutcome.Ambiguous);
    }

    [Fact]
    public void A_term_with_no_weight_in_the_profile_is_not_a_compared_signal()
    {
        // The shelf spine ignores edition and art: a read of title plus edition is still one signal there.
        var read = new ReadItem { Title = "Super Mario 64", Edition = "Player's Choice" };
        var release = Release("r", "Super Mario 64", edition: "Player's Choice");

        MatchScorer.Evaluate(read, [release], Profile(ScoringProfileKind.ShelfSpine)).Outcome.ShouldBe(MatchOutcome.Ambiguous);
    }

    [Fact]
    public void Two_compared_signals_are_enough_for_a_Match()
    {
        var read = new ReadItem { Title = "Super Mario 64", Platform = "N64" };
        var release = Release("r", "Super Mario 64", platform: "Nintendo 64");

        MatchScorer.Evaluate(read, [release], Profile(ScoringProfileKind.BarcodeProduct)).Outcome.ShouldBe(MatchOutcome.Matched);
    }

    [Fact]
    public void A_barcode_exact_match_is_Matched_without_any_other_signal()
    {
        var id = new Identifier(IdentifierType.Gtin13, "4006381333931");
        var release = Release("r", "Whatever The Catalogue Calls It", identifiers: [id]);

        foreach (var read in new[]
        {
            new ReadItem { Identifiers = [id] },
            new ReadItem { Title = "Something Misread", Identifiers = [id] },
        })
        {
            MatchScorer.Evaluate(read, [release], Profile(ScoringProfileKind.BarcodeProduct)).Outcome.ShouldBe(MatchOutcome.Matched);
        }
    }

    // ---- 4. cap configuration ----------------------------------------------------------------

    [Fact]
    public void The_default_options_are_valid()
    {
        Should.NotThrow(() => new ScoringOptions().Validate());
    }

    [Theory]
    [InlineData(0.90)]   // equal to the Matched threshold
    [InlineData(0.95)]
    [InlineData(1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-0.1)]
    public void A_platform_mismatch_cap_that_does_not_stay_below_Matched_fails_validation(double cap)
    {
        var options = new ScoringOptions { Thresholds = new ScoringThresholds { PlatformMismatchCap = cap } };

        Should.Throw<InvalidOperationException>(() => options.Validate()).Message.ShouldContain(nameof(ScoringThresholds.PlatformMismatchCap));
        Should.Throw<InvalidOperationException>(() => options.Profile(ScoringProfileKind.Cover));
    }

    [Fact]
    public void Lowering_the_Matched_threshold_to_or_below_the_cap_fails_validation()
    {
        var options = new ScoringOptions { Thresholds = new ScoringThresholds { MatchedScore = 0.80 } };

        Should.Throw<InvalidOperationException>(() => options.Validate()).Message.ShouldContain(nameof(ScoringThresholds.PlatformMismatchCap));
    }

    [Fact]
    public void A_title_year_mismatch_cap_that_does_not_stay_below_Matched_fails_validation()
    {
        var options = new ScoringOptions { Thresholds = new ScoringThresholds { TitleYearMismatchCap = 0.90 } };

        Should.Throw<InvalidOperationException>(() => options.Validate()).Message.ShouldContain(nameof(ScoringThresholds.TitleYearMismatchCap));
    }

    [Fact]
    public void A_stricter_Matched_threshold_with_the_default_caps_is_valid()
    {
        var options = new ScoringOptions { Thresholds = new ScoringThresholds { MatchedScore = 0.95 } };

        Should.NotThrow(() => options.Validate());
        Should.NotThrow(() => options.Profile(ScoringProfileKind.Cover));
    }

    // ------------------------------------------------------------------------------------------

    public static TheoryData<ScoringProfileKind> AllProfiles => new(Enum.GetValues<ScoringProfileKind>());

    private static CandidateRelease Release(
        string key,
        string title,
        WorkKind kind = WorkKind.Other,
        ReleaseFormat? format = null,
        string? platform = null,
        int? year = null,
        IReadOnlyList<string>? creators = null,
        string? edition = null,
        IReadOnlyList<Identifier>? identifiers = null) =>
        new()
        {
            Key = key,
            Title = title,
            Kind = kind,
            Format = format,
            Platform = platform,
            Year = year,
            Creators = creators ?? [],
            Edition = edition,
            Identifiers = identifiers ?? [],
        };
}
