using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Identifiers;

namespace CollectionMaxxing.Domain.Tests.Matching;

// Locked acceptance tests for CAT-1 (docs/02 section 6; docs/09 "Domain: title normalisation and scoring").
// Do not weaken, skip or delete.
public class MatchScorerTests
{
    private static readonly ScoringOptions Options = new();

    private static ScoringProfile Profile(ScoringProfileKind kind) => Options.Profile(kind);

    public static TheoryData<ScoringProfileKind> AllProfiles =>
        new(Enum.GetValues<ScoringProfileKind>());

    // ---- exact identifiers -------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Exact_identifier_match_scores_1_0_regardless_of_title(ScoringProfileKind kind)
    {
        var id = new Identifier(IdentifierType.Gtin13, "4006381333931");
        var read = new ReadItem { Title = "Completely Different Words", Platform = "PS5", Identifiers = [id] };
        var release = Release("r1", "The Matrix", WorkKind.Film, ReleaseFormat.Dvd, platform: "N64", identifiers: [id]);

        MatchScorer.Score(read, release, Profile(kind)).ShouldBe(1.0);
    }

    [Fact]
    public void A_different_identifier_gets_no_free_pass()
    {
        var read = new ReadItem
        {
            Title = "Completely Different Words",
            Identifiers = [new Identifier(IdentifierType.Gtin13, "4006381333931")],
        };
        var release = Release("r1", "The Matrix", identifiers: [new Identifier(IdentifierType.Gtin13, "5901234123457")]);

        MatchScorer.Score(read, release, Profile(ScoringProfileKind.BarcodeProduct)).ShouldBeLessThan(0.5);
    }

    [Fact]
    public void An_ISBN_13_and_a_GTIN_13_with_the_same_digits_are_not_the_same_identifier()
    {
        // They never are in practice (Gtin.ToIdentifier turns Bookland codes into ISBN-13s), but the scorer must not
        // match across types.
        var read = new ReadItem { Title = "Alpha", Identifiers = [new Identifier(IdentifierType.Isbn13, "9780306406157")] };
        var release = Release("r1", "Omega", identifiers: [new Identifier(IdentifierType.Asin, "B000FBJCJE")]);

        MatchScorer.Score(read, release, Profile(ScoringProfileKind.BarcodeProduct)).ShouldBeLessThan(1.0);
    }

    // ---- pinned hard cases -------------------------------------------------------------------

    [Fact]
    public void Alien_1979_outranks_Aliens_1986_for_the_query_Alien_1979_DVD()
    {
        var alien = Release("alien", "Alien", WorkKind.Film, ReleaseFormat.Dvd, year: 1979);
        var aliens = Release("aliens", "Aliens", WorkKind.Film, ReleaseFormat.Dvd, year: 1986);

        // As the NameCleaner would hand it over: the format is pulled out, the year may still sit in the title.
        foreach (var read in new[]
        {
            new ReadItem { Title = "Alien", Year = 1979, Format = ReleaseFormat.Dvd },
            new ReadItem { Title = "Alien 1979", Format = ReleaseFormat.Dvd },
        })
        {
            var result = MatchScorer.Evaluate(read, [aliens, alien], Profile(ScoringProfileKind.BarcodeProduct));

            result.Ranked[0].Release.Key.ShouldBe("alien");
            result.Ranked[0].Score.ShouldBeGreaterThan(result.Ranked[1].Score);
            result.Outcome.ShouldBe(MatchOutcome.Matched);
        }
    }

    [Fact]
    public void Aliens_is_not_a_match_for_Alien_when_it_is_the_only_candidate()
    {
        var read = new ReadItem { Title = "Alien", Format = ReleaseFormat.Dvd };

        var result = MatchScorer.Evaluate(
            read, [Release("aliens", "Aliens", WorkKind.Film, ReleaseFormat.Dvd, year: 1986)], Profile(ScoringProfileKind.BarcodeProduct));

        result.Outcome.ShouldNotBe(MatchOutcome.Matched);
    }

