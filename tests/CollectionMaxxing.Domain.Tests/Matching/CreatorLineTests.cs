using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Domain.Tests.Matching;

// Build-161 eval, vhs-dvd-shelf-01 t22 "Free Willy 3: The Rescue": the Vision read's title was "FREE WILLY 3" and the spine's other line,
// "THE RESCUE", went to the phone's creator slot. The sanitiser keeps no creator from it, so the scorer never saw it, and a book titled
// "Free Willy 3" beat the film. The line is the rest of the film's title: it counts toward a release whose title holds every word of it.
public class CreatorLineTests
{
    private static readonly ScoringOptions Options = new();

    private static ScoringProfile Spine => Options.Profile(ScoringProfileKind.ShelfSpine);

    private static CandidateRelease Film { get; } = new() { Key = "film", Title = "Free Willy 3: The Rescue", Kind = WorkKind.Film };

    private static CandidateRelease BookA { get; } =
        new() { Key = "bookA", Title = "Free Willy 3", Kind = WorkKind.Book, Creators = ["Nancy E. Krulik"] };

    private static CandidateRelease BookB { get; } =
        new() { Key = "bookB", Title = "Free Willy 3", Kind = WorkKind.Book, Creators = ["Todd Strasser", "John Mattson"] };

    private static ReadItem Read(string title, string? line) => new() { Title = title, CreatorLine = line };

    private static MatchResult Evaluate(ReadItem read, params CandidateRelease[] pool) => MatchScorer.Evaluate(read, pool, Spine);

    private static ScoredRelease Of(MatchResult result, string key) => result.Ranked.Single(r => r.Release.Key == key);

    private static double TitleTerm(ScoredRelease scored) => scored.Terms.Single(t => t.Name == "title").Value;

    [Fact]
    public void A_creator_line_that_reads_as_no_name_and_holds_the_rest_of_the_title_lifts_the_release_whose_title_holds_it()
    {
        var result = Evaluate(Read("FREE WILLY 3", "THE RESCUE"), Film, BookA, BookB);

        Of(result, "film").Score.ShouldBeGreaterThanOrEqualTo(result.Ranked.Max(r => r.Score));
        TitleTerm(Of(result, "film")).ShouldBe(1.0);
    }

    [Fact]
    public void Without_the_creator_line_the_shorter_title_stays_first()
    {
        var result = Evaluate(Read("FREE WILLY 3", null), Film, BookA, BookB);

        result.Ranked[0].Release.Key.ShouldNotBe("film");
        TitleTerm(Of(result, "film")).ShouldBe(0.8, 0.0005);
    }

    [Fact]
    public void The_creator_line_counts_in_either_order()
    {
        var stone = new CandidateRelease { Key = "stone", Title = "Romancing the Stone", Kind = WorkKind.Film };

        var result = Evaluate(Read("STONE", "ROMANCING THE"), stone);

        TitleTerm(Of(result, "stone")).ShouldBe(1.0);
    }

    [Fact]
    public void A_release_missing_a_word_of_the_line_scores_as_it_does_today()
    {
        var escape = new CandidateRelease { Key = "escape", Title = "Free Willy 3: The Escape", Kind = WorkKind.Film };

        var with = Evaluate(Read("FREE WILLY 3", "THE RESCUE"), BookA, escape);
        var without = Evaluate(Read("FREE WILLY 3", null), BookA, escape);

        with.Ranked.Select(r => (r.Release.Key, r.Score)).ShouldBe(without.Ranked.Select(r => (r.Release.Key, r.Score)));
    }

    [Fact]
    public void A_line_that_adds_no_word_to_the_reads_title_changes_nothing()
    {
        var with = Evaluate(Read("FREE WILLY 3", "WILLY"), BookA, BookB, Film);
        var without = Evaluate(Read("FREE WILLY 3", null), BookA, BookB, Film);

        with.Ranked.Select(r => (r.Release.Key, r.Score)).ShouldBe(without.Ranked.Select(r => (r.Release.Key, r.Score)));
    }

