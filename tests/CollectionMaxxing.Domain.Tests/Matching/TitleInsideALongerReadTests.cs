using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Domain.Tests.Matching;

// A spine reads the title with other print on the same line: the author's first name and initial in front, a stray pair
// of letters, a sticker, a shelf number. The whole read was compared with the title, so a short title wrapped in other
// print scored under the Ambiguous line and every candidate was dropped. A title a read holds whole is a find.
public class TitleInsideALongerReadTests
{
    private static readonly ScoringOptions Options = new();

    private static ScoringProfile Spine => Options.Profile(ScoringProfileKind.ShelfSpine);

    private static CandidateRelease Book(string key, string title, params string[] creators) =>
        new() { Key = key, Title = title, Kind = WorkKind.Book, Creators = creators };

    [Theory]
    [InlineData("MARA ELLINGSON dune")]  // a name the release does not list, in front
    [InlineData("QX ZV dune")]           // a misread pair of letters
    [InlineData("412 dune")]             // a shelf or sticker number
    [InlineData("dune SIGNED FIRST")]    // a sticker after the title
    [InlineData("dune EX LIBRIS MARA")]  // three words of other print
    public void A_read_that_holds_the_whole_title_offers_the_release(string spine)
    {
        var read = new ReadItem { Title = spine, Kind = WorkKind.Book };

        var result = MatchScorer.Evaluate(read, [Book("k", "Dune")], Spine);

        result.Outcome.ShouldBe(MatchOutcome.Ambiguous);
        result.Ranked[0].Score.ShouldBeGreaterThanOrEqualTo(Options.Thresholds.AmbiguousScore);
    }

    [Fact]
    public void A_read_that_holds_the_whole_title_and_nothing_else_is_offered_without_a_kind()
    {
        // A read that names no kind has the title as its only term, so the find itself must clear the Ambiguous line.
        var read = new ReadItem { Title = "dune EX LIBRIS MARA" };

        MatchScorer.Evaluate(read, [Book("k", "Dune")], Spine).Outcome.ShouldBe(MatchOutcome.Ambiguous);
    }

    [Fact]
    public void The_longest_title_the_read_holds_ranks_first()
    {
        var read = new ReadItem { Title = "ANNE B dune messiah", Kind = WorkKind.Book };

        var result = MatchScorer.Evaluate(read, [Book("dune", "Dune"), Book("messiah", "Dune Messiah")], Spine);

        result.Ranked[0].Release.Key.ShouldBe("messiah");
        result.Ranked[0].Score.ShouldBeGreaterThan(result.Ranked[1].Score);
    }

    [Fact]
    public void A_title_found_inside_a_longer_read_is_never_added_on_its_own()
    {
        var read = new ReadItem { Title = "MARA ELLINGSON dune", Kind = WorkKind.Book };

        MatchScorer.Evaluate(read, [Book("k", "Dune")], Spine).Outcome.ShouldBe(MatchOutcome.Ambiguous);
    }

    [Fact]
    public void Signals_that_agree_raise_a_find_inside_a_longer_read()
    {
        var titleAlone = new ReadItem { Title = "MARA ELLINGSON harbor light", Kind = WorkKind.Book };
        var withAuthor = new ReadItem { Title = "MARA ELLINGSON harbor light", Kind = WorkKind.Book, Creator = "Mara Odell", Year = 2010 };
        var release = new CandidateRelease
        {
            Key = "k",
            Title = "Harbor Light",
            Kind = WorkKind.Book,
            Year = 2010,
            Creators = ["Mara Odell"],
        };

        MatchScorer.Score(withAuthor, release, Spine).ShouldBeGreaterThan(MatchScorer.Score(titleAlone, release, Spine));
    }

    [Fact]
    public void Other_print_too_long_to_be_a_sticker_is_not_a_find()
    {
        var read = new ReadItem { Title = "dune road ex libris mara ellingson", Kind = WorkKind.Book };

        MatchScorer.Evaluate(read, [Book("k", "Dune")], Spine).Outcome.ShouldBe(MatchOutcome.NotFound);
    }

    [Fact]
    public void Words_that_are_there_but_not_standing_together_are_not_a_find()
    {
        var together = TitleNormalizer.TitleSimilarity("MARA ELLINGSON harbor light keepers", "Harbor Light Keepers");
        var apart = TitleNormalizer.TitleSimilarity("MARA harbor ELLINGSON light keepers", "Harbor Light Keepers");

        apart.ShouldBeLessThan(together);
    }

    [Fact]
    public void A_find_never_outranks_a_candidate_whose_whole_title_nearly_matches()
    {
        var read = new ReadItem { Title = "THE KITCHEN ORCHARD", Kind = WorkKind.Book };

        var result = MatchScorer.Evaluate(
            read, [Book("orchard", "Orchard"), Book("recipes", "Orchard Kitchen: Recipes for a Small Plot")], Spine);

        result.Ranked[0].Release.Key.ShouldBe("recipes");
    }

    [Fact]
    public void A_title_too_short_to_tell_items_apart_is_not_found_inside_a_read()
    {
        // "Up" inside "lantern up the hill" would make every long read an Up.
        TitleNormalizer.TitleSimilarity("lantern up the hill", "Up").ShouldBeLessThan(Options.Thresholds.AmbiguousScore);
    }

    [Fact]
    public void A_number_left_standing_after_the_title_is_a_sequel_number_not_a_sticker()
    {
        TitleNormalizer.TitleSimilarity("harbor light 2", "Harbor Light").ShouldBeLessThan(0.7);
    }

    [Fact]
    public void A_read_the_release_title_holds_is_not_a_find_the_other_way_round()
    {
        // The release title is the longer one here: that is the missing-subtitle case, which keeps its own credit.
        TitleNormalizer.TitleSimilarity("Dune", "Dune Messiah").ShouldBeLessThan(TitleNormalizer.ContainedTitleCredit);
    }

    [Theory]
    [InlineData("FRANKLIN dune", 0)]
    [InlineData("FRANKLIN ODELLSON dune", 1)]
    [InlineData("FRANKLIN ODELLSON ELLINGSON dune", 2)]
    public void Each_word_of_other_print_past_the_first_costs_the_find_a_little(string spine, int past)
    {
        TitleNormalizer.TitleSimilarity(spine, "Dune")
            .ShouldBe(TitleNormalizer.ContainedTitleCredit - (TitleNormalizer.ExtraPrintCost * past));
    }

    [Fact]
    public void A_find_inside_a_longer_read_is_worth_less_than_a_release_main_title()
    {
        TitleNormalizer.TitleSimilarity("FRANKLIN ODELLSON dune", "Dune").ShouldBeLessThan(TitleNormalizer.MainTitleCredit);
    }

    [Fact]
    public void The_plainer_comparison_still_forgives_nothing_it_did_not_before()
    {
        // Containment belongs to the title comparison only: the plainer Similarity, which the search ladder and the
        // creator term use, must not start calling a one-word answer close to a three-word query.
        TitleNormalizer.Similarity("FRANKLIN ODELLSON dune", "Dune").ShouldBeLessThan(Options.Thresholds.AmbiguousScore);
    }
}