    [Fact]
    public void Metroid_Prime_2_with_the_subtitle_missing_is_Ambiguous_not_Matched()
    {
        var read = new ReadItem { Title = "Metroid Prime 2", Platform = "GameCube", Format = ReleaseFormat.GameDisc };
        var echoes = Release("echoes", "Metroid Prime 2: Echoes", WorkKind.Game, ReleaseFormat.GameDisc, "Nintendo GameCube", 2004);
        var prime = Release("prime", "Metroid Prime", WorkKind.Game, ReleaseFormat.GameDisc, "Nintendo GameCube", 2002);

        var result = MatchScorer.Evaluate(read, [prime, echoes], Profile(ScoringProfileKind.BarcodeProduct));

        result.Ranked[0].Release.Key.ShouldBe("echoes");
        result.Outcome.ShouldBe(MatchOutcome.Ambiguous);
    }

    [Fact]
    public void Metroid_Prime_read_in_full_is_Matched_and_does_not_confuse_the_sequel()
    {
        var read = new ReadItem { Title = "Metroid Prime", Platform = "GameCube", Format = ReleaseFormat.GameDisc };
        var echoes = Release("echoes", "Metroid Prime 2: Echoes", WorkKind.Game, ReleaseFormat.GameDisc, "Nintendo GameCube", 2004);
        var prime = Release("prime", "Metroid Prime", WorkKind.Game, ReleaseFormat.GameDisc, "Nintendo GameCube", 2002);

        var result = MatchScorer.Evaluate(read, [echoes, prime], Profile(ScoringProfileKind.BarcodeProduct));

        result.Ranked[0].Release.Key.ShouldBe("prime");
        result.Outcome.ShouldBe(MatchOutcome.Matched);
    }

    private static readonly CandidateRelease JurassicNovel = Release(
        "novel", "Jurassic Park", WorkKind.Book, ReleaseFormat.Paperback, year: 1990, creators: ["Michael Crichton"]);

    private static readonly CandidateRelease JurassicFilm = Release(
        "film", "Jurassic Park", WorkKind.Film, ReleaseFormat.Dvd, year: 1993, creators: ["Steven Spielberg"]);

    [Fact]
    public void Jurassic_Park_read_with_nothing_but_the_title_is_Ambiguous_between_the_novel_and_the_film()
    {
        var read = new ReadItem { Title = "Jurassic Park" };

        var result = MatchScorer.Evaluate(read, [JurassicNovel, JurassicFilm], Profile(ScoringProfileKind.BarcodeProduct));

        result.Outcome.ShouldBe(MatchOutcome.Ambiguous);
        result.Ranked.Count.ShouldBe(2);
    }

    [Fact]
    public void Jurassic_Park_read_as_a_paperback_is_the_novel()
    {
        var read = new ReadItem { Title = "Jurassic Park", Format = ReleaseFormat.Paperback };

        var result = MatchScorer.Evaluate(read, [JurassicFilm, JurassicNovel], Profile(ScoringProfileKind.BarcodeProduct));

        result.Outcome.ShouldBe(MatchOutcome.Matched);
        result.Ranked[0].Release.Key.ShouldBe("novel");
    }

    [Fact]
    public void Jurassic_Park_read_as_a_DVD_is_the_film()
    {
        var read = new ReadItem { Title = "Jurassic Park", Format = ReleaseFormat.Dvd };

        var result = MatchScorer.Evaluate(read, [JurassicNovel, JurassicFilm], Profile(ScoringProfileKind.BarcodeProduct));

        result.Outcome.ShouldBe(MatchOutcome.Matched);
        result.Ranked[0].Release.Key.ShouldBe("film");
    }

    [Fact]
    public void Jurassic_Park_on_a_spine_is_settled_by_the_author()
    {
        var read = new ReadItem { Title = "Jurassic Park", Creator = "Michael Crichton" };

        var result = MatchScorer.Evaluate(read, [JurassicFilm, JurassicNovel], Profile(ScoringProfileKind.ShelfSpine));

        result.Outcome.ShouldBe(MatchOutcome.Matched);
        result.Ranked[0].Release.Key.ShouldBe("novel");
    }

