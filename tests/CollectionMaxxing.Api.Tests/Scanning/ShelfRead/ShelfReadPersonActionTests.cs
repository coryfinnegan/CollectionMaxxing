using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.ShelfReading;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Scanning;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Scanning.ShelfRead;

// What the server's read never changes (#814, fix round 1): a row the person picked a release on or searched again, every row when the server named
// no release, and any row of another snap or another session. Each phone row here is one the rule would leave out (a book on a shelf the server read as
// games, a one-word read, or a read only the phone's model made), so a row that changed would show. A fake reader; real Postgres; made-up titles.
public class ShelfReadPersonActionTests(ApiFixture api) : ShelfReadTest(api)
{
    private const string Harbor = "Quill Harbor Tides";
    private const string Lantern = "Ashen Lantern Row";
    private const string Meadow = "Copper Meadow Song";

    private static Dictionary<string, object?> Game(string title, bool modelOnly = false)
    {
        var read = Read(title: title, kind: "game", format: "game_cartridge", year: 1996);
        if (modelOnly)
        {
            read["unconfirmed"] = true;
        }

        return read;
    }

    private async Task<(HttpClient Editor, Guid SessionId)> ArrangeAsync(TestLibrary? library = null)
    {
        library ??= await CreateLibraryAsync();
        var editor = As(library, LibraryRole.Editor);
        return (editor, await StartSessionWithoutAutoCommitAsync(editor, library.Id));
    }

    private async Task<List<ScanCandidate>> RowsAsync(Guid sessionId)
    {
        await using var context = Db.CreateContext();
        return await context.ScanCandidates.IgnoreQueryFilters().AsNoTracking().Where(c => c.SessionId == sessionId).OrderBy(c => c.Seq).ToListAsync();
    }

    private async Task UnchangedAsync(Guid sessionId, List<ScanCandidate> before, string because)
    {
        var after = await RowsAsync(sessionId);
        foreach (var row in before)
        {
            var same = after.Single(r => r.Id == row.Id);
            same.Version.ShouldBe(row.Version, $"{row.VisibleText}: {because}");
            same.Decision.ShouldBe(row.Decision);
        }
    }

    [Fact]
    public async Task A_row_the_person_picked_another_release_on_is_never_skipped_and_keeps_the_pick()
    {
        await CatalogAsync(Harbor, ScanData.Upc(9901));
        await CatalogAsync(Lantern, ScanData.Upc(9902), ReleaseFormat.Paperback, WorkKind.Book, year: 1996);
        var other = await CatalogAsync(Lantern, ScanData.Upc(9903), ReleaseFormat.Hardcover, WorkKind.Book, year: 2004);
        var (editor, sessionId) = await ArrangeAsync();
        using var _1 = editor;
        var snap = await IdentifyShelfSnapAsync(editor, sessionId, "shelf_segment", Read(title: Lantern, kind: "book", format: "paperback", year: 1996));
        var row = (await RowsAsync(sessionId)).Single();
        row.Decision.ShouldBe(CandidateDecision.Accepted, "the server ticked its own match");
        row.MatchedReleaseId.ShouldNotBe(other);
        (await DecideAsync(editor, sessionId, row.Id, new { releaseId = other })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var before = await RowsAsync(sessionId);
        Reader.Answer = _ => ScriptedReader.Found(Harbor); // a game: the rule would leave a book out

        var answered = await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, snap));

