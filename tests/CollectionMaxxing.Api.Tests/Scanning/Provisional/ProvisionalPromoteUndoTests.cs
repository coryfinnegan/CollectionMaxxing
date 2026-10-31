using System.Net;
using System.Net.Http.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Scanning.Provisional;

// Undoing a scan commit takes back what that commit made and nothing else. A kept row promoted during the same session is an
// item the person made on purpose: its row points at it, but the commit did not make it, so undo neither deletes it nor trips on it.
public class ProvisionalPromoteUndoTests(ApiFixture api) : ProvisionalTest(api)
{
    private static Task<HttpResponseMessage> PromoteAsync(HttpClient client, Guid libraryId, Guid provisionalId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{ProvisionalUrl(libraryId, provisionalId)}:promote")
        {
            Content = JsonContent.Create(new { }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> UndoAsync(HttpClient client, Guid sessionId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{SessionUrl(sessionId)}/commit:undo");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }

    private static async Task<Guid> KeepAndPromoteAsync(HttpClient client, Guid libraryId, Guid sessionId, string text)
    {
        var candidateId = Id((await IdentifyUnreadableAsync(client, sessionId, text))[0]);
        var kept = await KeepAsync(client, sessionId, new { candidateId });
        kept.StatusCode.ShouldBe(HttpStatusCode.Created, await kept.Content.ReadAsStringAsync());
        var provisionalId = (await ReadJsonAsync(kept)).GetProperty("provisional").GetProperty("id").GetGuid();
        var promoted = await PromoteAsync(client, libraryId, provisionalId);
        promoted.StatusCode.ShouldBe(HttpStatusCode.OK, await promoted.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(promoted)).GetProperty("createdItemId").GetGuid();
    }

    [Fact]
    public async Task Undo_takes_back_the_commits_items_and_leaves_an_item_promoted_from_a_kept_row_in_the_same_session()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(64701);
        await CatalogAsync("Wave Race 64", code);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(editor, library.Id);
        var promotedId = await KeepAndPromoteAsync(editor, library.Id, sessionId, "Zzxq Kept Spine Qqz");
        var scanned = Id((await ScanAsync(editor, sessionId, code))[0]);
        (await DecideAsync(editor, sessionId, scanned, new { decision = "accepted" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var commit = await CommitAsync(editor, sessionId);
        commit.StatusCode.ShouldBe(HttpStatusCode.OK, await commit.Content.ReadAsStringAsync());
        var committed = (await ReadJsonAsync(commit)).GetProperty("itemIds").EnumerateArray().Select(e => e.GetGuid()).ToList();
        committed.ShouldNotContain(promotedId);
        (await ScanReaders.ItemsAsync(Db, library.Id)).Count.ShouldBe(2);

        var undo = await UndoAsync(editor, sessionId);

        undo.StatusCode.ShouldBe(HttpStatusCode.OK, await undo.Content.ReadAsStringAsync());
        (await ReadJsonAsync(undo)).GetProperty("removed").GetInt32().ShouldBe(1);
        (await ScanReaders.ItemsAsync(Db, library.Id)).Select(i => i.Id).ShouldBe([promotedId]);
        (await GetSessionAsync(editor, sessionId)).GetProperty("status").GetString().ShouldBe("open");
    }

    [Fact]
    public async Task Undo_forgets_the_item_on_the_committed_row_but_keeps_the_link_on_the_promoted_row()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(64702);
        await CatalogAsync("1080 Snowboarding", code);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(editor, library.Id);
        var keptCandidate = Id((await IdentifyUnreadableAsync(editor, sessionId))[0]);
        var kept = await KeepAsync(editor, sessionId, new { candidateId = keptCandidate });
        var provisionalId = (await ReadJsonAsync(kept)).GetProperty("provisional").GetProperty("id").GetGuid();
        var promotedId = (await ReadJsonAsync(await PromoteAsync(editor, library.Id, provisionalId))).GetProperty("createdItemId").GetGuid();
        var scanned = Id((await ScanAsync(editor, sessionId, code))[0]);
        (await DecideAsync(editor, sessionId, scanned, new { decision = "accepted" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CommitAsync(editor, sessionId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await UndoAsync(editor, sessionId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await CandidateAsync(scanned)).CreatedItemId.ShouldBeNull("the row's item was taken back");
        (await CandidateAsync(keptCandidate)).CreatedItemId.ShouldBe(promotedId, "the promoted item still stands, and so does its link");
    }

    [Fact]
    public async Task Undo_is_still_refused_when_an_item_the_commit_made_was_edited_even_with_a_promoted_row_beside_it()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(64703);
        await CatalogAsync("Star Fox 64", code);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(editor, library.Id);
        var promotedId = await KeepAndPromoteAsync(editor, library.Id, sessionId, "Zzxq Another Kept Spine Qqz");
        var scanned = Id((await ScanAsync(editor, sessionId, code))[0]);
        (await DecideAsync(editor, sessionId, scanned, new { decision = "accepted" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var commit = await CommitAsync(editor, sessionId);
        var madeId = (await ReadJsonAsync(commit)).GetProperty("itemIds").EnumerateArray().Single().GetGuid();
        await using (var context = Db.CreateContext())
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"update items set updated_at = updated_at + interval '1 minute' where id = {madeId}");
        }

        var undo = await UndoAsync(editor, sessionId);

        undo.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadJsonAsync(undo)).GetProperty("code").GetString().ShouldBe("scan.undo_items_changed");
        (await ScanReaders.ItemsAsync(Db, library.Id)).Select(i => i.Id).ShouldBe([promotedId, madeId], ignoreOrder: true);
        (await GetSessionAsync(editor, sessionId)).GetProperty("status").GetString().ShouldBe("committed");
    }
}
