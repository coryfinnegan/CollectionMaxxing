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

        result.Ranked[0].Release.Key.ShouldBe("film");
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
        // The release's title already holds every word of the line, and the read is close to it: joining only makes the text longer.
        var rangers = new CandidateRelease { Key = "rangers", Title = "The Rescue Rangers", Kind = WorkKind.Film };

        var with = Evaluate(Read("RESCUE RANGERS", "THE RESCUE"), rangers);
        var without = Evaluate(Read("RESCUE RANGERS", null), rangers);

        TitleTerm(Of(with, "rangers")).ShouldBeGreaterThanOrEqualTo(TitleTerm(Of(without, "rangers")));
    }
}
