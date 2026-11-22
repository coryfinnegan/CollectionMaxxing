using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Scanning;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Scanning.ShelfRead;

// The server's rows take the place of the phone read they agree with (#835), so Review can light the book on the photo: the box of that read and
// the snap's segment id. The phone reads below are books the catalogue does not have, so they are never the same release as the server's game
// and the server's row is added beside them. The phones boxes come through a real identify request. A fake reader; real Postgres; made-up titles.
public class ShelfReadPlaceTests(ApiFixture api) : ShelfReadTest(api)
{
    private const string Harbor = "Quill Harbor Tides";
    private const string Meadow = "Copper Meadow Song";
    private const string Segment = "seg-one";

    private static Dictionary<string, object?> PhoneBook(string title, double x, bool modelOnly = false, string segment = Segment)
    {
        var read = Read(title: title, kind: "book", format: "paperback", year: 1996, segmentId: segment);
        read["box"] = new { x, y = 0.1, width = 0.05, height = 0.8 };
        if (modelOnly)
        {
            read["unconfirmed"] = true;
        }

        return read;
    }

    private async Task<(HttpClient Editor, Guid SessionId)> ArrangeAsync(params string[] catalogue)
    {
        var library = await CreateLibraryAsync();
        for (var i = 0; i < catalogue.Length; i++)
        {
            await CatalogAsync(catalogue[i], ScanData.Upc(9800 + i));
        }

        var editor = As(library, LibraryRole.Editor);
        return (editor, await StartSessionWithoutAutoCommitAsync(editor, library.Id));
    }

