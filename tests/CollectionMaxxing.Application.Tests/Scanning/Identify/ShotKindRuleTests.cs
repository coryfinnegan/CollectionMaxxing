using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;

namespace CollectionMaxxing.Application.Tests.Scanning.Identify;

// A shelf says what it is by the spines that matched: three of them agree on a kind, or the shelf's own print named one. A spine
// that names no kind, found an ambiguous answer, and whose best release is another kind is asked again as the shelf's kind. The
// rule is pure; the kinds are those of the best releases, and the reads are the shapes of a stored eval.
public class ShotKindRuleTests
{
    /// <summary>A kind-less spine that matched a release of the kind given.</summary>
    private static ShotKindRead Matched(WorkKind best) => new(Spine: true, NamesKind: false, ResolutionVerdict.Matched, best);

    /// <summary>A kind-less spine whose answer is ambiguous and whose best release is of the kind given.</summary>
    private static ShotKindRead Ambiguous(WorkKind best) => new(Spine: true, NamesKind: false, ResolutionVerdict.Ambiguous, best);

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
    public void A_matched_spine_of_the_prints_own_family_silences_it_and_the_matched_spines_decide()
    {
        // One matched show shows the engines can tell the print's kind apart on this shelf: the print is left out, and three of
        // the four matched spines (75%) are books.
        ShotKind(WorkKind.Film, [.. Matches(WorkKind.Book, 3), Matched(WorkKind.Tv)]).ShouldBe(WorkKind.Book);
        ShotKind(WorkKind.Film, [.. Matches(WorkKind.Book, 2), Matched(WorkKind.Film)]).ShouldBeNull(); // two books are no shelf
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
        ShotKind(null, [new ShotKindRead(Spine: false, NamesKind: false, ResolutionVerdict.Matched, WorkKind.Book), .. Matches(WorkKind.Book, 2)]).ShouldBeNull(); // a cover is not a spine
        ShotKind(null, [new ShotKindRead(Spine: true, NamesKind: false, ResolutionVerdict.Pending, null), .. Matches(WorkKind.Book, 2)]).ShouldBeNull();
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
        var model = new ShotKindRead(Spine: true, NamesKind: true, ResolutionVerdict.Ambiguous, WorkKind.Film);

        Asked(null, [.. Matches(WorkKind.Book, 3), model])[3].ShouldBeNull();
    }

    [Theory]
    [InlineData(ResolutionVerdict.NotFound)]
    [InlineData(ResolutionVerdict.Pending)]
    public void A_read_that_found_nothing_is_not_asked_again(ResolutionVerdict verdict)
    {
        Asked(null, [.. Matches(WorkKind.Book, 3), new ShotKindRead(Spine: true, NamesKind: false, verdict, null)])[3].ShouldBeNull();
    }

    [Fact]
    public void A_cover_or_a_read_with_a_product_code_is_not_asked_again()
    {
        var cover = new ShotKindRead(Spine: false, NamesKind: false, ResolutionVerdict.Ambiguous, WorkKind.Film);

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
        Asked(null, [.. Matches(WorkKind.Book, 3), new ShotKindRead(Spine: true, NamesKind: false, ResolutionVerdict.Ambiguous, null)])[3].ShouldBeNull();
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
    public void A_find_that_scores_as_well_as_the_reads_best_option_comes_first_and_the_film_stays_pickable()
    {
        Ids(ShotKindRule.Offer([Option(1, 0.9)], [Option(10, 0.9), Option(11, 0.8), Option(12, 0.7)])).ShouldBe(["r1", "r10", "r11"]);
        Ids(ShotKindRule.Offer([Option(1, 0.95)], [Option(10, 0.9), Option(11, 0.8)])).ShouldBe(["r1", "r10", "r11"]);
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
}
