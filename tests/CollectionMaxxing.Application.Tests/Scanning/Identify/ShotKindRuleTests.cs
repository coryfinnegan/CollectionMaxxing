using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Application.Tests.Scanning.Identify;

// A shelf says what it is by the spines that matched: three of them agree on a kind, or the shelf's own print named one. A spine
// that names no kind, found an ambiguous answer, and whose best release is another kind is asked again as the shelf's kind. The
// rule is pure; the kinds are those of the best releases, and the reads are the shapes of a stored eval.
public class ShotKindRuleTests
{
    /// <summary>A kind-less spine that matched a release of the kind given.</summary>
    private static ShotKindRead Matched(WorkKind best) => new(Spine: true, OwnKind: null, ResolutionVerdict.Matched, best);

    /// <summary>A kind-less spine whose answer is ambiguous and whose best release is of the kind given.</summary>
    private static ShotKindRead Ambiguous(WorkKind best) => new(Spine: true, OwnKind: null, ResolutionVerdict.Ambiguous, best);

    private static ShotKindRead[] Matches(WorkKind kind, int count) => [.. Enumerable.Range(0, count).Select(_ => Matched(kind))];

    private static WorkKind? ShotKind(WorkKind? print, params ShotKindRead[] reads) => ShotKindRule.Of(print, reads);

    private static IReadOnlyList<WorkKind?> Asked(WorkKind? print, params ShotKindRead[] reads) => ShotKindRule.AskKinds(print, reads);

    // ---- the shot's kind ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Three_matched_book_spines_and_one_matched_film_make_a_shelf_of_books()
    {
        ShotKind(null, [.. Matches(WorkKind.Book, 3), Matched(WorkKind.Film)]).ShouldBe(WorkKind.Book);
    }

    [Fact]
    public void Two_matched_spines_are_not_a_shelf()
    {
        ShotKind(null, Matches(WorkKind.Book, 2)).ShouldBeNull();
        ShotKind(null).ShouldBeNull();
    }

    [Fact]
    public void A_split_shelf_has_no_kind()
    {
        ShotKind(null, [.. Matches(WorkKind.Book, 3), .. Matches(WorkKind.Film, 3)]).ShouldBeNull();
    }

    [Fact]
    public void The_print_of_a_shelf_with_no_matched_spine_is_its_kind()
    {
        ShotKind(WorkKind.Book).ShouldBe(WorkKind.Book);
        ShotKind(WorkKind.Film, Ambiguous(WorkKind.Book)).ShouldBe(WorkKind.Film);
    }

    [Fact]
    public void A_print_of_film_with_three_matched_books_is_still_a_shelf_of_films()
    {
        // The rule KindContext already has, pinned: the print is silenced only by a matched spine of its own family, and no
        // matched release here is a film. On the stored shelves this gives the same kinds as "the matched spines first, else the print".
        ShotKind(WorkKind.Film, Matches(WorkKind.Book, 3)).ShouldBe(WorkKind.Film);
    }

    [Fact]
    public void A_matched_spine_of_the_prints_own_family_leaves_the_print_silent_and_agreeing_matched_spines_decide()
    {
        // One matched show shows the engines can tell the print's kind apart on this shelf: the print is left out, and three of
        // the four matched spines (75%) are books.
        ShotKind(WorkKind.Film, [.. Matches(WorkKind.Book, 3), Matched(WorkKind.Tv)]).ShouldBe(WorkKind.Book);
    }

    [Fact]
    public void Matched_spines_that_split_with_no_majority_take_the_prints_kind_when_one_is_of_its_family()
    {
        // Two books and a film is 2/3, under the 70% share: the matched spines split, and the print breaks the tie they could not.
        ShotKind(WorkKind.Film, [.. Matches(WorkKind.Book, 2), Matched(WorkKind.Film)]).ShouldBe(WorkKind.Film);
        ShotKind(null, [.. Matches(WorkKind.Book, 2), Matched(WorkKind.Film)]).ShouldBeNull(); // with no print there is no tie-break
    }

