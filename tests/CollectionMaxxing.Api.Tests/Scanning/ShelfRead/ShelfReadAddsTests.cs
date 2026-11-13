using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.ShelfReading;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Scanning;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Scanning.ShelfRead;

// The server reads the shelf picture of a snap identify has answered (ADR-033) and adds the titles it finds that the scan lacks, as unconfirmed rows.
// A fake reader stands in for the model; real Postgres; the catalogue holds neutral titles.
public class ShelfReadAddsTests(ApiFixture api) : ShelfReadTest(api)
{
    private static object OptIn() => new { autoCommitTiers = new[] { "T0", "T1" } };

    private static Dictionary<string, object?> PhoneRead(string title) => Read(title: title, kind: "game", format: "game_cartridge", year: 1996);

    private async Task<(TestLibrary Library, HttpClient Editor, Guid SessionId)> ArrangeAsync(object? session = null, params string[] catalogue)
    {
        var library = await CreateLibraryAsync();
        for (var i = 0; i < catalogue.Length; i++)
        {
            await CatalogAsync(catalogue[i], ScanData.Upc(9100 + i));
        }

        var editor = As(library, LibraryRole.Editor);
        var sessionId = session is null ? await StartSessionWithoutAutoCommitAsync(editor, library.Id) : await StartSessionAsync(editor, library.Id, session);
        return (library, editor, sessionId);
    }

    private async Task<List<ScanCandidate>> RowsAsync(Guid sessionId)
    {
        await using var context = Db.CreateContext();
        return await context.ScanCandidates.IgnoreQueryFilters().AsNoTracking().Where(c => c.SessionId == sessionId).OrderBy(c => c.Seq).ToListAsync();
    }

