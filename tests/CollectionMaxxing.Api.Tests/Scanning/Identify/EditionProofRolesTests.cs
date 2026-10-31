using System.Net;
using CollectionMaxxing.Api.Tests.Items;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Scanning.Identify;

// An edition proof rewrites a library item (its release link and its verified flag), so it needs the same role as any edit:
// owner or editor. /identify itself is open to a viewer (they may scan and look), so the rule is checked on the proof and not
// by the route, on the request and again when the queued job finishes the proof.
public class EditionProofRolesTests(ApiFixture api) : IdentifyTest(api)
{
    private static Dictionary<string, object?> Proof(Guid snapId, Guid itemId, object read)
    {
        var body = Body(snapId, [read]);
        body["kind"] = "edition_proof";
        body["shotKind"] = "single_cover";
        body["itemId"] = itemId;
        return body;
    }

    private async Task<Item> ReadItemAsync(Guid itemId)
    {
        await using var context = Db.CreateContext();
        return await context.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.Id == itemId);
    }

    private async Task DemoteToViewerAsync(TestLibrary library, Guid userId)
    {
        await using var context = Db.CreateContext();
        var changed = await context.LibraryMembers.IgnoreQueryFilters()
            .Where(m => m.LibraryId == library.Id && m.UserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, LibraryRole.Viewer));
        changed.ShouldBe(1);
    }

    [Fact]
    public async Task A_viewer_cannot_swap_an_items_edition_through_an_edition_proof()
    {
        var library = await CreateLibraryAsync();
        var first = await ItemSeed.AddReleaseAsync(Db, "Dune", WorkKind.Book, ReleaseFormat.Paperback, 1965);
        var workId = await ItemSeed.WorkOfAsync(Db, first);
        var second = await ItemSeed.AddReleaseAsync(Db, "Dune", WorkKind.Book, ReleaseFormat.Hardcover, 1965, workId: workId);
        var itemId = await TestData.CreateItemAsync(Db, library, first);
        await ItemSeed.SetEditionUnverifiedAsync(Db, itemId);
        var before = await ReadItemAsync(itemId);
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        var body = Proof(Guid.NewGuid(), itemId, Read(title: "Dune", kind: "book", localMatchReleaseId: second));
        var response = await viewer.PostAsync(IdentifyUrl(sessionId), Json(body));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await response.Content.ReadAsStringAsync());
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("scan.edition_proof_needs_editor");
        var after = await ReadItemAsync(itemId);
        after.ReleaseId.ShouldBe(before.ReleaseId);
        after.EditionVerified.ShouldBeFalse();
        after.UpdatedAt.ShouldBe(before.UpdatedAt);
        (await CountCandidatesAsync(sessionId)).ShouldBe(0, "a refused proof leaves no row behind");
        (await CountSnapsAsync(sessionId)).ShouldBe(0);
    }

    [Fact]
    public async Task A_viewer_is_refused_before_the_item_is_looked_up_so_an_id_cannot_be_probed()
    {
        var library = await CreateLibraryAsync();
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        var body = Proof(Guid.NewGuid(), Guid.NewGuid(), Read(title: "Dune", kind: "book"));
        var response = await viewer.PostAsync(IdentifyUrl(sessionId), Json(body));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "403 whether or not the item exists, never a 404 that tells a viewer which ids are real");
    }

    [Fact]
    public async Task An_editor_who_was_demoted_to_viewer_can_no_longer_send_a_proof()
    {
        var library = await CreateLibraryAsync();
        var releaseId = await ItemSeed.AddReleaseAsync(Db, "Dune", WorkKind.Book, ReleaseFormat.Paperback, 1965);
        var itemId = await TestData.CreateItemAsync(Db, library, releaseId);
        await ItemSeed.SetEditionUnverifiedAsync(Db, itemId);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        await DemoteToViewerAsync(library, library.EditorId);

        var body = Proof(Guid.NewGuid(), itemId, Read(title: "Dune", kind: "book", format: "paperback", year: 1965));
        var response = await editor.PostAsync(IdentifyUrl(sessionId), Json(body));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await response.Content.ReadAsStringAsync());
        (await ReadItemAsync(itemId)).EditionVerified.ShouldBeFalse();
    }

    [Fact]
    public async Task A_viewer_can_still_read_a_shelf_and_a_cover_with_identify()
    {
        var library = await CreateLibraryAsync();
        await ItemSeed.AddReleaseAsync(Db, "Dune", WorkKind.Book, ReleaseFormat.Paperback, 1965);
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        var candidates = await IdentifyOkAsync(viewer, sessionId, Read(title: "Dune", kind: "book", format: "paperback", year: 1965));

        Resolution(candidates.ShouldHaveSingleItem()).ShouldBe("matched");
    }

    // ---- the queued job re-checks the role of the person who sent the proof, at run time ----

    private sealed record QueuedProof(TestLibrary Library, HttpClient Editor, Guid SessionId, Guid ItemId, Guid CandidateId, Guid ReleaseId);

    /// <summary>
    /// An editor's proof the request ran out of time for: pending, with its job queued. By the time the job runs the catalogue
    /// knows the book (the item's release is that book), so a job that finishes the proof would verify the item.
    /// </summary>
    private async Task<QueuedProof> QueueProofAsync()
    {
        Options.ResolveBudget = TimeSpan.FromMilliseconds(300);
        var slow = true;
        Books.Behaviour = async ct =>
        {
            if (slow)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
        };
        var library = await CreateLibraryAsync();
        var placeholder = await ItemSeed.AddReleaseAsync(Db, "A Placeholder Nobody Reads", WorkKind.Book, ReleaseFormat.Paperback, 1990);
        var itemId = await TestData.CreateItemAsync(Db, library, placeholder);
        await ItemSeed.SetEditionUnverifiedAsync(Db, itemId);
        var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, new { autoCommitTiers = new[] { "T0", "T1" } });

        var body = Proof(Guid.NewGuid(), itemId, Read(title: "Dune", kind: "book", format: "paperback", year: 1965));
        var response = await editor.PostAsync(IdentifyUrl(sessionId), Json(body));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var pending = (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().Single();
        Resolution(pending).ShouldBe("pending");

        // The catalogue learns the book, and the item's release is that book: what the proof would confirm.
        var dune = await ItemSeed.AddReleaseAsync(Db, "Dune", WorkKind.Book, ReleaseFormat.Paperback, 1965);
        await using (var context = Db.CreateContext())
        {
            await context.Items.IgnoreQueryFilters().Where(i => i.Id == itemId).ExecuteUpdateAsync(s => s.SetProperty(i => i.ReleaseId, dune));
        }

        slow = false;
        return new QueuedProof(library, editor, sessionId, itemId, Id(pending), dune);
    }

    [Fact]
    public async Task A_queued_proof_still_verifies_the_item_when_its_sender_is_an_editor_at_run_time()
    {
        var queued = await QueueProofAsync();
        using var editor = queued.Editor;

        await RunNextJobAsync();

        var item = await ReadItemAsync(queued.ItemId);
        item.ReleaseId.ShouldBe(queued.ReleaseId);
        item.EditionVerified.ShouldBeTrue("the control: an editor's proof finishes as its request would have");
    }

    [Fact]
    public async Task A_queued_proof_does_nothing_to_the_item_when_its_sender_was_demoted_to_viewer_before_it_ran()
    {
        var queued = await QueueProofAsync();
        using var editor = queued.Editor;
        var before = await ReadItemAsync(queued.ItemId);
        await DemoteToViewerAsync(queued.Library, queued.Library.EditorId);

        await RunNextJobAsync();

        var after = await ReadItemAsync(queued.ItemId);
        after.ReleaseId.ShouldBe(before.ReleaseId);
        after.EditionVerified.ShouldBeFalse();
        after.UpdatedAt.ShouldBe(before.UpdatedAt);
        await using var context = Db.CreateContext();
        var row = await context.ScanCandidates.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == queued.CandidateId);
        row.CreatedItemId.ShouldBeNull("the row is not the item");
        row.Resolution.ShouldNotBe(Domain.Scanning.CandidateResolution.Pending, "the row is settled for review rather than waiting for ever");
    }
}