    [Fact]
    public void The_creator_line_never_lowers_a_title_term()
    {
        // The guard holds (the line's only word, "novel", is in the release and not in the read), but the joined text scores lower.
        var novel = new CandidateRelease { Key = "novel", Title = "Dune: A Novel", Kind = WorkKind.Book };

        var with = Evaluate(Read("DUNE", "THE NOVEL"), novel);
        var without = Evaluate(Read("DUNE", null), novel);

        TitleTerm(Of(without, "novel")).ShouldBe(1.0);
        TitleTerm(Of(with, "novel")).ShouldBe(1.0);
    }

    [Fact]
    public void A_line_whose_only_word_is_in_the_reads_title_changes_nothing_even_when_the_join_would_score_higher()
    {
        var willy = new CandidateRelease { Key = "willy", Title = "Free Willy 3", Kind = WorkKind.Film };

        var with = Evaluate(Read("FREE WILLY", "WILLY 3"), willy);
        var without = Evaluate(Read("FREE WILLY", null), willy);

        TitleTerm(Of(with, "willy")).ShouldBe(TitleTerm(Of(without, "willy")));
        with.Ranked[0].Score.ShouldBe(without.Ranked[0].Score);
    }

    [Fact]
    public void Every_word_of_the_line_must_be_in_the_release_not_only_one()
    {
        var film = new CandidateRelease { Key = "film", Title = "Free Willy 3: The Rescue", Kind = WorkKind.Film };

        var with = Evaluate(Read("FREE WILLY 3", "THE GREAT RESCUE"), film);
        var without = Evaluate(Read("FREE WILLY 3", null), film);

        TitleTerm(Of(with, "film")).ShouldBe(TitleTerm(Of(without, "film")));
        TitleTerm(Of(with, "film")).ShouldBe(0.8, 0.0005);
    }

    [Fact]
    public void One_line_word_missing_from_the_reads_title_is_enough_to_lift()
    {
        var goblet = new CandidateRelease { Key = "goblet", Title = "Harry Potter and the Goblet of Fire", Kind = WorkKind.Book };

        var with = Evaluate(Read("HARRY POTTER", "HARRY POTTER AND THE GOBLET OF FIRE"), goblet);
        var without = Evaluate(Read("HARRY POTTER", null), goblet);

        TitleTerm(Of(without, "goblet")).ShouldBe(0.343, 0.0005);
        TitleTerm(Of(with, "goblet")).ShouldBe(0.9, 0.0005);
    }

    [Fact]
    public void A_tie_with_the_lifted_release_keeps_the_pool_order_whichever_it_is()
    {
        var filmLast = Evaluate(Read("FREE WILLY 3", "THE RESCUE"), BookA, BookB, Film);

        TitleTerm(Of(filmLast, "film")).ShouldBe(1.0);
        Of(filmLast, "film").Score.ShouldBe(Of(filmLast, "bookA").Score);
    }

    [Theory]
    [InlineData("FOLLOWS", "IT", "It Follows", 1.0)]
    [InlineData("IN THE AIR", "UP", "Up in the Air", 1.0)]
    [InlineData("HOMECOMING", "SPIDER-MAN", "Spider-Man: Homecoming", 1.0)]
    [InlineData("FREE WILLY", "3", "Free Willy 3", 0.625)]
    [InlineData("ROMANCING STONE", "THE", "Romancing the Stone", 0.789)]
    public void The_words_of_a_line_follow_the_title_word_rule(string title, string line, string releaseTitle, double expected)
    {
        var release = new CandidateRelease { Key = "r", Title = releaseTitle, Kind = WorkKind.Film };

        TitleTerm(Of(Evaluate(Read(title, line), release), "r")).ShouldBe(expected, 0.0015);
    }
}