    [Fact]
    public void A_read_that_names_a_kind_says_it_whatever_it_matched()
    {
        ShotKindRead Model(WorkKind own) => new(Spine: true, OwnKind: own, ResolutionVerdict.Ambiguous, WorkKind.Film);

        ShotKind(null, Model(WorkKind.Book), Model(WorkKind.Book), Model(WorkKind.Book)).ShouldBe(WorkKind.Book);
        ShotKind(null, Model(WorkKind.Other), Model(WorkKind.Other), Model(WorkKind.Other)).ShouldBeNull(); // the reader does not know
    }

    [Fact]
    public void A_read_that_names_the_prints_kind_silences_the_print_so_the_other_reads_decide()
    {
        // One film read among four books: the engines can tell a film from a book on this photo, so the print of film has nothing to add.
        ShotKindRead Model(WorkKind own) => new(Spine: true, OwnKind: own, ResolutionVerdict.Ambiguous, WorkKind.Book);

        ShotKind(WorkKind.Film, [.. Enumerable.Range(0, 4).Select(_ => Model(WorkKind.Book)), Model(WorkKind.Film), Ambiguous(WorkKind.Book)]).ShouldBe(WorkKind.Book);
        Asked(WorkKind.Film, [.. Enumerable.Range(0, 4).Select(_ => Model(WorkKind.Book)), Model(WorkKind.Film), Ambiguous(WorkKind.Book)]).ShouldAllBe(kind => kind == null);
    }

    [Fact]
    public void A_print_of_other_says_nothing()
    {
        ShotKind(WorkKind.Other).ShouldBeNull();
    }

    [Fact]
    public void Only_matched_spines_count_for_the_shots_kind()
    {
        // The Vision reads of a shelf of tapes ground to books at 0.9 and over: a high score is not the signal, a Matched verdict is.
        ShotKind(null, [Ambiguous(WorkKind.Book), Ambiguous(WorkKind.Book), Ambiguous(WorkKind.Book), Ambiguous(WorkKind.Book)]).ShouldBeNull();
        ShotKind(null, [new ShotKindRead(Spine: false, OwnKind: null, ResolutionVerdict.Matched, WorkKind.Book), .. Matches(WorkKind.Book, 2)]).ShouldBeNull(); // a cover is not a spine
        ShotKind(null, [new ShotKindRead(Spine: true, OwnKind: null, ResolutionVerdict.Pending, null), .. Matches(WorkKind.Book, 2)]).ShouldBeNull();
    }

    [Fact]
    public void A_film_and_a_show_are_one_family()
    {
        ShotKind(null, Matched(WorkKind.Film), Matched(WorkKind.Tv), Matched(WorkKind.Film)).ShouldBe(WorkKind.Film);
    }

    // ---- which reads are asked again ----------------------------------------------------------------------------------------------

    [Fact]
    public void An_ambiguous_spine_with_no_kind_whose_best_release_is_a_film_is_asked_again_as_a_book()
    {
        Asked(null, [.. Matches(WorkKind.Book, 3), Ambiguous(WorkKind.Film)]).ShouldBe([null, null, null, WorkKind.Book]);
    }

    [Fact]
    public void A_matched_spine_is_never_asked_again()
    {
        Asked(null, [.. Matches(WorkKind.Book, 3), Matched(WorkKind.Film)]).ShouldAllBe(kind => kind == null);
    }

    [Fact]
    public void A_read_with_its_own_kind_is_not_asked_again()
    {
        var model = new ShotKindRead(Spine: true, OwnKind: WorkKind.Film, ResolutionVerdict.Ambiguous, WorkKind.Film);

        Asked(null, [.. Matches(WorkKind.Book, 3), model])[3].ShouldBeNull();
    }

