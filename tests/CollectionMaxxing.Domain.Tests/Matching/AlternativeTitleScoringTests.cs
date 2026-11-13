using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Domain.Tests.Matching;

// #828: a film is read by the title printed on its box, and the catalogue lists it under another main title. The scorer takes the best
// score over the main title and the alternative titles a candidate carries; the candidate stays what it was (its own title is the shown one).
public class AlternativeTitleScoringTests
{
    private static readonly ScoringProfile Spine = new ScoringOptions().Profile(ScoringProfileKind.ShelfSpine);

    private static CandidateRelease Film(string key, string title, int? year = null, params string[] alternatives) => new()
    {
        Key = key,
        Title = title,
        Kind = WorkKind.Film,
        Year = year,
        AlternativeTitles = alternatives,
    };

    private static ReadItem Read(string title) => new() { Title = title, Kind = WorkKind.Film };

    private const string BoxTitle = "HIGHLANDER THE FINAL DIMENSION";

    [Fact]
    public void A_film_read_by_an_alternative_title_is_matched_to_the_film_that_has_it()
    {
        var endgame = Film("endgame", "Highlander: Endgame", 2000);
        var second = Film("second", "Highlander II: The Quickening", 1991);
        var third = Film("third", "Highlander III: The Sorcerer", 1994, "Highlander: The Final Dimension");

        var result = MatchScorer.Evaluate(Read(BoxTitle), [endgame, second, third], Spine);

        result.Outcome.ShouldBe(MatchOutcome.Matched);
        result.Ranked[0].Release.Key.ShouldBe("third");
    }

    [Fact]
    public void Without_the_alternative_title_the_same_film_does_not_win()
    {
        var third = Film("third", "Highlander III: The Sorcerer", 1994);
        var endgame = Film("endgame", "Highlander: Endgame", 2000);

        MatchScorer.Evaluate(Read(BoxTitle), [third, endgame], Spine).Ranked[0].Release.Key.ShouldBe("endgame");
    }

    [Fact]
    public void The_shown_release_keeps_its_main_title_and_its_alternative_titles()
    {
        var third = Film("third", "Highlander III: The Sorcerer", 1994, "Highlander: The Final Dimension");

        var scored = MatchScorer.Evaluate(Read(BoxTitle), [third], Spine).Ranked.Single();

        scored.Release.ShouldBeSameAs(third);
        scored.Release.Title.ShouldBe("Highlander III: The Sorcerer");
    }

    [Fact]
    public void The_best_title_counts_and_a_worse_alternative_title_never_lowers_the_score()
    {
        var exact = Film("a", "Highlander: The Final Dimension");
        var withWorse = exact with { AlternativeTitles = ["Something Else Entirely"] };

        var plain = MatchScorer.Evaluate(Read(BoxTitle), [exact], Spine).Ranked.Single().Score;
        var scored = MatchScorer.Evaluate(Read(BoxTitle), [withWorse], Spine).Ranked.Single().Score;

        scored.ShouldBe(plain);
    }

    [Fact]
    public void The_best_of_several_alternative_titles_counts()
    {
        var film = Film("a", "Main Title Nobody Printed", null, "Wrong Words", "Highlander: The Final Dimension", "More Wrong Words");

        MatchScorer.Evaluate(Read(BoxTitle), [film], Spine).Ranked.Single().Terms.Single(t => t.Name == "title").Value.ShouldBeGreaterThan(0.95);
    }

    [Fact]
    public void A_candidate_with_no_alternative_titles_scores_exactly_as_it_did_before()
    {
        var film = Film("a", "Highlander III: The Sorcerer", 1994);

        MatchScorer.Evaluate(Read(BoxTitle), [film], Spine).Ranked.Single().Score.ShouldBe(MatchScorer.Score(Read(BoxTitle), film, Spine));
    }

    [Fact]
    public void An_alternative_title_that_is_another_films_main_title_does_not_beat_that_film()
    {
        // The regional title of one film is the main title of another; the read is that other film, exactly.
        var other = Film("other", "Cutthroat Island", 1995, "Pirate Queen");
        var intended = Film("intended", "The Sorcerer", 1994);
        var impostor = Film("impostor", "Highlander III", 1994, "The Sorcerer");

        var result = MatchScorer.Evaluate(Read("The Sorcerer"), [impostor, other, intended], Spine);

        result.Ranked[0].Release.Key.ShouldBe("intended");
        result.Outcome.ShouldNotBe(MatchOutcome.Matched, "the two films tie on the title, so neither has the lead");
    }

    [Fact]
    public void An_alternative_title_that_ties_with_a_main_title_ranks_below_it_whatever_the_input_order()
    {
        var intended = Film("intended", "The Sorcerer", 1994);
        var impostor = Film("impostor", "Highlander III", 1994, "The Sorcerer");

        MatchScorer.Evaluate(Read("The Sorcerer"), [impostor, intended], Spine).Ranked.Select(r => r.Release.Key).ShouldBe(["intended", "impostor"]);
        MatchScorer.Evaluate(Read("The Sorcerer"), [intended, impostor], Spine).Ranked.Select(r => r.Release.Key).ShouldBe(["intended", "impostor"]);
    }

    [Fact]
    public void An_alternative_title_that_scores_better_than_a_main_title_does_win()
    {
        var weak = Film("weak", "The Sorcerer Returns Again", 1994);
        var strong = Film("strong", "Highlander III", 1994, "The Sorcerer");

        MatchScorer.Evaluate(Read("The Sorcerer"), [weak, strong], Spine).Ranked[0].Release.Key.ShouldBe("strong");
    }

    [Fact]
    public void A_blank_alternative_title_is_ignored()
    {
        var film = Film("a", "Highlander III: The Sorcerer", 1994, "", "   ");

        MatchScorer.Evaluate(Read(BoxTitle), [film], Spine).Ranked.Single().Score.ShouldBe(MatchScorer.Score(Read(BoxTitle), film, Spine));
    }
}