    // ---- platform ----------------------------------------------------------------------------

    [Theory]
    [InlineData("N64", "Nintendo 64")]
    [InlineData("Nintendo 64", "N64")]
    [InlineData("nintendo gamecube", "GameCube")]
    [InlineData("PS2", "PlayStation 2")]
    public void The_same_platform_under_different_names_is_not_a_mismatch(string read, string release)
    {
        var item = new ReadItem { Title = "Super Mario 64", Platform = read, Format = ReleaseFormat.GameCartridge };
        var candidate = Release("r", "Super Mario 64", WorkKind.Game, ReleaseFormat.GameCartridge, release);

        MatchScorer.Evaluate(item, [candidate], Profile(ScoringProfileKind.BarcodeProduct))
            .Outcome.ShouldBe(MatchOutcome.Matched);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void A_platform_mismatch_caps_the_score_below_the_Matched_threshold_in_every_profile(ScoringProfileKind kind)
    {
        var profile = Profile(kind);
        var read = new ReadItem
        {
            Title = "Super Mario 64",
            Platform = "N64",
            Format = ReleaseFormat.GameCartridge,
            Year = 1996,
            Creator = "Nintendo",
            Edition = "Player's Choice",
            ArtDistances = new Dictionary<string, double> { ["r"] = 0.0 },
        };
        var wrongPlatform = Release(
            "r", "Super Mario 64", WorkKind.Game, ReleaseFormat.GameCartridge, "Nintendo GameCube", 1996, ["Nintendo"], "Player's Choice");
        var rightPlatform = wrongPlatform with { Platform = "Nintendo 64" };

        MatchScorer.Score(read, rightPlatform, profile).ShouldBeGreaterThanOrEqualTo(profile.Thresholds.MatchedScore);
        var capped = MatchScorer.Score(read, wrongPlatform, profile);
        capped.ShouldBeLessThan(profile.Thresholds.MatchedScore);

        var result = MatchScorer.Evaluate(read, [wrongPlatform], profile);
        result.Outcome.ShouldNotBe(MatchOutcome.Matched);
    }

    [Fact]
    public void Super_Mario_64_on_the_wrong_console_is_Ambiguous_rather_than_Matched_or_lost()
    {
        var read = new ReadItem { Title = "Super Mario 64", Platform = "N64", Format = ReleaseFormat.GameCartridge };
        var onGameCube = Release("gc", "Super Mario 64", WorkKind.Game, ReleaseFormat.GameCartridge, "Nintendo GameCube");

        MatchScorer.Evaluate(read, [onGameCube], Profile(ScoringProfileKind.BarcodeProduct))
            .Outcome.ShouldBe(MatchOutcome.Ambiguous);
    }

    // ---- lead over the runner-up -------------------------------------------------------------

    [Fact]
    public void A_lead_of_less_than_0_08_over_the_runner_up_yields_Ambiguous()
    {
        var read = new ReadItem { Title = "The Legend of Zelda", Format = ReleaseFormat.GameCartridge, Year = 1987 };
        var first = Release("1987", "The Legend of Zelda", WorkKind.Game, ReleaseFormat.GameCartridge, year: 1987);
        var second = Release("1988", "The Legend of Zelda", WorkKind.Game, ReleaseFormat.GameCartridge, year: 1988);

        var result = MatchScorer.Evaluate(read, [second, first], Profile(ScoringProfileKind.BarcodeProduct));

        result.Ranked[0].Release.Key.ShouldBe("1987");
        (result.Ranked[0].Score - result.Ranked[1].Score).ShouldBeLessThan(0.08);
        result.Outcome.ShouldBe(MatchOutcome.Ambiguous);
    }

    [Fact]
    public void A_clear_lead_over_the_runner_up_yields_Matched()
    {
        var read = new ReadItem { Title = "The Legend of Zelda", Format = ReleaseFormat.GameCartridge, Year = 1987 };
        var first = Release("zelda", "The Legend of Zelda", WorkKind.Game, ReleaseFormat.GameCartridge, year: 1987);
        var other = Release("link", "Zelda II: The Adventure of Link", WorkKind.Game, ReleaseFormat.GameCartridge, year: 1988);

        var result = MatchScorer.Evaluate(read, [other, first], Profile(ScoringProfileKind.BarcodeProduct));

        result.Outcome.ShouldBe(MatchOutcome.Matched);
        result.Ranked[0].Release.Key.ShouldBe("zelda");
    }

    [Fact]
    public void Nothing_close_is_NotFound()
    {
        var read = new ReadItem { Title = "The Legend of Zelda", Format = ReleaseFormat.GameCartridge };

        MatchScorer.Evaluate(
                read,
                [Release("x", "Casablanca", WorkKind.Film, ReleaseFormat.Dvd, year: 1942)],
                Profile(ScoringProfileKind.BarcodeProduct))
            .Outcome.ShouldBe(MatchOutcome.NotFound);
    }

    [Fact]
    public void No_candidates_is_NotFound_and_never_throws()
    {
        var result = MatchScorer.Evaluate(
            new ReadItem { Title = "Anything" }, [], Profile(ScoringProfileKind.BarcodeProduct));

        result.Outcome.ShouldBe(MatchOutcome.NotFound);
        result.Ranked.ShouldBeEmpty();
    }

    [Fact]
    public void A_read_with_nothing_in_it_scores_zero()
    {
        MatchScorer.Score(new ReadItem(), Release("r", "Alien"), Profile(ScoringProfileKind.BarcodeProduct)).ShouldBe(0.0);
    }

    [Fact]
    public void Results_are_ordered_best_first_with_ties_kept_in_input_order()
    {
        var read = new ReadItem { Title = "Alien", Format = ReleaseFormat.Dvd };
        var a = Release("a", "Alien", WorkKind.Film, ReleaseFormat.Dvd);
        var b = Release("b", "Alien", WorkKind.Film, ReleaseFormat.Dvd);
        var worse = Release("w", "Aliens", WorkKind.Film, ReleaseFormat.Dvd);

        var result = MatchScorer.Evaluate(read, [worse, a, b], Profile(ScoringProfileKind.BarcodeProduct));

        result.Ranked.Select(r => r.Release.Key).ShouldBe(["a", "b", "w"]);
    }

    // ---- the visual (art) term ----------------------------------------------------------------

    private static (ReadItem Near, ReadItem Far, CandidateRelease Release) ArtCase()
    {
        var release = Release("r", "Super Mario 64", WorkKind.Game, ReleaseFormat.GameCartridge, "Nintendo 64", 1996);
        ReadItem Read(double distance) => new()
        {
            Title = "Super Mario 64",
            Platform = "N64",
            Format = ReleaseFormat.GameCartridge,
            Year = 1996,
            ArtDistances = new Dictionary<string, double> { ["r"] = distance },
        };
        return (Read(0.0), Read(1.3), release);
    }

    [Fact]
    public void The_cover_profile_uses_the_visual_term()
    {
        var (near, far, release) = ArtCase();
        var cover = Profile(ScoringProfileKind.Cover);

        MatchScorer.Score(near, release, cover).ShouldBeGreaterThan(MatchScorer.Score(far, release, cover));
    }

    [Fact]
    public void The_shelf_spine_profile_ignores_the_visual_term()
    {
        var (near, far, release) = ArtCase();
        var spine = Profile(ScoringProfileKind.ShelfSpine);

        MatchScorer.Score(near, release, spine).ShouldBe(MatchScorer.Score(far, release, spine));
    }

    [Fact]
    public void The_art_term_is_ignored_and_weights_redistributed_when_the_phone_sent_no_distances()
    {
        var (near, _, release) = ArtCase();
        var withoutDistances = near with { ArtDistances = null };
        var cover = Profile(ScoringProfileKind.Cover);

        // Everything else is a perfect match, so dropping the art term must not cost anything (it would cost 0.20 if the
        // weight stayed in the denominator).
        MatchScorer.Score(withoutDistances, release, cover).ShouldBe(1.0);
        MatchScorer.Score(withoutDistances with { ArtDistances = new Dictionary<string, double>() }, release, cover).ShouldBe(1.0);
    }

    [Fact]
    public void Distances_for_other_releases_leave_this_release_without_an_art_term()
    {
        var (near, _, release) = ArtCase();
        var other = near with { ArtDistances = new Dictionary<string, double> { ["somebody-else"] = 1.3 } };

        MatchScorer.Score(other, release, Profile(ScoringProfileKind.Cover)).ShouldBe(1.0);
    }

    [Theory]
    [InlineData(0.0, 1.0)]    // identical pictures
    [InlineData(1.0, 0.5)]    // cosine 0.5 (distance of unit vectors: cos = 1 - d^2 / 2)
    [InlineData(1.4142135623730951, 0.0)] // orthogonal
    [InlineData(2.0, 0.0)]    // opposite: the cosine is -1, clamped to 0
    [InlineData(5.0, 0.0)]    // far beyond the unit sphere: still clamped, never negative
    public void Visual_similarity_is_rescaled_to_0_1_and_clamped(double distance, double expected)
    {
        var artOnly = new ScoringProfile("art-only", new ScoringWeights(0, 0, 0, 0, 0, 0, 1), new ScoringThresholds());
        var read = new ReadItem { ArtDistances = new Dictionary<string, double> { ["r"] = distance } };

        MatchScorer.Score(read, Release("r", "Whatever"), artOnly).ShouldBe(expected, 1e-6);
    }

    [Fact]
    public void A_nonsense_distance_is_treated_as_no_picture_rather_than_poisoning_the_score()
    {
        var cover = Profile(ScoringProfileKind.Cover);
        var (near, _, release) = ArtCase();

        foreach (var bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1.0 })
        {
            var read = near with { ArtDistances = new Dictionary<string, double> { ["r"] = bad } };
            var score = MatchScorer.Score(read, release, cover);
            double.IsNaN(score).ShouldBeFalse();
            score.ShouldBeInRange(0.0, 1.0);
        }
    }