    [Theory]
    [InlineData(ResolutionVerdict.NotFound)]
    [InlineData(ResolutionVerdict.Pending)]
    public void A_read_that_found_nothing_is_not_asked_again(ResolutionVerdict verdict)
    {
        Asked(null, [.. Matches(WorkKind.Book, 3), new ShotKindRead(Spine: true, OwnKind: null, verdict, null)])[3].ShouldBeNull();
    }

    [Fact]
    public void A_cover_or_a_read_with_a_product_code_is_not_asked_again()
    {
        var cover = new ShotKindRead(Spine: false, OwnKind: null, ResolutionVerdict.Ambiguous, WorkKind.Film);

        Asked(null, [.. Matches(WorkKind.Book, 3), cover])[3].ShouldBeNull();
    }

    [Fact]
    public void A_read_whose_best_release_is_already_of_the_shots_kind_is_not_asked_again()
    {
        Asked(null, [.. Matches(WorkKind.Book, 3), Ambiguous(WorkKind.Book)])[3].ShouldBeNull();
    }

    [Fact]
    public void A_show_is_not_another_kind_than_a_film_shelf()
    {
        Asked(WorkKind.Film, Ambiguous(WorkKind.Tv)).ShouldBe([null]);
    }

    [Fact]
    public void A_best_release_of_unknown_kind_gives_no_reason_to_ask()
    {
        Asked(null, [.. Matches(WorkKind.Book, 3), Ambiguous(WorkKind.Other)])[3].ShouldBeNull();
        Asked(null, [.. Matches(WorkKind.Book, 3), new ShotKindRead(Spine: true, OwnKind: null, ResolutionVerdict.Ambiguous, null)])[3].ShouldBeNull();
    }

    [Fact]
    public void A_shot_with_no_kind_asks_nothing()
    {
        Asked(null, [.. Matches(WorkKind.Book, 2), Ambiguous(WorkKind.Film)]).ShouldAllBe(kind => kind == null);
    }

    [Fact]
    public void The_print_alone_is_enough_to_ask()
    {
        Asked(WorkKind.Book, Ambiguous(WorkKind.Film)).ShouldBe([WorkKind.Book]);
    }

    // ---- what is offered ----------------------------------------------------------------------------------------------------------

    private static ResolutionOption Option(int n, double score) => new(
        null,
        new ProviderRelease { Provider = "scripted", ExternalId = $"r{n}", Title = $"Title {n}", Kind = WorkKind.Book },
        score);

    private static string[] Ids(IEnumerable<ResolutionOption> options) => [.. options.Select(o => o.Provider!.ExternalId)];

    [Fact]
    public void A_find_that_scores_strictly_better_than_the_reads_best_option_comes_first_and_the_film_stays_pickable()
    {
        Ids(ShotKindRule.Offer([Option(1, 0.95)], [Option(10, 0.9), Option(11, 0.8)])).ShouldBe(["r1", "r10", "r11"]);
        Ids(ShotKindRule.Offer([Option(1, 0.91)], [Option(10, 0.9), Option(11, 0.8), Option(12, 0.7)])).ShouldBe(["r1", "r10", "r11"]);
    }

    [Fact]
    public void A_find_that_only_ties_the_reads_best_option_comes_after_it()
    {
        Ids(ShotKindRule.Offer([Option(1, 0.9)], [Option(10, 0.9), Option(11, 0.8), Option(12, 0.7)])).ShouldBe(["r10", "r1", "r11"]);
    }

    [Fact]
    public void A_find_is_scored_without_its_type_term_and_so_compared_with_the_reads_best_option_on_the_same_footing()
    {
        // The ask scored the book 0.875 with the type term; without it the book is 0.85, the film's own score: a tie, so the film stays first.
        ResolutionOption Scored(int n, double score, params ScoreTerm[] terms) => Option(n, score) with { Terms = terms };
        var book = Scored(1, 0.875, new ScoreTerm("title", 0.5, 0.85, true), new ScoreTerm("type", 0.1, 1.0, true));
        var stronger = Scored(2, 0.95, new ScoreTerm("title", 0.5, 0.9, true), new ScoreTerm("type", 0.1, 1.0, true));

        var footing = ShotKindRule.OnOriginalFooting(book);

        footing.Score.ShouldBe(0.85, 1e-9);
        footing.Terms.Select(term => term.Name).ShouldBe(["title"]);
        footing.Provider.ShouldBeSameAs(book.Provider);
        Ids(ShotKindRule.Offer([footing], [Option(10, 0.85)])).ShouldBe(["r10", "r1"]);
        Ids(ShotKindRule.Offer([ShotKindRule.OnOriginalFooting(stronger)], [Option(10, 0.85)])).ShouldBe(["r2", "r10"]);
    }

