using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.TestSupport.Evals;
using static CollectionMaxxing.Recognition.Evals.EvalFixtures;

namespace CollectionMaxxing.Recognition.Evals;

/// <summary>
/// The <c>server_vision</c> row (<see cref="EvalCombination.OfServerRead"/>) under the server-first rule (#814): the server's items all stand, in its
/// order, then the Vision items the rule keeps; the phone model's items are left out. Made-up titles throughout.
/// </summary>
public sealed class ServerFirstRowTests : IDisposable
{
    private const string ServerModel = ServerEngines.Model;

    private readonly SyntheticGolden _golden = new();

    public void Dispose() => _golden.Dispose();

    private IReadOnlyDictionary<string, TruthEntry> Truths(string set = "shelf")
    {
        if (set == "shelf")
        {
            _golden.AddShelf("s1", new[] { "Quill Harbor Tides", "Copper Meadow Song" }.Select(t => ShelfItem(t, "book", null)));
        }
        else
        {
            _golden.AddSingle("s1", new SyntheticGolden.SingleItem("Quill Harbor Tides", "paperback", "n_a", EvalKinds.BookSpine));
        }

        return TruthsOf(_golden);
    }

    private static GroundedItem Book(string title, string readAs = "") => T1(Matched(title, platform: null, readAs: readAs, kind: "book", format: "paperback"));

    private static GroundedPhoto Row(string engine, params GroundedItem[] items) => Photo("s1", engine, items);

    private static GroundedPhoto Printing(GroundedPhoto vision, params string[] lines) =>
        vision with { OcrLines = [.. lines.Select((text, i) => new GroundedOcrLine(text, 0.05 * i, 0.1, 0.04, 0.02))] };

    private GroundedPhoto ServerVision(params GroundedPhoto[] rows) => EvalCombination.OfServerRead(rows, Truths()).Single();

    [Fact]
    public void The_server_items_all_stand_in_its_order_with_no_print_check_then_the_vision_items_the_rule_keeps()
    {
        var vision = Printing(Row(Vision, Book("Copper Meadow Song"), Book("Velvet Orchard Road")), "COPPER MEADOW SONG", "VELVET ORCHARD ROAD");
        var server = Row(ServerModel, Book("Zebra Opus Nowhere"), Book("Quill Harbor Tides"));

        var row = ServerVision(server, vision);

        row.Engine.ShouldBe(ServerEngines.Vision);
        row.Items.Select(i => i.ReadTitle).ShouldBe(["Zebra Opus Nowhere", "Quill Harbor Tides", "Copper Meadow Song", "Velvet Orchard Road"]);
    }

    [Fact]
    public void The_phone_models_items_are_left_out()
    {
        var phone = Row(Apple, Book("Ashen Lantern Row"));
        var vision = Row(Vision, Book("Copper Meadow Song"));
        var server = Row(ServerModel, Book("Quill Harbor Tides"));

        var row = ServerVision(phone, server, vision);

        row.Items.Select(i => i.ReadTitle).ShouldBe(["Quill Harbor Tides", "Copper Meadow Song"]);
    }

    [Fact]
    public void A_vision_item_the_rule_does_not_keep_is_left_out()
    {
        var vision = Row(
            Vision,
            Book("Quill Harbor Tides", readAs: "Quill Harbor"), // the same item as a server item
            Book("Copper Meadow Song", readAs: "Copper"), // one word
            T1(Matched("Velvet Orchard Road", platform: null, kind: "game")), // a kind the server read nothing of
            NotFound("Ashen Lantern Row"), // found nothing
            Book("Silver Thicket Fair")); // stands
        var server = Row(ServerModel, Book("Quill Harbor Tides"));

        var row = ServerVision(server, vision);

        row.Items.Select(i => i.ReadTitle).ShouldBe(["Quill Harbor Tides", "Silver Thicket Fair"]);
    }

    [Fact]
    public void A_server_item_is_confirmed_only_when_a_vision_item_names_its_release_and_a_kept_vision_item_is_unconfirmed()
    {
        var vision = Row(Vision, Book("Quill Harbor Tides"), Book("Silver Thicket Fair"));
        var server = Row(ServerModel, Book("Quill Harbor Tides"), Book("Copper Meadow Song"));

        var row = ServerVision(server, vision);

        row.Items.Select(i => (i.ReadTitle, i.Unconfirmed)).ShouldBe(
            [("Quill Harbor Tides", false), ("Copper Meadow Song", true), ("Silver Thicket Fair", true)]);
        row.Items.Select(i => i.Tier).ShouldAllBe(tier => tier == "T1", "each keeps its own tier; the mark decides what may be added by itself");
    }

