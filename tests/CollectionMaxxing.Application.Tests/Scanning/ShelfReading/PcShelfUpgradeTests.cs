using CollectionMaxxing.Application.Scanning.ShelfReading;
using CollectionMaxxing.Application.ShelfReading;
using CollectionMaxxing.Domain.Catalog;
using static CollectionMaxxing.Application.Scanning.ShelfReading.PcShelfUpgrade;

namespace CollectionMaxxing.Application.Tests.Scanning.ShelfReading;

// The server-first rule (#814): the server's reads all stand, and a Vision read stands beside them only when it found a release, has two
// title words, is of a kind the server read on the shelf, and is not the same item as a server read. Made-up titles throughout.
public class PcShelfUpgradeTests
{
    private static ShelfRead Read(string readAs, string? best = null, int? year = 2001, WorkKind? kind = WorkKind.Book, WorkKind? readKind = null) =>
        new(readAs, readKind, best, best is null ? null : year, best is null ? null : kind);

    private static ShelfRead Found(string title, WorkKind kind = WorkKind.Book, int? year = 2001) => Read(title, title, year, kind);

    private static ShelfRead[] Server(params ShelfRead[] reads) => reads;

    /// <summary>The places for donors that have nothing else at their place.</summary>
    private static IReadOnlyList<int?> Places(ShelfRead[] server, ShelfRead[] donors) => PlacesOf(server, [.. donors.Select(read => new PlaceDonor(read, []))]);

    [Fact]
    public void Every_server_read_stands_whatever_it_found()
    {
        var decision = Decide(Server(Found("Quill Harbor Tides"), Read("Zebra"), Read("Ashen Lantern Row")), []);

        decision.ServerConfirmed.Count.ShouldBe(3, "one answer per server read: none is dropped");
        decision.VisionKept.ShouldBeEmpty();
    }

    [Fact]
    public void A_vision_read_of_another_book_with_two_words_stands()
    {
        var decision = Decide(Server(Found("Quill Harbor Tides")), [Found("Copper Meadow Song")]);

        decision.VisionKept.ShouldBe([true]);
    }

    [Fact]
    public void A_vision_read_that_found_no_release_is_left_out()
    {
        var decision = Decide(Server(Found("Quill Harbor Tides")), [Read("Copper Meadow Song"), Found("Velvet Orchard Road")]);

        decision.VisionKept.ShouldBe([false, true]);
    }

    [Fact]
    public void A_vision_read_of_one_title_word_is_left_out_and_stop_words_and_single_letters_do_not_count()
    {
        var decision = Decide(
            Server(Found("Quill Harbor Tides")),
            [Read("Copper", "Copper Meadow Song"), Read("The Copper A", "Copper Meadow Song"), Read("Copper Meadow", "Copper Meadow Song")]);

        decision.VisionKept.ShouldBe([false, false, true]);
    }

    [Fact]
    public void A_vision_read_of_a_kind_no_server_read_names_is_left_out()
    {
        var decision = Decide(Server(Found("Quill Harbor Tides")), [Found("Copper Meadow Song", WorkKind.Game), Found("Velvet Orchard Road", WorkKind.Book)]);

        decision.VisionKept.ShouldBe([false, true]);
    }

    [Fact]
    public void A_film_and_a_tv_show_are_one_family_and_a_server_read_names_its_family_by_the_kind_it_was_read_as_when_it_found_nothing()
    {
        var decision = Decide(
            Server(Read("Signal Over Marsh", readKind: WorkKind.Film), Found("Pebble Kart Rally", WorkKind.Game)),
            [Found("Copper Meadow Song", WorkKind.Tv), Found("Velvet Orchard Road", WorkKind.Film), Found("Ashen Lantern Row", WorkKind.Book)]);

        decision.VisionKept.ShouldBe([true, true, false]);
    }

    [Fact]
    public void A_server_read_names_its_family_by_its_release_before_the_kind_it_was_read_as()
    {
        var decision = Decide(
            Server(Read("Quill Harbor Tides", "Quill Harbor Tides", kind: WorkKind.Game, readKind: WorkKind.Book)),
            [Found("Copper Meadow Song", WorkKind.Book), Found("Velvet Orchard Road", WorkKind.Game)]);

        decision.VisionKept.ShouldBe([false, true]);
    }