    private async Task<List<ScanCandidate>> ServerRowsAsync(Guid sessionId)
    {
        await using var context = Db.CreateContext();
        return await context.ScanCandidates.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.SessionId == sessionId && c.Source == CandidateSource.PictureServerModel).OrderBy(c => c.Seq).ToListAsync();
    }

    private async Task<ScanCandidate> PhoneRowAsync(Guid sessionId, string text)
    {
        await using var context = Db.CreateContext();
        return (await context.ScanCandidates.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.SessionId == sessionId && c.Source != CandidateSource.PictureServerModel).ToListAsync()).Single(c => c.VisibleText == text);
    }

    [Fact]
    public async Task A_server_row_takes_the_box_of_the_vision_row_it_replaces_and_the_photos_segment_id()
    {
        var (editor, sessionId) = await ArrangeAsync(Harbor);
        using var _ = editor;
        var snap = await IdentifyShelfSnapAsync(editor, sessionId, "shelf_segment", PhoneBook("Quill Harbor", 0.3));
        var phone = await PhoneRowAsync(sessionId, "Quill Harbor");
        Reader.Answer = _ => ScriptedReader.Found(Harbor);

        await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, snap));

        var row = (await ServerRowsAsync(sessionId)).Single();
        row.BoxX.ShouldBe(phone.BoxX);
        row.BoxY.ShouldBe(phone.BoxY);
        row.BoxW.ShouldBe(phone.BoxW);
        row.BoxH.ShouldBe(phone.BoxH);
        row.BoxX.ShouldNotBeNull();
        row.BoxSource.ShouldBe(phone.BoxSource);
        row.SegmentId.ShouldBe(Segment);
    }

    [Fact]
    public async Task A_server_row_takes_the_box_of_a_phone_read_the_rule_does_not_show_when_no_kept_row_agrees()
    {
        var (editor, sessionId) = await ArrangeAsync(Harbor, Meadow);
        using var _ = editor;
        var snap = await IdentifyShelfSnapAsync(editor, sessionId, "shelf_segment", PhoneBook("Quill Harbor", 0.3, modelOnly: true));
        var phone = await PhoneRowAsync(sessionId, "Quill Harbor");
        Reader.Answer = _ => ScriptedReader.Found(Harbor);

        await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, snap));

        var row = (await ServerRowsAsync(sessionId)).Single();
        row.BoxX.ShouldBe(phone.BoxX);
        row.BoxX.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_box_is_never_given_to_two_server_rows()
    {
        var (editor, sessionId) = await ArrangeAsync(Harbor, "Quill Harbor Tides Redux");
        using var _ = editor;
        var snap = await IdentifyShelfSnapAsync(editor, sessionId, "shelf_segment", PhoneBook("Quill Harbor", 0.3));
        Reader.Answer = _ => ScriptedReader.Found(Harbor, "Quill Harbor Tides Redux");

        await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, snap));

        var rows = await ServerRowsAsync(sessionId);
        rows.Count.ShouldBe(2);
        rows.Count(r => r.BoxX is not null).ShouldBe(1, "one phone box, one server row");
        rows.ShouldAllBe(r => r.SegmentId == Segment, "the segment id goes on every row, boxed or not");
        rows[0].BoxX.ShouldNotBeNull("the tie goes to the earlier row");
    }

    [Fact]
    public async Task A_server_row_stays_without_a_box_when_no_words_agree_or_only_one_does_but_still_carries_the_segment_id()
    {
        var (editor, sessionId) = await ArrangeAsync(Harbor, Meadow);
        using var _ = editor;
        var snap = await IdentifyShelfSnapAsync(
            editor, sessionId, "shelf_segment", PhoneBook("Velvet Orchard Road", 0.1, modelOnly: true), PhoneBook("Tides", 0.5, modelOnly: true));
        Reader.Answer = _ => ScriptedReader.Found(Harbor, Meadow);

        await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, snap));

        var rows = await ServerRowsAsync(sessionId);
        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(r => r.BoxX == null && r.BoxY == null && r.BoxW == null && r.BoxH == null && r.BoxSource == null);
        rows.ShouldAllBe(r => r.SegmentId == Segment);
    }

    [Fact]
    public async Task A_repeat_request_changes_no_box()
    {
        var (editor, sessionId) = await ArrangeAsync(Harbor);
        using var _ = editor;
        var snap = await IdentifyShelfSnapAsync(editor, sessionId, "shelf_segment", PhoneBook("Quill Harbor", 0.3));
        Reader.Answer = _ => ScriptedReader.Found(Harbor);
        await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, snap));
        var first = await ServerRowsAsync(sessionId);

        await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, snap));

        var second = await ServerRowsAsync(sessionId);
        second.Select(r => (r.Id, r.BoxX, r.BoxY, r.BoxW, r.BoxH, r.SegmentId)).ShouldBe(first.Select(r => (r.Id, r.BoxX, r.BoxY, r.BoxW, r.BoxH, r.SegmentId)));
    }

    [Fact]
    public async Task A_phone_row_that_stands_keeps_its_box_and_no_server_row_takes_it()
    {
        var (editor, sessionId) = await ArrangeAsync(Harbor, "Quill Harbor Tides Redux");
        using var _ = editor;
        var read = Read(title: Harbor, kind: "game", format: "game_cartridge", year: 1996, segmentId: Segment);
        read["box"] = new { x = 0.3, y = 0.1, width = 0.05, height = 0.8 };
        var snap = await IdentifyShelfSnapAsync(editor, sessionId, "shelf_segment", read);
        Reader.Answer = _ => ScriptedReader.Found(Harbor, "Quill Harbor Tides Redux");

        await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, snap));

        var rows = await ServerRowsAsync(sessionId);
        rows.Count.ShouldBe(1, "the phone's row is the row for the first title");
        rows[0].BoxX.ShouldBeNull("the phone row still shows at that place");
        rows[0].SegmentId.ShouldBe(Segment);
        (await PhoneRowAsync(sessionId, Harbor)).BoxX.ShouldBe(0.3);
    }

    [Fact]
    public async Task A_phone_row_the_person_decided_does_not_lend_its_box()
    {
        var (editor, sessionId) = await ArrangeAsync(Harbor);
        using var _ = editor;
        var snap = await IdentifyShelfSnapAsync(editor, sessionId, "shelf_segment", PhoneBook("Quill Harbor", 0.3, modelOnly: true));
        var phone = await PhoneRowAsync(sessionId, "Quill Harbor");
        (await DecideAsync(editor, sessionId, phone.Id, new { decision = "rejected" })).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        Reader.Answer = _ => ScriptedReader.Found(Harbor);

        await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, snap));

        (await ServerRowsAsync(sessionId)).Single().BoxX.ShouldBeNull("the person's row keeps showing there");
    }

    [Fact]
    public async Task No_box_is_lent_when_the_server_read_names_no_release()
    {
        var (editor, sessionId) = await ArrangeAsync(Harbor);
        using var _ = editor;
        var snap = await IdentifyShelfSnapAsync(editor, sessionId, "shelf_segment", PhoneBook("Quill Harbor", 0.3, modelOnly: true));
        Reader.Answer = _ => ScriptedReader.Found("Zorblax Quill Harbor Saga"); // not in the catalogue: the phone's rows all stand

        await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, snap));

        var rows = await ServerRowsAsync(sessionId);
        rows.Single().BoxX.ShouldBeNull();
        rows.Single().SegmentId.ShouldBe(Segment);
    }

    [Fact]
    public async Task A_server_row_with_a_donor_carries_the_donors_segment_id()
    {
        var (editor, sessionId) = await ArrangeAsync(Harbor, Meadow);
        using var _ = editor;
        var snap = await IdentifyShelfSnapAsync(
            editor, sessionId, "shelf_segment",
            PhoneBook("Velvet Orchard Road", 0.1, modelOnly: true, segment: "seg-first"),
            PhoneBook("Quill Harbor", 0.5, modelOnly: true, segment: "seg-donor"));
        Reader.Answer = _ => ScriptedReader.Found(Harbor, Meadow);

        await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, snap));

        var rows = await ServerRowsAsync(sessionId);
        rows[0].SegmentId.ShouldBe("seg-donor");
        rows[0].BoxX.ShouldBe(0.5);
        rows[1].SegmentId.ShouldBe("seg-first", "without a donor, the photo's first segment id");
        rows[1].BoxX.ShouldBeNull();
    }
}