    [Fact]
    public async Task A_shelf_read_by_the_server_adds_titles_the_phone_missed()
    {
        var (_, editor, sessionId) = await ArrangeAsync(null, "Star Fox 64", "Wave Race 64");
        using var _1 = editor;
        var snap = await IdentifyShelfSnapAsync(editor, sessionId, "shelf_segment", PhoneRead("Star Fox 64"));
        Reader.Answer = _ => ScriptedReader.Found("Star Fox 64", "Wave Race 64");

        var added = await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, snap));

        added.Count.ShouldBe(1);
        ReleaseTitle(added[0]).ShouldBe("Wave Race 64");
        SourceOf(added[0]).ShouldBe("picture_server_model");
        Resolution(added[0]).ShouldBe("matched");
        (await CountCandidatesAsync(sessionId)).ShouldBe(2);
        Stills.Calls.ShouldBe(1);
        Reader.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task A_title_a_row_already_has_is_not_added_again()
    {
        var (_, editor, sessionId) = await ArrangeAsync(null, "Star Fox 64");
        using var _1 = editor;
        var snap = await IdentifyShelfSnapAsync(editor, sessionId, "shelf_segment", PhoneRead("Star Fox 64"));
        Reader.Answer = _ => ScriptedReader.Found("Star Fox 64");

        var added = await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, snap));

        added.ShouldBeEmpty();
        (await CountCandidatesAsync(sessionId)).ShouldBe(1);
    }

    [Fact]
    public async Task A_title_only_the_server_read_is_added_whatever_the_shelf_prints()
    {
        var (_, editor, sessionId) = await ArrangeAsync(null, "Star Fox 64", "Wave Race 64", "Pilotwings 64");
        using var _1 = editor;
        var printed = await IdentifyShelfSnapAsync(editor, sessionId, "shelf_segment", PhoneRead("Star Fox 64"));
        var unprinted = await IdentifyShelfSnapAsync(editor, sessionId, "shelf_segment", PhoneRead("Star Fox 64"));
        Reader.Answer = _ => ScriptedReader.Found("Wave Race 64", "Pilotwings 64");

        var withPrint = await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, printed, lines: ["WAVE RACE 64", "STAR FOX"]));
        var withoutIt = await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, unprinted, lines: ["STAR FOX", "SOMETHING ELSE ENTIRELY"]));

        withPrint.Select(ReleaseTitle).ShouldBe(["Wave Race 64", "Pilotwings 64"], "in the server's order");
        withoutIt.Select(ReleaseTitle).ShouldBe(["Wave Race 64", "Pilotwings 64"], "the server's reads need no print to stand (#814)");
    }

    [Fact]
    public async Task Without_any_print_the_rule_holds_no_read_back_but_a_repeat()
    {
        var (_, editor, sessionId) = await ArrangeAsync(null, "Star Fox 64", "Wave Race 64");
        using var _1 = editor;
        var snap = await IdentifyShelfSnapAsync(editor, sessionId, "shelf_segment", PhoneRead("Star Fox 64"));
        Reader.Answer = _ => ScriptedReader.Found("Star Fox 64", "Wave Race 64");

        var added = await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, snap, lines: null));

        added.Select(ReleaseTitle).ShouldBe(["Wave Race 64"]);
    }

    [Fact]
    public async Task A_row_the_person_decided_edited_or_that_was_added_is_never_changed()
    {
        var (library, editor, sessionId) = await ArrangeAsync(OptIn(), "Star Fox 64", "Mario Golf", "Wave Race 64", "Pilotwings 64");
        using var _1 = editor;
        var unconfirmed = PhoneRead("Mario Golf");
        unconfirmed["unconfirmed"] = true;
        var edited = PhoneRead("Pilotwings 64");
        edited["unconfirmed"] = true;
        var snap = await IdentifyShelfSnapAsync(editor, sessionId, "shelf_segment", PhoneRead("Star Fox 64"), unconfirmed, edited);
        var rows = await RowsAsync(sessionId);
        var added = rows.Single(r => r.CreatedItemId is not null); // Star Fox 64: T1, added by itself
        var decided = rows.Single(r => r.VisibleText == "Mario Golf");
        var changed = rows.Single(r => r.VisibleText == "Pilotwings 64");
        (await DecideAsync(editor, sessionId, decided.Id, new { decision = "rejected" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await DecideAsync(editor, sessionId, changed.Id, new { edition = "Player's Choice" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var before = await RowsAsync(sessionId);
        Reader.Answer = _ => ScriptedReader.Found("Star Fox 64", "Mario Golf", "Pilotwings 64", "Wave Race 64");

        var fresh = await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, snap));

        fresh.Select(ReleaseTitle).ShouldBe(["Wave Race 64"]);
        var after = await RowsAsync(sessionId);
        foreach (var row in before)
        {
            var same = after.Single(r => r.Id == row.Id);
            same.Version.ShouldBe(row.Version, $"{row.VisibleText} was written");
            same.ChangeSeq.ShouldBe(row.ChangeSeq);
            same.Decision.ShouldBe(row.Decision);
            same.CreatedItemId.ShouldBe(row.CreatedItemId);
            same.AlternativesJson.ShouldBe(row.AlternativesJson);
        }

        after.Single(r => r.Id == added.Id).CreatedItemId.ShouldBe(added.CreatedItemId);
        after.Single(r => r.Id == decided.Id).Decision.ShouldBe(CandidateDecision.Rejected);
        after.Single(r => r.Id == changed.Id).EditedEdition.ShouldBe("Player's Choice");
        (await CountItemsAsync(library.Id)).ShouldBe(1, "only the phone's own auto-add");
    }

    [Fact]
    public async Task Rows_the_server_read_are_never_added_by_themselves()
    {
        var (library, editor, sessionId) = await ArrangeAsync(OptIn(), "Star Fox 64", "Wave Race 64");
        using var _1 = editor;
        var phone = PhoneRead("Star Fox 64");
        phone["unconfirmed"] = true;
        var snap = await IdentifyShelfSnapAsync(editor, sessionId, "shelf_segment", phone);
        Reader.Answer = _ => ScriptedReader.Found("Wave Race 64");

        var added = (await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, snap))).Where(c => SourceOf(c) == "picture_server_model").ToList();

        added.Count.ShouldBe(1);
        Resolution(added[0]).ShouldBe("matched");
        added[0].GetProperty("tier").GetString().ShouldBe("T2");
        Decision(added[0]).ShouldBe("undecided");
        added[0].TryGetProperty("createdItemId", out var created).ShouldBeTrue();
        (created.ValueKind == System.Text.Json.JsonValueKind.Null).ShouldBeTrue();
        (await CountItemsAsync(library.Id)).ShouldBe(0, "the person opts in; nothing adds a server read");
        var row = (await RowsAsync(sessionId)).Single(r => r.Source == CandidateSource.PictureServerModel);
        StoredUnconfirmed(row).ShouldBeTrue("the stored read says only the server's model saw it");
        (await CommitAsync(editor, sessionId)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CountItemsAsync(library.Id)).ShouldBe(0, "a commit adds only what was accepted");
    }

    [Fact]
    public async Task Sending_the_same_snap_twice_reads_it_once_and_answers_the_same_rows()
    {
        var (_, editor, sessionId) = await ArrangeAsync(null, "Star Fox 64", "Wave Race 64");
        using var _1 = editor;
        var snap = await IdentifyShelfSnapAsync(editor, sessionId, "shelf_segment", PhoneRead("Star Fox 64"));
        Reader.Answer = _ => ScriptedReader.Found("Wave Race 64");

        var first = await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, snap));
        var second = await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, snap));

        Reader.Calls.ShouldBe(1);
        Stills.Calls.ShouldBe(1);
        second.Select(Id).ShouldBe(first.Select(Id));
        (await CountCandidatesAsync(sessionId)).ShouldBe(2);
    }

    [Fact]
    public async Task A_read_that_added_nothing_is_not_read_again()
    {
        var (_, editor, sessionId) = await ArrangeAsync(null, "Star Fox 64");
        using var _1 = editor;
        var snap = await IdentifyShelfSnapAsync(editor, sessionId, "shelf_segment", PhoneRead("Star Fox 64"));
        Reader.Answer = _ => ScriptedReader.Found("Star Fox 64");

        (await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, snap))).ShouldBeEmpty();
        (await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, snap))).ShouldBeEmpty();

        Reader.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task A_read_cut_short_answers_what_it_read()
    {
        var (_, editor, sessionId) = await ArrangeAsync(null, "Star Fox 64", "Wave Race 64");
        using var _1 = editor;
        var snap = await IdentifyShelfSnapAsync(editor, sessionId, "shelf_segment", PhoneRead("Star Fox 64"));
        Reader.Answer = _ => new ShelfReadResult(ScriptedReader.Found("Wave Race 64").Items, ShelfReadStatus.CutShort);

        var added = await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, snap));

        added.Select(ReleaseTitle).ShouldBe(["Wave Race 64"]);
    }

    [Fact]
    public async Task A_title_the_catalogue_cannot_find_is_added_to_search_for_by_hand()
    {
        var (_, editor, sessionId) = await ArrangeAsync(null, "Star Fox 64");
        using var _1 = editor;
        var snap = await IdentifyShelfSnapAsync(editor, sessionId, "shelf_segment", PhoneRead("Star Fox 64"));
        Reader.Answer = _ => ScriptedReader.Found("Zebra Opus Nowhere");

        var added = await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, snap, lines: ["STAR FOX 64"]));

        added.Count.ShouldBe(1);
        Resolution(added[0]).ShouldBe("not_found", "a row to search for by hand, as the eval's server_vision row shows it");
    }

    [Fact]
    public async Task A_viewer_may_read_a_shelf_and_the_rows_are_theirs_alone()
    {
        var library = await CreateLibraryAsync();
        await CatalogAsync("Star Fox 64", ScanData.Upc(9201));
        await CatalogAsync("Wave Race 64", ScanData.Upc(9202));
        using var viewer = As(library, LibraryRole.Viewer);
        using var owner = As(library, LibraryRole.Owner);
        var sessionId = await StartSessionWithoutAutoCommitAsync(viewer, library.Id);
        var snap = await IdentifyShelfSnapAsync(viewer, sessionId, "shelf_segment", PhoneRead("Star Fox 64"));
        Reader.Answer = _ => ScriptedReader.Found("Wave Race 64");

        (await ReadShelfAsync(owner, sessionId, snap)).StatusCode.ShouldBe(HttpStatusCode.NotFound, "another member's session is not readable");
        Reader.Calls.ShouldBe(0);
        (await CandidatesOfAsync(await ReadShelfAsync(viewer, sessionId, snap))).Count.ShouldBe(1);
    }

    private static bool StoredUnconfirmed(ScanCandidate row)
    {
        using var json = System.Text.Json.JsonDocument.Parse(row.InferredJson!);
        return json.RootElement.TryGetProperty("unconfirmed", out var flag) && flag.GetBoolean();
    }
}