    [Fact]
    public void Artwork_separates_the_original_release_from_a_Players_Choice_re_release()
    {
        // Same title, platform and year: only the picture can tell them apart (docs/02 section 5, close calls).
        var original = Release("orig", "Super Mario 64", WorkKind.Game, ReleaseFormat.GameCartridge, "Nintendo 64", 1996);
        var reissue = Release("pc", "Super Mario 64", WorkKind.Game, ReleaseFormat.GameCartridge, "Nintendo 64", 1998, edition: "Player's Choice");
        var read = new ReadItem
        {
            Title = "Super Mario 64",
            Platform = "N64",
            Format = ReleaseFormat.GameCartridge,
            ArtDistances = new Dictionary<string, double> { ["orig"] = 0.1, ["pc"] = 0.9 },
        };

        var result = MatchScorer.Evaluate(read, [reissue, original], Profile(ScoringProfileKind.Cover));

        result.Ranked[0].Release.Key.ShouldBe("orig");
        result.Outcome.ShouldBe(MatchOutcome.Matched);
    }

    // ---- loose cartridges --------------------------------------------------------------------

    [Fact]
    public void Loose_cartridge_profile_a_platform_mismatch_outweighs_a_strong_visual_match()
    {
        var read = new ReadItem
        {
            Title = "Pokemon Red Version",
            Platform = "Game Boy",
            Format = ReleaseFormat.GameCartridge,
            ArtDistances = new Dictionary<string, double> { ["lookalike"] = 0.0, ["right"] = 1.2 },
        };
        // A different system's version of the same game whose box art looks identical, versus the right system's
        // cartridge whose artwork is only a weak visual match.
        var lookalike = Release("lookalike", "Pokemon Red Version", WorkKind.Game, ReleaseFormat.GameCartridge, "Game Boy Advance");
        var right = Release("right", "Pokemon Red Version", WorkKind.Game, ReleaseFormat.GameCartridge, "Game Boy");

        var loose = Profile(ScoringProfileKind.LooseCartridge);
        var scoreLookalike = MatchScorer.Score(read, lookalike, loose);
        var scoreRight = MatchScorer.Score(read, right, loose);

        scoreRight.ShouldBeGreaterThan(scoreLookalike + 0.15);
        MatchScorer.Evaluate(read, [lookalike, right], loose).Ranked[0].Release.Key.ShouldBe("right");

        // The same two candidates are almost level under the cover profile, where the picture counts for more.
        var cover = Profile(ScoringProfileKind.Cover);
        var coverGap = MatchScorer.Score(read, right, cover) - MatchScorer.Score(read, lookalike, cover);
        coverGap.ShouldBeLessThan(scoreRight - scoreLookalike);
    }

