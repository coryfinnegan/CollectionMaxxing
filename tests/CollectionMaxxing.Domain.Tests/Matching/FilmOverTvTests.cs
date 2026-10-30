using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Domain.Tests.Matching;

// The twelfth eval run (2026-10-30): a VHS cover read as a film ranked a TV series above the film of the same title, both
// scoring a perfect 1.0. The format agreed (the provider's TV release is stored with the format that was read) and the
// type term never looked at the kind. A film and a show are the same family of video but not the same kind of work:
// the kind the reader named should win a tie, without ruling the other out when nothing else fits.
public class FilmOverTvTests
{
    private static readonly ScoringOptions Options = new();

    private static ScoringProfile Cover => Options.Profile(ScoringProfileKind.Cover);

    private static CandidateRelease Release(string key, string title, WorkKind kind, ReleaseFormat? format = null, int? year = null) =>
        new() { Key = key, Title = title, Kind = kind, Format = format, Year = year };

    [Theory]
    [InlineData(ReleaseFormat.Vhs)]
    [InlineData(null)]
    public void A_film_read_ranks_an_equal_titled_film_above_the_TV_release(ReleaseFormat? releaseFormat)
    {
        var read = new ReadItem { Title = "Lantern Road", Kind = WorkKind.Film, Format = ReleaseFormat.Vhs };
        var show = Release("show", "Lantern Road", WorkKind.Tv, releaseFormat, 2014);
        var film = Release("film", "Lantern Road", WorkKind.Film, releaseFormat, 1940);

        MatchScorer.Evaluate(read, [show, film], Cover).Ranked[0].Release.Key.ShouldBe("film");
        MatchScorer.Evaluate(read, [film, show], Cover).Ranked[0].Release.Key.ShouldBe("film");
    }

    [Fact]
    public void A_film_read_without_a_format_ranks_an_equal_titled_film_above_the_TV_release()
    {
        var read = new ReadItem { Title = "Lantern Road", Kind = WorkKind.Film };
        var show = Release("show", "Lantern Road", WorkKind.Tv);
        var film = Release("film", "Lantern Road", WorkKind.Film);

        MatchScorer.Evaluate(read, [show, film], Cover).Ranked[0].Release.Key.ShouldBe("film");
    }

    [Fact]
    public void A_TV_read_ranks_the_TV_release_above_an_equal_titled_film()
    {
        var read = new ReadItem { Title = "Lantern Road", Kind = WorkKind.Tv, Format = ReleaseFormat.Dvd };
        var show = Release("show", "Lantern Road", WorkKind.Tv, ReleaseFormat.Dvd, 2014);
        var film = Release("film", "Lantern Road", WorkKind.Film, ReleaseFormat.Dvd, 1940);

        MatchScorer.Evaluate(read, [film, show], Cover).Ranked[0].Release.Key.ShouldBe("show");
        MatchScorer.Evaluate(read, [show, film], Cover).Ranked[0].Release.Key.ShouldBe("show");
    }

    [Fact]
    public void A_TV_read_without_a_format_ranks_the_TV_release_above_an_equal_titled_film()
    {
        var read = new ReadItem { Title = "Lantern Road", Kind = WorkKind.Tv };
        var show = Release("show", "Lantern Road", WorkKind.Tv);
        var film = Release("film", "Lantern Road", WorkKind.Film);

        MatchScorer.Evaluate(read, [film, show], Cover).Ranked[0].Release.Key.ShouldBe("show");
    }

    [Theory]
    [InlineData(WorkKind.Film, WorkKind.Tv)]
    [InlineData(WorkKind.Tv, WorkKind.Film)]
    public void With_only_the_other_kind_of_video_on_file_the_read_still_finds_it_as_a_candidate(WorkKind readKind, WorkKind releaseKind)
    {
        var read = new ReadItem { Title = "Lantern Road", Kind = readKind };

        var result = MatchScorer.Evaluate(read, [Release("only", "Lantern Road", releaseKind)], Cover);

        result.Ranked.Count.ShouldBe(1);
        result.Ranked[0].Score.ShouldBeGreaterThanOrEqualTo(Options.Thresholds.AmbiguousScore);
        result.Outcome.ShouldNotBe(MatchOutcome.NotFound);
    }

    [Fact]
    public void With_only_a_TV_release_a_film_read_with_a_format_still_finds_it()
    {
        var read = new ReadItem { Title = "Lantern Road", Kind = WorkKind.Film, Format = ReleaseFormat.Vhs };

        var result = MatchScorer.Evaluate(read, [Release("only", "Lantern Road", WorkKind.Tv, ReleaseFormat.Vhs, 2014)], Cover);

        result.Ranked[0].Score.ShouldBeGreaterThanOrEqualTo(Options.Thresholds.AmbiguousScore);
        result.Outcome.ShouldNotBe(MatchOutcome.NotFound);
    }

    [Fact]
    public void A_film_and_a_show_are_a_partial_type_match_not_a_full_one_or_none()
    {
        var read = new ReadItem { Title = "Lantern Road", Kind = WorkKind.Film };

        var type = MatchScorer.Evaluate(read, [Release("show", "Lantern Road", WorkKind.Tv)], Cover)
            .Ranked[0].Terms.Single(t => t.Name == "type");

        type.Value.ShouldBeGreaterThan(0.0);
        type.Value.ShouldBeLessThan(1.0);
        type.Compared.ShouldBeTrue();
    }

    [Fact]
    public void A_film_read_is_still_no_match_for_a_game_of_the_same_name()
    {
        var read = new ReadItem { Title = "Lantern Road", Kind = WorkKind.Film };

        MatchScorer.Evaluate(read, [Release("game", "Lantern Road", WorkKind.Game)], Cover)
            .Ranked[0].Terms.Single(t => t.Name == "type").Value.ShouldBe(0.0);
    }

    [Fact]
    public void A_format_read_with_no_kind_still_agrees_with_a_show_and_a_film_alike()
    {
        var read = new ReadItem { Title = "Lantern Road", Format = ReleaseFormat.Dvd };

        var result = MatchScorer.Evaluate(
            read,
            [Release("show", "Lantern Road", WorkKind.Tv), Release("film", "Lantern Road", WorkKind.Film)],
            Cover);

        result.Ranked.ShouldAllBe(s => s.Terms.Single(t => t.Name == "type").Value == 1.0);
    }
}