    [Fact]
    public void The_other_terms_keep_their_weight_when_the_type_term_is_taken_off()
    {
        var book = Option(1, ((0.5 * 0.8) + 0.1 + 0.15) / 0.75) with
        {
            Terms = [new ScoreTerm("title", 0.5, 0.8, true), new ScoreTerm("type", 0.1, 1.0, true), new ScoreTerm("creator", 0.15, 1.0, true)],
        };

        ShotKindRule.OnOriginalFooting(book).Score.ShouldBe(((0.5 * 0.8) + 0.15) / 0.65, 1e-9);
    }

    [Fact]
    public void An_option_the_scorer_capped_for_a_known_conflict_stays_capped_after_the_rescore()
    {
        // Every term agrees (1.0), so the terms alone would score 1.0 without the type term; the scorer capped the option at 0.85
        // (a platform or title-year conflict), and the rescored option can never exceed that.
        var capped = Option(1, 0.85) with
        {
            Terms = [new ScoreTerm("title", 0.5, 1.0, true), new ScoreTerm("type", 0.1, 1.0, true)],
        };

        ShotKindRule.OnOriginalFooting(capped).Score.ShouldBe(0.85);
    }

    [Fact]
    public void An_option_with_no_terms_is_left_as_it_is()
    {
        var option = Option(1, 0.7);

        ShotKindRule.OnOriginalFooting(option).ShouldBeSameAs(option);
    }

    [Fact]
    public void A_find_that_scores_worse_than_the_reads_best_option_comes_after_it()
    {
        Ids(ShotKindRule.Offer([Option(1, 0.7)], [Option(10, 0.9), Option(11, 0.8), Option(12, 0.7)])).ShouldBe(["r10", "r1", "r11"]);
        Ids(ShotKindRule.Offer([Option(1, 0.7), Option(2, 0.65)], [Option(10, 0.9), Option(11, 0.8)])).ShouldBe(["r10", "r1", "r2"]);
    }

    [Fact]
    public void The_offer_is_never_more_than_three_and_always_keeps_one_of_the_reads_own()
    {
        Ids(ShotKindRule.Offer([Option(1, 0.95), Option(2, 0.94), Option(3, 0.93)], [Option(10, 0.9), Option(11, 0.8)])).ShouldBe(["r1", "r2", "r10"]);
        Ids(ShotKindRule.Offer([Option(1, 0.95)], [])).ShouldBe(["r1"]);
    }

    [Fact]
    public void A_release_the_read_already_had_is_offered_once()
    {
        Ids(ShotKindRule.Offer([Option(1, 0.9), Option(2, 0.8)], [Option(10, 0.85), Option(2, 0.7)])).ShouldBe(["r1", "r2", "r10"]);
        Ids(ShotKindRule.Offer([Option(1, 0.7), Option(2, 0.65)], [Option(10, 0.9), Option(2, 0.5)])).ShouldBe(["r10", "r1", "r2"]);
    }

    // ---- the decision of 2026-11-05: the same-titled release of the shot's kind comes first ----------------------------------------

    private static ResolutionOption Titled(string id, string title, WorkKind kind, double score) => new(
        null, new ProviderRelease { Provider = "scripted", ExternalId = id, Title = title, Kind = kind }, score);

    private static (string Title, WorkKind Kind)? Describe(ResolutionOption option) =>
        option.Provider is { } provider ? (provider.Title, provider.Kind) : null;