    // ---- other terms -------------------------------------------------------------------------

    [Fact]
    public void Year_proximity_prefers_the_closer_year_and_gives_nothing_for_a_distant_one()
    {
        var read = new ReadItem { Title = "Alien", Year = 1979 };
        var barcode = Profile(ScoringProfileKind.BarcodeProduct);

        var exact = MatchScorer.Score(read, Release("a", "Alien", year: 1979), barcode);
        var near = MatchScorer.Score(read, Release("b", "Alien", year: 1980), barcode);
        var far = MatchScorer.Score(read, Release("c", "Alien", year: 2003), barcode);

        exact.ShouldBeGreaterThan(near);
        near.ShouldBeGreaterThan(far);
        far.ShouldBeLessThan(near - 0.1);
    }

    [Fact]
    public void A_release_with_an_unknown_year_is_not_punished_as_harshly_as_a_wrong_one()
    {
        var read = new ReadItem { Title = "Alien", Year = 1979 };
        var barcode = Profile(ScoringProfileKind.BarcodeProduct);

        MatchScorer.Score(read, Release("a", "Alien", year: null), barcode)
            .ShouldBeGreaterThan(MatchScorer.Score(read, Release("b", "Alien", year: 2003), barcode));
    }

    [Fact]
    public void The_format_family_gives_partial_credit_but_an_exact_format_gives_full_credit()
    {
        var read = new ReadItem { Title = "Alien", Format = ReleaseFormat.Dvd };
        var barcode = Profile(ScoringProfileKind.BarcodeProduct);

        var dvd = MatchScorer.Score(read, Release("a", "Alien", WorkKind.Film, ReleaseFormat.Dvd), barcode);
        var bluray = MatchScorer.Score(read, Release("b", "Alien", WorkKind.Film, ReleaseFormat.Bluray), barcode);
        var book = MatchScorer.Score(read, Release("c", "Alien", WorkKind.Book, ReleaseFormat.Paperback), barcode);

        dvd.ShouldBeGreaterThan(bluray);
        bluray.ShouldBeGreaterThan(book);
    }