    [Fact]
    public void A_vision_read_whose_release_has_the_title_of_a_server_reads_release_is_the_same_item_in_any_year()
    {
        var decision = Decide(Server(Found("Quill Harbor Tides", year: 2001)), [Read("Harbor Tidings Quarterly", "Quill Harbor Tides", year: 1987)]);

        decision.VisionKept.ShouldBe([false]);
    }

    [Fact]
    public void Two_thirds_of_a_vision_reads_words_among_a_server_reads_words_is_the_same_item()
    {
        var decision = Decide(
            Server(Read("Quill Harbor", "Tides of Quill")),
            [
                Read("Quill Harbor Lantern", "Lantern Shelf Almanac"), // 2 of 3 words: quill, harbor
                Read("Tides Copper", "Copper Bay Almanac"), // 1 of 2: tides is the server's best title, copper is not
                Read("Harbor Tides Quill", "Bay Atlas Volume"), // 3 of 3, from the read title and the best title together
            ]);

        decision.VisionKept.ShouldBe([false, true, false]);
    }

    [Fact]
    public void A_vision_read_whose_words_are_only_in_a_server_reads_best_title_is_the_same_item()
    {
        var decision = Decide(Server(Read("Tidings", "Quill Harbor Tides")), [Read("Quill Harbor", "Lantern Shelf Almanac")]);

        decision.VisionKept.ShouldBe([false], "quill and harbor are words of the server read's release, not of what it read");
    }

    [Fact]
    public void A_server_read_with_no_reads_leaves_the_phone_as_it_is()
    {
        var decision = Decide([], [Read("Copper"), Found("Velvet Orchard Road")]);

        decision.PhoneStands.ShouldBeTrue();
        decision.VisionKept.ShouldBe([true, true]);
    }

    [Fact]
    public void A_server_read_that_named_no_release_leaves_the_phone_as_it_is_and_its_reads_still_stand()
    {
        var decision = Decide(Server(Read("Zebra Opus"), Read("Ashen Lantern Row")), [Read("Copper", "Copper Meadow Song"), Found("Velvet Orchard Road", WorkKind.Game)]);

        decision.PhoneStands.ShouldBeTrue();
        decision.ServerConfirmed.ShouldBe([false, false]);
        decision.VisionKept.ShouldBe([true, true]);
    }

    [Fact]
    public void One_server_read_that_named_a_release_is_evidence_enough()
    {
        var decision = Decide(Server(Read("Zebra Opus"), Found("Quill Harbor Tides")), [Read("Copper", "Copper Meadow Song")]);

        decision.PhoneStands.ShouldBeFalse();
        decision.VisionKept.ShouldBe([false]);
    }

    [Fact]
    public void A_server_read_is_confirmed_only_when_a_vision_read_names_the_same_release_title_and_year()
    {
        var decision = Decide(
            Server(Found("Quill Harbor Tides", year: 2001), Found("Copper Meadow Song", year: 2001), Found("Velvet Orchard Road")),
            [Found("Quill Harbor Tides", year: 2001), Found("Copper Meadow Song", year: 1999)]);

        decision.ServerConfirmed.ShouldBe([true, false, false]);
    }

    [Fact]
    public void A_server_read_that_found_nothing_is_never_confirmed()
    {
        var decision = Decide(Server(Read("Quill Harbor Tides")), [Read("Quill Harbor Tides")]);

        decision.ServerConfirmed.ShouldBe([false]);
    }

    [Fact]
    public void A_server_read_is_placed_at_the_phone_read_of_the_same_title()
    {
        Places([Found("Quill Harbor Tides")], [Found("Copper Meadow Song"), Found("Quill Harbor Tides")]).ShouldBe([1]);
    }

    [Fact]
    public void A_server_read_is_placed_at_a_fragment_that_shares_two_of_its_words()
    {
        Places([Found("Quill Harbor Tides")], [Read("Quill Harbor")]).ShouldBe([0]);
    }

