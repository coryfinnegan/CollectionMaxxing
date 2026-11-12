using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Operations;
using CollectionMaxxing.Domain.Scanning;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Scanning.ShelfRead;

// Each mark a person's action leaves on a row keeps the server's later shelf read (#814) from skipping it (fix round 2). Every row here is one the
// rule would leave out (a book on a shelf the server read as games, or a spine the catalogue found nothing for), and each fixture leaves exactly one
// mark, so each test fails when that mark's own guard is removed. Where the real action leaves two marks, the test takes the other one back in the
// database, saying so. A fake reader; real Postgres; made-up titles.
public class ShelfReadPersonMarksTests(ApiFixture api) : ShelfReadTest(api)
{
    private const string Harbor = "Quill Harbor Tides";
    private const string Lantern = "Ashen Lantern Row";

    private static object OptIn() => new { autoCommitTiers = new[] { "T0", "T1" } };

    private static Dictionary<string, object?> Book(string title) => Read(title: title, kind: "book", format: "paperback", year: 1996);

    private async Task<(TestLibrary Library, HttpClient Editor, Guid SessionId)> ArrangeAsync(object? session = null)
    {
        await CatalogAsync(Harbor, ScanData.Upc(9961));
        var library = await CreateLibraryAsync();
        var editor = As(library, LibraryRole.Editor);
        var sessionId = session is null ? await StartSessionWithoutAutoCommitAsync(editor, library.Id) : await StartSessionAsync(editor, library.Id, session);
        Reader.Answer = _ => ScriptedReader.Found(Harbor); // a game: the rule leaves the book out
        return (library, editor, sessionId);
    }