        answered.Select(Id).ShouldNotContain(row.Id);
        await UnchangedAsync(sessionId, before, "the person picked its release");
        (await RowsAsync(sessionId)).Single(r => r.Id == row.Id).MatchedReleaseId.ShouldBe(other);
    }

    [Fact]
    public async Task A_row_the_person_searched_again_is_never_skipped()
    {
        await CatalogAsync(Harbor, ScanData.Upc(9911));
        await CatalogAsync(Meadow, ScanData.Upc(9912));
        await CatalogAsync(Lantern, ScanData.Upc(9913));
        var (editor, sessionId) = await ArrangeAsync();
        using var _1 = editor;
        var snap = await IdentifyShelfSnapAsync(editor, sessionId, "shelf_segment", Game(Harbor), Game("Copper"));
        var row = (await RowsAsync(sessionId)).Single(r => r.VisibleText == "Copper");
        var searched = await editor.PostAsync($"{SessionUrl(sessionId)}/candidates/{row.Id}:search", Json(new { text = Lantern }));
        searched.StatusCode.ShouldBe(HttpStatusCode.OK, await searched.Content.ReadAsStringAsync());
        var before = await RowsAsync(sessionId);
        Reader.Answer = _ => ScriptedReader.Found(Harbor, Meadow); // the one-word read "Copper" would be left out, and Meadow is the same item

        var answered = await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, snap));

        answered.Select(Id).ShouldNotContain(row.Id);
        await UnchangedAsync(sessionId, before, "the person searched it again");
    }

    [Fact]
    public async Task A_server_read_that_found_nothing_changes_no_row()
    {
        await CatalogAsync(Harbor, ScanData.Upc(9921));
        await CatalogAsync(Meadow, ScanData.Upc(9922));
        var (editor, sessionId) = await ArrangeAsync();
        using var _1 = editor;
        var snap = await IdentifyShelfSnapAsync(editor, sessionId, "shelf_segment", Game("Harbor"), Game(Meadow, modelOnly: true));
        var before = await RowsAsync(sessionId);
        Reader.Answer = _ => ShelfReadResult.Empty(ShelfReadStatus.Ok);

        (await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, snap))).ShouldBeEmpty();

        await UnchangedAsync(sessionId, before, "an empty read is no evidence against the phone's rows");
    }

    [Fact]
    public async Task A_server_read_the_catalogue_matched_none_of_adds_its_rows_and_changes_none_of_the_phones()
    {
        await CatalogAsync(Harbor, ScanData.Upc(9931));
        await CatalogAsync(Meadow, ScanData.Upc(9932));
        var (editor, sessionId) = await ArrangeAsync();
        using var _1 = editor;
        var snap = await IdentifyShelfSnapAsync(editor, sessionId, "shelf_segment", Game("Harbor"), Game(Meadow, modelOnly: true));
        var before = await RowsAsync(sessionId);
        Reader.Answer = _ => ScriptedReader.Found("Zebra Opus Nowhere");

        var answered = await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, snap));

        answered.Select(SourceOf).ShouldBe(["picture_server_model"]);
        Resolution(answered[0]).ShouldBe("not_found");
        await UnchangedAsync(sessionId, before, "a read that matched nothing is no evidence against the phone's rows");
    }

    [Fact]
    public async Task Reading_one_snap_never_changes_a_row_of_another_snap_of_the_session()
    {
        await CatalogAsync(Harbor, ScanData.Upc(9941));
        await CatalogAsync(Meadow, ScanData.Upc(9942));
        var (editor, sessionId) = await ArrangeAsync();
        using var _1 = editor;
        var read = await IdentifyShelfSnapAsync(editor, sessionId, "shelf_segment", Game(Harbor));
        var otherSnap = await IdentifyShelfSnapAsync(editor, sessionId, "shelf_segment", Game(Meadow, modelOnly: true), Game("Copper"));
        var before = (await RowsAsync(sessionId)).Where(r => r.SnapId == otherSnap).ToList();
        before.Count.ShouldBe(2);
        Reader.Answer = _ => ScriptedReader.Found(Harbor);

        (await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, read))).ShouldBeEmpty();

        await UnchangedAsync(sessionId, before, "it belongs to another snap");
    }

    [Fact]
    public async Task Reading_a_snap_never_changes_a_row_of_another_session()
    {
        await CatalogAsync(Harbor, ScanData.Upc(9951));
        await CatalogAsync(Meadow, ScanData.Upc(9952));
        var library = await CreateLibraryAsync();
        var (editor, sessionId) = await ArrangeAsync(library);
        using var _1 = editor;
        var read = await IdentifyShelfSnapAsync(editor, sessionId, "shelf_segment", Game(Harbor));
        var otherSession = await StartSessionWithoutAutoCommitAsync(editor, library.Id);
        await IdentifyShelfSnapAsync(editor, otherSession, "shelf_segment", Game(Meadow, modelOnly: true), Game("Copper"));
        var before = await RowsAsync(otherSession);
        Reader.Answer = _ => ScriptedReader.Found(Harbor);

        (await ReadShelfAsync(editor, sessionId, read)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await UnchangedAsync(otherSession, before, "it belongs to another session");
    }
}
