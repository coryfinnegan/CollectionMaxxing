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
            Server(Read("Signal Over Marsh", readKind: WorkKind.Film)),
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

    [Theory]
    [InlineData(ShelfReadStatus.Ok, true)]
    [InlineData(ShelfReadStatus.CutShort, true)]
    [InlineData(ShelfReadStatus.TimedOut, false)]
    [InlineData(ShelfReadStatus.Unavailable, false)]
    [InlineData(ShelfReadStatus.Off, false)]
    public void Only_a_read_that_answered_or_was_cut_short_is_used(ShelfReadStatus status, bool usable) =>
        IsUsable(status).ShouldBe(usable);
}