    private async Task<ScanCandidate> RowAsync(Guid id)
    {
        await using var context = Db.CreateContext();
        return await context.ScanCandidates.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == id);
    }

    private async Task ChangeAsync(Guid id, Action<ScanCandidate> change)
    {
        await using var context = Db.CreateContext();
        var row = await context.ScanCandidates.IgnoreQueryFilters().SingleAsync(c => c.Id == id);
        change(row);
        await context.SaveChangesAsync();
    }

    private async Task NotSkippedAsync(HttpClient editor, Guid sessionId, Guid snap, Guid rowId)
    {
        var before = await RowAsync(rowId);

        var answered = await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, snap));

        answered.Select(Id).ShouldNotContain(rowId);
        var after = await RowAsync(rowId);
        after.Version.ShouldBe(before.Version, "the row was written");
        after.Decision.ShouldBe(before.Decision);
    }

    private static async Task<Guid> OneBookAsync(HttpClient editor, Guid sessionId, string title = Lantern) =>
        await IdentifyShelfSnapAsync(editor, sessionId, "shelf_segment", Book(title));

    [Fact]
    public async Task A_row_the_person_picked_the_servers_own_first_release_on_again_is_not_skipped()
    {
        await CatalogAsync(Lantern, ScanData.Upc(9962), ReleaseFormat.Paperback, WorkKind.Book);
        var (_, editor, sessionId) = await ArrangeAsync();
        using var _1 = editor;
        var snap = await OneBookAsync(editor, sessionId);
        var row = (await RowsOfSnapAsync(snap)).Single();
        row.Decision.ShouldBe(CandidateDecision.Accepted, "the server ticked its own match");

        (await DecideAsync(editor, sessionId, row.Id, new { releaseId = row.MatchedReleaseId })).StatusCode.ShouldBe(HttpStatusCode.OK);

        await NotSkippedAsync(editor, sessionId, snap, row.Id);
    }

    [Fact]
    public async Task A_row_whose_match_is_no_longer_its_first_release_is_not_skipped()
    {
        await CatalogAsync(Lantern, ScanData.Upc(9963), ReleaseFormat.Paperback, WorkKind.Book, year: 1996);
        var other = await CatalogAsync(Lantern, ScanData.Upc(9964), ReleaseFormat.Hardcover, WorkKind.Book, year: 2004);
        var (_, editor, sessionId) = await ArrangeAsync();
        using var _1 = editor;
        var snap = await OneBookAsync(editor, sessionId);
        var row = (await RowsOfSnapAsync(snap)).Single();
        row.Decision.ShouldBe(CandidateDecision.Accepted);
        row.MatchedReleaseId.ShouldNotBe(other);

        // Only the match moved: the server's own decision and no feedback row, so the moved match alone keeps the row.
        await ChangeAsync(row.Id, r => r.MatchedReleaseId = other);

        await NotSkippedAsync(editor, sessionId, snap, row.Id);
    }

    [Fact]
    public async Task A_row_with_match_feedback_is_not_skipped()
    {
        var first = await CatalogAsync(Lantern, ScanData.Upc(9965), ReleaseFormat.Paperback, WorkKind.Book);
        var (library, editor, sessionId) = await ArrangeAsync();
        using var _1 = editor;
        var snap = await OneBookAsync(editor, sessionId);
        var row = (await RowsOfSnapAsync(snap)).Single();
        row.Decision.ShouldBe(CandidateDecision.Accepted);

        // Only the feedback row: the match, the decision and the trace are as the server left them.
        await using (var context = Db.CreateContext())
        {
            context.MatchFeedback.Add(new MatchFeedback
            {
                Id = Guid.CreateVersion7(),
                LibraryId = library.Id,
                CandidateId = row.Id,
                RankedFirstReleaseId = Guid.CreateVersion7(),
                PickedReleaseId = first,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        await NotSkippedAsync(editor, sessionId, snap, row.Id);
    }

    [Fact]
    public async Task Rows_restored_by_undoing_a_commit_are_not_skipped_by_a_late_shelf_read()
    {
        await CatalogAsync(Lantern, ScanData.Upc(9966), ReleaseFormat.Paperback, WorkKind.Book);
        var (library, editor, sessionId) = await ArrangeAsync();
        using var _1 = editor;
        var snap = await OneBookAsync(editor, sessionId);
        var row = (await RowsOfSnapAsync(snap)).Single();
        (await CommitAsync(editor, sessionId)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CountItemsAsync(library.Id)).ShouldBe(1);
        var undo = new HttpRequestMessage(HttpMethod.Post, $"{SessionUrl(sessionId)}/commit:undo");
        undo.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        (await editor.SendAsync(undo)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RowAsync(row.Id)).Decision.ShouldBe(CandidateDecision.Accepted, "the undo gives back the rows the person added");

        await NotSkippedAsync(editor, sessionId, snap, row.Id);
    }

    [Fact]
    public async Task A_row_the_person_changed_the_copies_of_is_not_skipped()
    {
        await CatalogAsync(Lantern, ScanData.Upc(9967), ReleaseFormat.Paperback, WorkKind.Book);
        var (_, editor, sessionId) = await ArrangeAsync();
        using var _1 = editor;
        var snap = await OneBookAsync(editor, sessionId);
        var row = (await RowsOfSnapAsync(snap)).Single();

        (await DecideAsync(editor, sessionId, row.Id, new { copies = 2 })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RowAsync(row.Id)).Decision.ShouldBe(CandidateDecision.Accepted, "still as the server ticked it: only the copies say the person was here");

        await NotSkippedAsync(editor, sessionId, snap, row.Id);
    }

    [Fact]
    public async Task A_row_whose_automatic_add_the_person_undid_is_not_skipped()
    {
        await CatalogAsync(Lantern, ScanData.Upc(9968), ReleaseFormat.Paperback, WorkKind.Book);
        var (_, editor, sessionId) = await ArrangeAsync(OptIn());
        using var _1 = editor;
        var snap = await OneBookAsync(editor, sessionId);
        var row = (await RowsOfSnapAsync(snap)).Single();
        row.CreatedItemId.ShouldNotBeNull("added by itself");
        var undo = new HttpRequestMessage(HttpMethod.Post, $"{SessionUrl(sessionId)}/auto-commit:undo") { Content = Json(new { candidateId = row.Id }) };
        undo.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        (await editor.SendAsync(undo)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var undone = await RowAsync(row.Id);
        undone.Decision.ShouldBe(CandidateDecision.Undecided);
        undone.Resolution.ShouldBe(CandidateResolution.Matched, "matched and confirmed: only its being undecided says the person was here");

        await NotSkippedAsync(editor, sessionId, snap, row.Id);
    }

    [Fact]
    public async Task A_row_the_person_put_back_after_identify_skipped_it_is_not_skipped()
    {
        var (_, editor, sessionId) = await ArrangeAsync();
        using var _1 = editor;
        object Spine(string title, double x)
        {
            var read = Book(title);
            read["box"] = new { x, y = 0.1, width = 0.05, height = 0.8 };
            return read;
        }

        // Four books, so the reads are spines on a shelf (ScoringProfileSelector.ShelfMinimum), and none is in the catalogue.
        var snap = await IdentifyShelfSnapAsync(
            editor,
            sessionId,
            "shelf_segment",
            Spine("Nowhere Atlas Of Lost Tides", 0.1),
            Spine("Nowhere Almanac Of Far Bays", 0.3),
            Spine("Nowhere Ledger Of Grey Fens", 0.5),
            Spine("Nowhere Primer Of Cold Moors", 0.7));
        var row = (await RowsOfSnapAsync(snap))[0];
        row.Decision.ShouldBe(CandidateDecision.Rejected, "identify skipped a spine the catalogue has nothing for");
        row.Resolution.ShouldBe(CandidateResolution.NotFound);

        (await DecideAsync(editor, sessionId, row.Id, new { decision = "undecided" })).StatusCode.ShouldBe(HttpStatusCode.OK);

        await NotSkippedAsync(editor, sessionId, snap, row.Id);
    }

    [Fact]
    public async Task A_row_kept_as_a_provisional_record_is_not_skipped()
    {
        var (_, editor, sessionId) = await ArrangeAsync();
        using var _1 = editor;
        var snap = await IdentifyShelfSnapAsync(editor, sessionId, "shelf_segment", Book("Nowhere Atlas Of Lost Tides"));
        var row = (await RowsOfSnapAsync(snap)).Single();
        row.Decision.ShouldBe(CandidateDecision.Undecided, "a read the catalogue found nothing for, not a spine identify skips");
        (await editor.PostAsync($"{SessionUrl(sessionId)}/provisionals", Json(new { candidateId = row.Id }))).StatusCode.ShouldBe(HttpStatusCode.Created);

        // Keeping retires the row (rejected) as well; that decision is taken back here, so the provisional link alone keeps the row.
        await ChangeAsync(row.Id, r => r.Decision = CandidateDecision.Undecided);
        (await RowAsync(row.Id)).ProvisionalId.ShouldNotBeNull();

        await NotSkippedAsync(editor, sessionId, snap, row.Id);
    }

    private async Task<List<ScanCandidate>> RowsOfSnapAsync(Guid snap)
    {
        await using var context = Db.CreateContext();
        return await context.ScanCandidates.IgnoreQueryFilters().AsNoTracking().Where(c => c.SnapId == snap).OrderBy(c => c.Seq).ToListAsync();
    }
}