    [Fact]
    public void The_same_title_in_another_year_does_not_confirm_a_server_item()
    {
        var vision = Row(Vision, Book("Quill Harbor Tides") with { Candidates = [Release("Quill Harbor Tides", null, "book", "paperback") with { Year = 2004 }] });
        var server = Row(ServerModel, Book("Quill Harbor Tides"));

        ServerVision(server, vision).Items.Single().Unconfirmed.ShouldBeTrue();
    }

    [Fact]
    public void A_failed_server_read_changes_nothing_the_row_is_what_the_phone_shows()
    {
        var phone = Row(Apple, Book("Ashen Lantern Row"));
        var vision = Row(Vision, Book("Copper Meadow Song"));
        var server = Photo("s1", ServerModel, [], outcome: "error");
        var truths = Truths();

        var row = EvalCombination.OfServerRead([phone, server, vision], truths).Single();
        var shown = EvalCombination.Of([phone, server, vision], truths).Single();

        row.Items.ShouldBe(shown.Items);
        row.Engine.ShouldBe(ServerEngines.Vision);
    }

    [Fact]
    public void A_server_read_with_no_items_changes_nothing_the_row_is_what_the_phone_shows()
    {
        var phone = Row(Apple, Book("Ashen Lantern Row"));
        var vision = Row(Vision, Book("Copper Meadow Song"), Book("Silver Thicket Fair", readAs: "Silver"));
        var server = Row(ServerModel);
        var truths = Truths();

        var row = EvalCombination.OfServerRead([phone, server, vision], truths).Single();
        var shown = EvalCombination.Of([phone, server, vision], truths).Single();

        row.Items.ShouldBe(shown.Items);
        row.Engine.ShouldBe(ServerEngines.Vision);
    }

    [Fact]
    public void A_server_read_the_catalogue_matched_none_of_comes_first_and_the_phones_items_follow_as_they_are()
    {
        var phone = Row(Apple, Book("Ashen Lantern Row"));
        var vision = Row(Vision, Book("Copper Meadow Song"), Book("Silver Thicket Fair", readAs: "Silver"));
        var server = Row(ServerModel, NotFound("Zebra Opus Nowhere").ReadKind(WorkKind.Book));
        var truths = Truths();

        var row = EvalCombination.OfServerRead([phone, server, vision], truths).Single();
        var shown = EvalCombination.Of([phone, server, vision], truths).Single();

        row.Items.Select(i => i.ReadTitle).ShouldBe(["Zebra Opus Nowhere", .. shown.Items.Select(i => i.ReadTitle)]);
    }

    [Fact]
    public void A_server_read_cut_short_is_used_with_what_it_read()
    {
        var vision = Row(Vision, Book("Copper Meadow Song"));
        var server = Row(ServerModel, Book("Quill Harbor Tides")) with { TimedOut = true };

        var row = ServerVision(server, vision);

        row.Items.Select(i => i.ReadTitle).ShouldBe(["Quill Harbor Tides", "Copper Meadow Song"]);
        row.TimedOut.ShouldBeNull("the server_model row reports its own timeouts");
    }

    [Fact]
    public void When_vision_failed_the_row_is_the_server_items_alone_unconfirmed()
    {
        var vision = Photo("s1", Vision, [Book("Quill Harbor Tides")], outcome: "error");
        var server = Row(ServerModel, Book("Quill Harbor Tides"));

        var row = ServerVision(server, vision);

        row.Items.Select(i => (i.ReadTitle, i.Unconfirmed)).ShouldBe([("Quill Harbor Tides", true)]);
    }

    [Fact]
    public void A_single_cover_is_the_server_row_whole()
    {
        var truths = Truths(set: "single");
        var server = Row(ServerModel, Book("Quill Harbor Tides"));

        var row = EvalCombination.OfServerRead([server, Row(Vision, Book("Copper Meadow Song"))], truths).Single();

        row.Items.ShouldBe(server.Items);
    }

    [Fact]
    public void The_kind_a_server_item_was_read_as_names_its_family_when_it_found_nothing()
    {
        var vision = Row(Vision, Book("Copper Meadow Song"), T1(Matched("Velvet Orchard Road", platform: null, kind: "film", format: "dvd")));
        var server = Row(ServerModel, NotFound("Signal Over Marsh").ReadKind(WorkKind.Tv), T1(Matched("Pebble Kart Rally", platform: null)));

        var row = ServerVision(server, vision);

        row.Items.Select(i => i.ReadTitle).ShouldBe(["Signal Over Marsh", "Pebble Kart Rally", "Velvet Orchard Road"]);
    }
}
