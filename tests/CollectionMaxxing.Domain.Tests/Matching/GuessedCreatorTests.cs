using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Domain.Tests.Matching;

// Build-161 eval, shelf-03 "Beer for My Corpses": the Vision piece's title was a neighbour's author, so the creator swap asked the real
// title with that author as the creator. A film with no creators took the neutral creator term (0.5) while the right book was marked
// down for an author nobody has, and the film won. A guessed creator is no evidence against a book: a release with no creators takes no
// better a creator term than the best release that has creators gets. Names are as the eval read them.
public class GuessedCreatorTests
{
    private static readonly ScoringOptions Options = new();

    private static ScoringProfile Spine => Options.Profile(ScoringProfileKind.ShelfSpine);

    private static CandidateRelease Film { get; } = new() { Key = "film", Title = "Beer for My Horses", Kind = WorkKind.Film };

    private static CandidateRelease Book { get; } =
        new() { Key = "book", Title = "Beer For My Corpses", Kind = WorkKind.Book, Creators = ["C. M. McGuire"] };

    private static CandidateRelease LongerEdition { get; } = new()
    {
        Key = "longer",
        Title = "Beer For My Corpses - A Misplaced Adventures Novel",
        Kind = WorkKind.Book,
        Creators = ["C M McGuire"],
    };

    private static CandidateRelease Game { get; } = new() { Key = "game", Title = "Beer for My Pals", Kind = WorkKind.Game, Creators = ["Eden Games"] };

    private static ReadItem Read(bool guess, string creator = "M.L. EADEN") =>
        new() { Title = "BEER FOR MY CORPSES", Creator = creator, CreatorIsAGuess = guess };

    private static ScoredRelease Of(MatchResult result, string key) => result.Ranked.Single(r => r.Release.Key == key);

    private static ScoreTerm CreatorTerm(ScoredRelease scored) => scored.Terms.Single(t => t.Name == "creator");

    private static MatchResult Evaluate(ReadItem read, params CandidateRelease[] pool) => MatchScorer.Evaluate(read, pool, Spine);

    [Fact]
    public void A_guessed_author_nobody_has_gives_a_release_with_no_creators_no_edge_over_the_same_titled_book()
    {
        var guessed = Evaluate(Read(guess: true), Film, Book, LongerEdition, Game);
        var plain = Evaluate(Read(guess: false), Film, Book, LongerEdition, Game);

        guessed.Ranked[0].Release.Key.ShouldBe("book");
        var best = new[] { Book, LongerEdition, Game }.Max(r => CreatorTerm(Of(guessed, r.Key)).Value);
        best.ShouldBeLessThan(0.5);
        CreatorTerm(Of(guessed, "film")).Value.ShouldBe(best);
        CreatorTerm(Of(guessed, "film")).Compared.ShouldBeFalse();
        Of(guessed, "book").Score.ShouldBe(Of(plain, "book").Score);
    }

    [Fact]
    public void Without_the_guess_the_release_with_no_creators_keeps_its_neutral_creator_term()
    {
        var plain = Evaluate(Read(guess: false), Film, Book, LongerEdition, Game);

        plain.Ranked[0].Release.Key.ShouldBe("film");
        plain.Ranked[1].Release.Key.ShouldBe("book");
        CreatorTerm(Of(plain, "film")).Value.ShouldBe(0.5);
    }

    [Fact]
    public void A_guessed_author_that_a_release_has_leaves_a_release_with_no_creators_neutral()
    {
        var guessed = Evaluate(Read(guess: true, creator: "C. M. McGuire"), Film, Book, LongerEdition, Game);
        var plain = Evaluate(Read(guess: false, creator: "C. M. McGuire"), Film, Book, LongerEdition, Game);

        CreatorTerm(Of(guessed, "film")).Value.ShouldBe(0.5);
        guessed.Ranked.Select(r => (r.Release.Key, r.Score)).ShouldBe(plain.Ranked.Select(r => (r.Release.Key, r.Score)));
    }

    [Fact]
    public void A_guessed_author_in_a_run_where_no_release_has_creators_is_no_term_at_all()
    {
        var other = new CandidateRelease { Key = "other", Title = "Beer for My Corpses Too", Kind = WorkKind.Film };

        var guessed = Evaluate(Read(guess: true), Film, other);
        var plain = Evaluate(Read(guess: false), Film, other);

        guessed.Ranked.ShouldAllBe(r => r.Terms.All(t => t.Name != "creator"));
        guessed.Ranked.Select(r => (r.Release.Key, r.Score)).ShouldBe(plain.Ranked.Select(r => (r.Release.Key, r.Score)));
    }

    [Fact]
    public void An_author_printed_in_the_title_counts_as_the_best_creator_term()
    {
        var read = new ReadItem { Title = "BEER FOR MY CORPSES C M MCGUIRE", Creator = "M.L. EADEN", CreatorIsAGuess = true };
        var plain = read with { CreatorIsAGuess = false };

        var guessed = Evaluate(read, Film, Book);
        var unguessed = Evaluate(plain, Film, Book);

        CreatorTerm(Of(guessed, "book")).Value.ShouldBe(1.0);
        CreatorTerm(Of(guessed, "film")).Value.ShouldBe(0.5);
        guessed.Ranked.Select(r => (r.Release.Key, r.Score)).ShouldBe(unguessed.Ranked.Select(r => (r.Release.Key, r.Score)));
    }

    [Fact]
    public void A_release_scored_on_its_own_is_unchanged_by_the_guess()
    {
        MatchScorer.Score(Read(guess: true), Film, Spine).ShouldBe(MatchScorer.Score(Read(guess: false), Film, Spine));
        MatchScorer.Score(Read(guess: true), Book, Spine).ShouldBe(MatchScorer.Score(Read(guess: false), Book, Spine));
    }
}