    private static string[] SameTitledFirst(WorkKind shotKind, params ResolutionOption[] options) =>
        Ids(ShotKindRule.SameTitledOfKindFirst(options, Describe, shotKind));

    [Fact]
    public void A_same_titled_book_comes_before_the_film_on_a_shelf_of_books()
    {
        SameTitledFirst(WorkKind.Book, Titled("f1", "The Quiet Harbor", WorkKind.Film, 0.9), Titled("b1", "The Quiet Harbor", WorkKind.Book, 0.6))
            .ShouldBe(["b1", "f1"]);
    }

    [Fact]
    public void A_same_titled_film_comes_before_the_book_on_a_shelf_of_tapes()
    {
        SameTitledFirst(
            WorkKind.Film,
            Titled("b1", "The Quiet Harbor", WorkKind.Book, 0.9),
            Titled("x1", "Another Name", WorkKind.Book, 0.8),
            Titled("f1", "the quiet harbor!", WorkKind.Film, 0.5)).ShouldBe(["f1", "b1", "x1"]);
    }

    [Fact]
    public void A_differently_titled_release_of_the_shots_kind_does_not_jump_a_better_one()
    {
        SameTitledFirst(WorkKind.Book, Titled("f1", "The Quiet Harbor", WorkKind.Film, 0.9), Titled("b1", "The Loud Harbor", WorkKind.Book, 0.95))
            .ShouldBe(["f1", "b1"]);
    }

    [Fact]
    public void A_best_option_already_of_the_shots_kind_is_not_reordered()
    {
        SameTitledFirst(WorkKind.Book, Titled("b1", "The Quiet Harbor", WorkKind.Book, 0.9), Titled("b2", "The Quiet Harbor", WorkKind.Book, 0.95))
            .ShouldBe(["b1", "b2"]);
    }

    [Fact]
    public void An_option_whose_title_or_kind_is_unknown_does_not_move()
    {
        var unknown = new ResolutionOption(Guid.NewGuid(), null, 0.6);
        var options = new[] { Titled("f1", "The Quiet Harbor", WorkKind.Film, 0.9), unknown };

        ShotKindRule.SameTitledOfKindFirst(options, Describe, WorkKind.Book).ShouldBe(options);
    }

    [Fact]
    public void A_read_that_names_its_own_kind_is_not_reordered()
    {
        // Only a read asked again is reordered (Asks), and a read that names its own kind is never asked again.
        ShotKindRule.Asks(new ShotKindRead(Spine: true, OwnKind: WorkKind.Film, ResolutionVerdict.Ambiguous, WorkKind.Film), WorkKind.Book).ShouldBeFalse();
        ShotKindRule.Asks(new ShotKindRead(Spine: true, OwnKind: WorkKind.Book, ResolutionVerdict.Ambiguous, WorkKind.Film), WorkKind.Book).ShouldBeFalse();
    }

    [Theory]
    [InlineData("cnoi Emergency Contact o", "Emergency Contact", "Emergency Contact")]
    [InlineData("Emergency Contact", "Emergency Contact", null)]
    [InlineData("EMERGENCY  CONTACT", "Emergency Contact", null)] // the same title once normalised
    [InlineData("cnoi Emergency Contact o", null, null)]
    [InlineData("cnoi Emergency Contact o", "  ", null)]
    public void The_best_options_title_is_asked_only_when_it_differs_from_the_reads_own_title(string read, string? best, string? expected) =>
        ShotKindRule.AskTitle(read, best).ShouldBe(expected);

    [Fact]
    public void A_mixed_shelf_has_no_shot_kind_so_nothing_is_asked_or_moved()
    {
        Asked(null, [Matched(WorkKind.Book), Matched(WorkKind.Film), Matched(WorkKind.Book), Matched(WorkKind.Film), Ambiguous(WorkKind.Book)])
            .ShouldAllBe(kind => kind == null);
    }
}