    [Fact]
    public void The_donor_sharing_the_most_words_wins_and_a_tie_goes_to_the_earlier_donor()
    {
        var server = Found("Quill Harbor Tides Almanac");

        Places([server], [Read("Quill Harbor Tides"), Read("Quill Harbor Tides Almanac")]).ShouldBe([1], "all of its words are the server's");
        Places([server], [Read("Harbor Tides Almanac"), Read("Quill Harbor Tides")]).ShouldBe([0], "equal shares: the earlier donor");
    }

    [Fact]
    public void A_donor_is_used_once_and_goes_to_the_server_read_it_shares_most_with_even_when_that_read_comes_later()
    {
        // Share with "Philosopher's Stone" is lower than with "Chamber Of Secrets": the best pair goes first, not the first server read.
        var places = Places([Found("Harry Potter Philosopher Stone Saga"), Found("Harry Potter Chamber")], [Read("Harry Potter Chamber")]);

        places.ShouldBe([null, 0]);
    }

    [Fact]
    public void A_donor_whose_two_best_server_reads_are_close_lends_to_neither()
    {
        // Shares 0.8 and 0.8 (a tie) lend to neither; shares 1.0 and 0.67 are far enough apart for the closer one to take it.
        Places([Found("Quill Harbor Tides"), Found("Quill Harbor Redux")], [Read("Quill Harbor")]).ShouldBe([null, null]);
        Places([Found("Quill Harbor"), Found("Quill Harbor Tides Redux")], [Read("Quill Harbor")]).ShouldBe([0, null]);
    }

    [Fact]
    public void A_server_read_of_one_word_or_one_shared_word_has_no_place()
    {
        Places([Read("Zebra")], [Read("Zebra")]).ShouldBe([null], "a one-word title");
        Places([Found("Quill Harbor Tides")], [Read("Tides")]).ShouldBe([null], "one word in common");
        Places([Found("Quill Harbor Tides")], [Read("Copper Meadow Song")]).ShouldBe([null], "no word in common");
        Places([Found("Quill Harbor Tides")], []).ShouldBe([null], "nothing to place it at");
    }

    [Fact]
    public void A_fragment_does_not_lend_to_a_server_read_when_a_longer_read_at_its_place_names_another_book()
    {
        var full = Read("Lord Rings Two Towers", "Lord Rings Two Towers");
        var fragment = Read("Lord Rings");
        var returnOfTheKing = Found("Lord Rings Return King");

        PlacesOf([returnOfTheKing], [new PlaceDonor(fragment, [full])]).ShouldBe([null], "the longer read at that place is another book");
        PlacesOf([returnOfTheKing], [new PlaceDonor(fragment, [])]).ShouldBe([0], "alone at its place, the fragment is the place of the title that replaces it");
        PlacesOf([returnOfTheKing], [new PlaceDonor(fragment, [Read("Lord")])]).ShouldBe([0], "a one-word read at the place is no evidence");
        PlacesOf([returnOfTheKing], [new PlaceDonor(fragment, [Read("Lord Rings Return King")])]).ShouldBe([0], "a read at the place that agrees does not block");
    }

    [Fact]
    public void A_one_word_title_named_by_both_best_releases_has_no_place()
    {
        Places([Found("Dune")], [Found("Dune")]).ShouldBe([null], "equal best titles of one word are not a place");
        Places([Found("Dune Messiah")], [Found("Dune Messiah")]).ShouldBe([0], "equal best titles of two words are");
    }

    [Theory]
    [InlineData(ShelfReadStatus.Ok, true)]
    [InlineData(ShelfReadStatus.CutShort, true)]
    [InlineData(ShelfReadStatus.TimedOut, false)]
    [InlineData(ShelfReadStatus.Unavailable, false)]
    [InlineData(ShelfReadStatus.Off, false)]
    public void Only_a_read_that_answered_or_was_cut_short_is_used(ShelfReadStatus status, bool usable) =>
        IsUsable(status).ShouldBe(usable);
}
