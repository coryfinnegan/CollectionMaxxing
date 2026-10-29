using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Domain.Tests.Matching;

// Shapes taken from real scans on the owner's server (2026-10-24 to 10-29): perfect title and author reads stuck at
// ~0.75 because terms no candidate could answer averaged in at 0.5, movie covers that could never be Matched because
// provider films carry no format, junk platform words capping real games, and subtitles hiding the right book.
public class ScanEvidenceTests
{
    private static readonly ScoringOptions Options = new();

    private static ScoringProfile Profile(ScoringProfileKind kind) => Options.Profile(kind);

    // ---- terms nobody can answer drop out ------------------------------------------------------

    [Fact]
    public void A_perfect_title_and_author_read_is_not_dragged_down_by_fields_no_candidate_can_answer()
    {
        // The model sent an edition and a platform; neither provider book has either on file.
        var read = new ReadItem
        {
            Title = "Suddenly You",
            Creator = "Lisa Kleypas",
            Kind = WorkKind.Book,
            Edition = "Avon Historical Romance",
            Platform = "Avon",
        };
        var right = Release("right", "Suddenly You", WorkKind.Book, creators: ["Lisa Kleypas"]);
        var other = Release("other", "Suddenly You", WorkKind.Book, creators: ["Someone Else Entirely"]);

        var result = MatchScorer.Evaluate(read, [other, right], Profile(ScoringProfileKind.Cover));

        result.Ranked[0].Release.Key.ShouldBe("right");
        result.Ranked[0].Score.ShouldBe(1.0);
        result.Outcome.ShouldBe(MatchOutcome.Matched);
    }

    [Fact]
    public void A_release_that_confirms_a_term_still_beats_one_that_cannot_answer_it()
    {
        var read = new ReadItem { Title = "Alien", Kind = WorkKind.Film, Year = 1979 };
        var dated = Release("dated", "Alien", WorkKind.Film, year: 1979);
        var undated = Release("undated", "Alien", WorkKind.Film);

        var result = MatchScorer.Evaluate(read, [undated, dated], Profile(ScoringProfileKind.Cover));

        result.Ranked[0].Release.Key.ShouldBe("dated");
        result.Ranked[1].Score.ShouldBeLessThan(result.Ranked[0].Score);
    }

    [Fact]
    public void Score_on_its_own_keeps_a_term_the_release_cannot_answer_neutral()
    {
        var read = new ReadItem { Title = "Alien", Year = 1979 };

        MatchScorer.Score(read, Release("r", "Alien"), Profile(ScoringProfileKind.Cover)).ShouldBeLessThan(1.0);
    }

    // ---- a format read agrees with a release kind --------------------------------------------

    [Theory]
    [InlineData(ReleaseFormat.Dvd, WorkKind.Film)]
    [InlineData(ReleaseFormat.Vhs, WorkKind.Film)]
    [InlineData(ReleaseFormat.Bluray, WorkKind.Tv)]
    [InlineData(ReleaseFormat.GameCartridge, WorkKind.Game)]
    [InlineData(ReleaseFormat.Paperback, WorkKind.Book)]
    public void A_format_read_agrees_with_a_formatless_release_of_the_matching_kind(ReleaseFormat format, WorkKind kind)
    {
        var read = new ReadItem { Title = "The Matrix", Format = format };

        var result = MatchScorer.Evaluate(read, [Release("r", "The Matrix", kind)], Profile(ScoringProfileKind.Cover));

        result.Ranked[0].ComparedSignals.ShouldBe(2);
        result.Ranked[0].Score.ShouldBe(1.0);
        result.Outcome.ShouldBe(MatchOutcome.Matched);
    }

    [Fact]
    public void A_release_stored_with_an_unknown_format_is_formatless_not_a_different_format()
    {
        // Every film and game a provider answered is stored with format Unknown.
        var read = new ReadItem { Title = "The Matrix", Format = ReleaseFormat.Dvd };
        var stored = Release("r", "The Matrix", WorkKind.Film, format: ReleaseFormat.Unknown);

        var result = MatchScorer.Evaluate(read, [stored], Profile(ScoringProfileKind.Cover));

        result.Ranked[0].Score.ShouldBe(1.0);
        result.Outcome.ShouldBe(MatchOutcome.Matched);
    }