    [Fact]
    public void Without_a_format_the_kind_stands_in_for_it()
    {
        var read = new ReadItem { Title = "Alien", Kind = WorkKind.Film };
        var barcode = Profile(ScoringProfileKind.BarcodeProduct);

        MatchScorer.Score(read, Release("a", "Alien", WorkKind.Film), barcode)
            .ShouldBeGreaterThan(MatchScorer.Score(read, Release("b", "Alien", WorkKind.Game), barcode));
    }

    [Fact]
    public void An_edition_that_was_read_and_matches_outranks_one_that_does_not()
    {
        var read = new ReadItem { Title = "Super Mario 64", Edition = "Player's Choice" };
        var cover = Profile(ScoringProfileKind.Cover);

        var pc = MatchScorer.Score(read, Release("pc", "Super Mario 64", edition: "Players Choice"), cover);
        var plain = MatchScorer.Score(read, Release("plain", "Super Mario 64", edition: null), cover);

        pc.ShouldBeGreaterThan(plain);
    }

    // ---- configuration -----------------------------------------------------------------------

    [Fact]
    public void The_default_profiles_are_the_docs_02_table()
    {
        var o = new ScoringOptions();

        o.Profile(ScoringProfileKind.BarcodeProduct).Weights.ShouldBe(new ScoringWeights(0.55, 0.15, 0.15, 0.10, 0.05, 0, 0));
        o.Profile(ScoringProfileKind.ShelfSpine).Weights.ShouldBe(new ScoringWeights(0.50, 0.10, 0.15, 0.05, 0.20, 0, 0));
        o.Profile(ScoringProfileKind.Cover).Weights.ShouldBe(new ScoringWeights(0.35, 0.10, 0.15, 0.05, 0.05, 0.10, 0.20));
        o.Profile(ScoringProfileKind.LooseCartridge).Weights.ShouldBe(new ScoringWeights(0.40, 0.05, 0.30, 0.05, 0, 0.05, 0.15));
        o.Profile(ScoringProfileKind.BareLabel).Weights.ShouldBe(new ScoringWeights(0.55, 0.15, 0.15, 0.10, 0, 0, 0.05));
    }