    [Fact]
    public void A_dvd_read_does_not_agree_with_a_formatless_game()
    {
        var read = new ReadItem { Title = "The Matrix", Format = ReleaseFormat.Dvd };
        var film = Release("film", "The Matrix", WorkKind.Film);
        var game = Release("game", "The Matrix", WorkKind.Game);

        var result = MatchScorer.Evaluate(read, [game, film], Profile(ScoringProfileKind.Cover));

        result.Ranked[0].Release.Key.ShouldBe("film");
        result.Outcome.ShouldBe(MatchOutcome.Matched);
    }

    [Fact]
    public void A_movie_cover_read_against_a_film_and_its_remake_is_Ambiguous()
    {
        var read = new ReadItem { Title = "Total Recall", Format = ReleaseFormat.Dvd };

        var result = MatchScorer.Evaluate(
            read,
            [Release("1990", "Total Recall", WorkKind.Film, year: 1990), Release("2012", "Total Recall", WorkKind.Film, year: 2012)],
            Profile(ScoringProfileKind.Cover));

        result.Outcome.ShouldBe(MatchOutcome.Ambiguous);
    }

    [Fact]
    public void A_read_kind_of_other_is_no_evidence_against_a_real_kind()
    {
        var read = new ReadItem { Title = "Nigella Bites", Kind = WorkKind.Other };

        MatchScorer.Evaluate(read, [Release("r", "Nigella Bites", WorkKind.Book)], Profile(ScoringProfileKind.Cover))
            .Ranked[0].Score.ShouldBe(1.0);
    }

    // ---- platform words ----------------------------------------------------------------------

    [Theory]
    [InlineData("boxed")]
    [InlineData("looseCart")]
    [InlineData("book")]
    public void A_platform_word_no_console_goes_by_is_not_a_mismatch(string junk)
    {
        var read = new ReadItem { Title = "Super Mario 64", Platform = junk, Kind = WorkKind.Game };
        var release = Release("r", "Super Mario 64", WorkKind.Game, platform: "Nintendo 64");

        var result = MatchScorer.Evaluate(read, [release], Profile(ScoringProfileKind.Cover));

        result.Ranked[0].HasKnownConflict.ShouldBeFalse();
        result.Ranked[0].Score.ShouldBe(1.0);
    }

    [Fact]
    public void Two_consoles_the_table_knows_still_mismatch()
    {
        var read = new ReadItem { Title = "Hyrule Warriors: Age of Imprisonment", Platform = "Nintendo Switch", Kind = WorkKind.Game };
        var release = Release("r", "Hyrule Warriors: Age of Imprisonment", WorkKind.Game, platform: "Nintendo Switch 2");

        var result = MatchScorer.Evaluate(read, [release], Profile(ScoringProfileKind.Cover));

        result.Ranked[0].HasKnownConflict.ShouldBeTrue();
        result.Outcome.ShouldBe(MatchOutcome.Ambiguous);
    }

    // ---- artwork: a candidate nobody could compare never wins on art ----------------------------

    [Fact]
    public void A_coverless_candidate_does_not_outscore_a_covered_one_because_the_picture_matched_nothing_well()
    {
        // #519: the coverless impostor rode a neutral 0.5 while both compared covers came out below it.
        var read = new ReadItem
        {
            Title = "Suddenly You",
            Kind = WorkKind.Book,
            ArtDistances = new Dictionary<string, double> { ["right"] = 1.1, ["sibling"] = 1.15 },
        };
        var right = Release("right", "Suddenly You", WorkKind.Book);
        var sibling = Release("sibling", "Suddenly You", WorkKind.Book);
        var coverless = Release("coverless", "Suddenly You", WorkKind.Book);

        var result = MatchScorer.Evaluate(read, [coverless, sibling, right], Profile(ScoringProfileKind.Cover));

        result.Ranked[0].Release.Key.ShouldBe("right");
        result.Ranked.Single(r => r.Release.Key == "coverless").Score.ShouldBeLessThanOrEqualTo(result.Ranked[0].Score);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static CandidateRelease Release(
        string key,
        string title,
        WorkKind kind = WorkKind.Other,
        ReleaseFormat? format = null,
        string? platform = null,
        int? year = null,
        IReadOnlyList<string>? creators = null,
        string? edition = null) =>
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
        };
}