    [Fact]
    public void The_default_thresholds_are_the_docs_02_outcome_rules()
    {
        var t = new ScoringOptions().Thresholds;

        t.MatchedScore.ShouldBe(0.90);
        t.MatchedLead.ShouldBe(0.08);
        t.AmbiguousScore.ShouldBe(0.60);
        t.PlatformMismatchCap.ShouldBeLessThan(t.MatchedScore);
    }

    [Fact]
    public void Every_profile_carries_the_options_thresholds()
    {
        var options = new ScoringOptions { Thresholds = new ScoringThresholds { MatchedScore = 0.95 } };

        foreach (var kind in Enum.GetValues<ScoringProfileKind>())
        {
            options.Profile(kind).Thresholds.MatchedScore.ShouldBe(0.95);
        }
    }

    [Fact]
    public void Thresholds_are_configuration_so_a_stricter_setting_turns_a_Match_into_Ambiguous()
    {
        var read = new ReadItem { Title = "The Legend of Zelda", Format = ReleaseFormat.GameCartridge };
        var zelda = Release("zelda", "The Legend of Zelda", WorkKind.Game, ReleaseFormat.GameCartridge);

        MatchScorer.Evaluate(read, [zelda], Profile(ScoringProfileKind.BarcodeProduct)).Outcome.ShouldBe(MatchOutcome.Matched);

        var strict = new ScoringOptions { Thresholds = new ScoringThresholds { MatchedScore = 1.01 } };
        MatchScorer.Evaluate(read, [zelda], strict.Profile(ScoringProfileKind.BarcodeProduct)).Outcome.ShouldBe(MatchOutcome.Ambiguous);
    }

    [Fact]
    public void Weights_are_configuration_so_zeroing_the_title_weight_makes_the_title_irrelevant()
    {
        var noTitle = new ScoringProfile("no-title", new ScoringWeights(0, 1, 0, 0, 0, 0, 0), new ScoringThresholds());
        var read = new ReadItem { Title = "Alien", Format = ReleaseFormat.Dvd };

        MatchScorer.Score(read, Release("r", "Something Else Entirely", WorkKind.Film, ReleaseFormat.Dvd), noTitle).ShouldBe(1.0);
    }

    [Fact]
    public void Scores_are_always_between_zero_and_one()
    {
        var random = new Random(42);
        string[] titles = ["Alien", "Aliens", "", "Metroid Prime 2: Echoes", "The Matrix", "   "];
        foreach (var kind in Enum.GetValues<ScoringProfileKind>())
        {
            for (var i = 0; i < 200; i++)
            {
                var read = new ReadItem
                {
                    Title = titles[random.Next(titles.Length)],
                    Year = random.Next(3) == 0 ? null : 1900 + random.Next(150),
                    Format = random.Next(2) == 0 ? null : (ReleaseFormat)random.Next(14),
                    Platform = random.Next(2) == 0 ? null : "N64",
                    ArtDistances = random.Next(2) == 0 ? null : new Dictionary<string, double> { ["r"] = random.NextDouble() * 4 - 1 },
                };
                var release = Release("r", titles[random.Next(titles.Length)], year: random.Next(2) == 0 ? null : 1900 + random.Next(150));

                MatchScorer.Score(read, release, Profile(kind)).ShouldBeInRange(0.0, 1.0);
            }
        }
    }

    // ------------------------------------------------------------------------------------------

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
