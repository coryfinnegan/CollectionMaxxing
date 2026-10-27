using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Scanning;

// Undoing a T0 auto-add (issue #341): it removes exactly the event's items and takes back exactly what it taught.
// The tests marked LOCKED pin that precision. Do not weaken, skip or delete them.
public class UndoAutoCommitTests : ScanTest
{
    public UndoAutoCommitTests(ApiFixture api)
        : base(api)
    {
    }

    private static Task<HttpResponseMessage> UndoAutoAsync(HttpClient client, Guid sessionId, Guid? candidateId = null, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{SessionUrl(sessionId)}/auto-commit:undo")
        {
            Content = Json(new { candidateId }),
        };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }

    private static async Task<string> CodeOfAsync(HttpResponseMessage response) =>
        (await ReadJsonAsync(response)).GetProperty("code").GetString()!;

    private async Task<bool> HasCodeAsync(Guid releaseId, string code)
    {
        Identifier.TryParse(code, out var id).ShouldBeTrue();
        await using var context = Db.CreateContext();
        return await context.ReleaseIdentifiers.AsNoTracking().AnyAsync(i => i.ReleaseId == releaseId && i.Value == id!.Value);
    }

    private static async Task TouchAsync(HttpClient client, Guid libraryId, Guid itemId)
    {
        var itemUrl = $"/api/v1/libraries/{libraryId}/items/{itemId}";
        var read = await client.GetAsync(itemUrl);
        read.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var patch = new HttpRequestMessage(HttpMethod.Patch, itemUrl) { Content = Json(new { notes = "Mine now" }) };
        patch.Headers.TryAddWithoutValidation("If-Match", read.Headers.ETag!.Tag);
        (await client.SendAsync(patch)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>Scans two catalogue games that both auto-add, and returns the session with the two candidates in scan order.</summary>
    private async Task<(TestLibrary Library, HttpClient Editor, Guid SessionId, List<JsonElement> Candidates, string[] Codes)> AutoAddedAsync(int seed)
    {
        var library = await CreateLibraryAsync();
        var codes = new[] { ScanData.Upc(seed), ScanData.Upc(seed + 1) };
        foreach (var code in codes)
        {
            await CatalogAsync($"Auto game {code}", code);
        }

        var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithAutoCommitAsync(editor, library.Id);
        var candidates = await ScanAsync(editor, sessionId, codes[0], codes[1]);
        candidates.Count.ShouldBe(2);
        candidates.ShouldAllBe(c => c.GetProperty("createdItemId").ValueKind == JsonValueKind.String);
        return (library, editor, sessionId, candidates, codes);
    }

    // ---- LOCKED: precision ----------------------------------------------------------------------------------

    [Fact]
    public async Task Undo_removes_exactly_the_latest_auto_add_and_reopens_its_candidate_with_its_evidence()
    {
        var (library, editor, sessionId, candidates, codes) = await AutoAddedAsync(34130);
        var kept = candidates[0].GetProperty("createdItemId").GetGuid();
        var undoneRelease = candidates[1].GetProperty("release").GetProperty("id").GetGuid();

        var response = await UndoAutoAsync(editor, sessionId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var undone = await ReadJsonAsync(response);
        undone.GetProperty("removed").GetInt32().ShouldBe(1);
        var items = await ScanReaders.ItemsAsync(Db, library.Id);
        items.Select(i => i.Id).ShouldBe([kept]);
        (await HasCodeAsync(undoneRelease, codes[1])).ShouldBeTrue("the catalogue's own link is never touched");

        var again = undone.GetProperty("candidate");
        again.GetProperty("id").GetGuid().ShouldBe(Id(candidates[1]));
        again.GetProperty("createdItemId").ValueKind.ShouldBe(JsonValueKind.Null);
        again.GetProperty("decision").GetString().ShouldBe("undecided");
        again.GetProperty("resolution").GetString().ShouldBe("matched");
        again.GetProperty("tier").GetString().ShouldBe("T0");
        again.GetProperty("release").GetProperty("id").GetGuid().ShouldBe(candidates[1].GetProperty("release").GetProperty("id").GetGuid());

        var session = await GetSessionAsync(editor, sessionId);
        session.GetProperty("status").GetString().ShouldBe("open");
    }

    [Fact]
    public async Task Undo_with_a_candidate_id_undoes_that_event_and_retracts_what_it_taught()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(34132);
        Products.Know(code, ScanData.Product("Wave Race 64", code));
        Games.Results.Add(ScanData.Game("Wave Race 64"));
        var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithAutoCommitAsync(editor, library.Id);
        var candidate = (await ScanAsync(editor, sessionId, code))[0];
        candidate.GetProperty("createdItemId").ValueKind.ShouldBe(JsonValueKind.String);
        var releaseId = candidate.GetProperty("release").GetProperty("id").GetGuid();
        (await HasCodeAsync(releaseId, code)).ShouldBeTrue("the auto-add taught the link");

        var response = await UndoAutoAsync(editor, sessionId, Id(candidate));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await ReadJsonAsync(response)).GetProperty("removed").GetInt32().ShouldBe(1);
        (await ScanReaders.ItemsAsync(Db, library.Id)).ShouldBeEmpty();
        (await HasCodeAsync(releaseId, code)).ShouldBeFalse("undo took back what the auto-add taught");
    }

    [Fact]
    public async Task Undo_twice_is_409_and_changes_nothing()
    {
        var (library, editor, sessionId, candidates, _) = await AutoAddedAsync(34134);

        (await UndoAutoAsync(editor, sessionId, Id(candidates[0]))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var response = await UndoAutoAsync(editor, sessionId, Id(candidates[0]));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOfAsync(response)).ShouldBe("scan.nothing_to_undo");
        (await ScanReaders.ItemsAsync(Db, library.Id)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Undo_after_an_item_changed_is_409_and_keeps_everything()
    {
        var (library, editor, sessionId, candidates, _) = await AutoAddedAsync(34136);
        await TouchAsync(editor, library.Id, candidates[1].GetProperty("createdItemId").GetGuid());

        var response = await UndoAutoAsync(editor, sessionId);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOfAsync(response)).ShouldBe("scan.undo_items_changed");
        (await ScanReaders.ItemsAsync(Db, library.Id)).Count.ShouldBe(2);
        var session = await GetSessionAsync(editor, sessionId);
        session.GetProperty("candidates").EnumerateArray()
            .First(c => c.GetProperty("id").GetGuid() == Id(candidates[1]))
            .GetProperty("createdItemId").ValueKind.ShouldBe(JsonValueKind.String);
    }

    [Fact]
    public async Task Full_undo_after_auto_adds_removes_only_what_the_commit_made()
    {
        var library = await CreateLibraryAsync();
        var (autoCode, commitCode) = (ScanData.Upc(34138), ScanData.Upc(34139));
        await CatalogAsync("Auto game", autoCode);
        await OwnAsync(library, "Commit game", commitCode);
        var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithAutoCommitAsync(editor, library.Id);
        var auto = (await ScanAsync(editor, sessionId, autoCode))[0];
        auto.GetProperty("createdItemId").ValueKind.ShouldBe(JsonValueKind.String);
        var owned = (await ScanAsync(editor, sessionId, commitCode))[0];
        owned.GetProperty("createdItemId").ValueKind.ShouldBe(JsonValueKind.Null);
        (await DecideAsync(editor, sessionId, Id(owned), new { decision = "accepted" })).StatusCode.ShouldBe(HttpStatusCode.OK);

        var commit = await CommitAsync(editor, sessionId);
        commit.StatusCode.ShouldBe(HttpStatusCode.OK, await commit.Content.ReadAsStringAsync());
        (await ReadJsonAsync(commit)).GetProperty("created").GetInt32().ShouldBe(1);
        (await ScanReaders.ItemsAsync(Db, library.Id)).Count.ShouldBe(3);

        var undo = new HttpRequestMessage(HttpMethod.Post, $"{SessionUrl(sessionId)}/commit:undo");
        undo.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var undone = await editor.SendAsync(undo);

        undone.StatusCode.ShouldBe(HttpStatusCode.OK, await undone.Content.ReadAsStringAsync());
        var items = await ScanReaders.ItemsAsync(Db, library.Id);
        items.Count.ShouldBe(2);
        items.Select(i => i.Id).ShouldContain(auto.GetProperty("createdItemId").GetGuid());
        var session = await GetSessionAsync(editor, sessionId);
        session.GetProperty("status").GetString().ShouldBe("open");
        session.GetProperty("candidates").EnumerateArray()
            .First(c => c.GetProperty("id").GetGuid() == Id(auto))
            .GetProperty("createdItemId").ValueKind.ShouldBe(JsonValueKind.String);
    }

    [Fact]
    public async Task Undo_keeps_the_link_a_standing_auto_add_still_uses()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(34140);
        var unknown = ScanData.Upc(34141);
        Products.Know(code, ScanData.Product("Wave Race 64", code));
        Games.Results.Add(ScanData.Game("Wave Race 64"));
        var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, new { autoCommitTiers = new[] { "T0" }, autoCommitCopies = true });
        var first = (await ScanAsync(editor, sessionId, code))[0];
        var bound = (await ScanAsync(editor, sessionId, unknown))[0];
        Resolution(bound).ShouldBe("not_found");

        // A barcode bound to another row skips the line dedupe: two events, one link.
        var bind = new HttpRequestMessage(HttpMethod.Post, $"{SessionUrl(sessionId)}/barcodes")
        {
            Content = Json(new { codes = new[] { new { value = code, symbology = "ean_13" } }, candidateId = Id(bound) }),
        };
        bind.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var boundResponse = await editor.SendAsync(bind);
        boundResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await boundResponse.Content.ReadAsStringAsync());
        var second = (await ReadJsonAsync(boundResponse)).GetProperty("candidates").EnumerateArray().Single();

        first.GetProperty("createdItemId").ValueKind.ShouldBe(JsonValueKind.String);
        second.GetProperty("createdItemId").ValueKind.ShouldBe(JsonValueKind.String);
        var releaseId = first.GetProperty("release").GetProperty("id").GetGuid();
        second.GetProperty("release").GetProperty("id").GetGuid().ShouldBe(releaseId);

        (await UndoAutoAsync(editor, sessionId, Id(first))).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await HasCodeAsync(releaseId, code)).ShouldBeTrue("the standing add still uses the link");
        (await UndoAutoAsync(editor, sessionId, Id(second))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await HasCodeAsync(releaseId, code)).ShouldBeFalse("no standing add uses the link any more");
    }

    // ---- Not locked: flows around the precision core ----------------------------------------------------------

    [Fact]
    public async Task Undo_after_commit_is_refused()
    {
        var (library, editor, sessionId, _, _) = await AutoAddedAsync(34142);
        var ownedCode = ScanData.Upc(34146);
        await OwnAsync(library, "Owned game", ownedCode);
        var owned = (await ScanAsync(editor, sessionId, ownedCode))[0];
        (await DecideAsync(editor, sessionId, Id(owned), new { decision = "accepted" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var commit = await CommitAsync(editor, sessionId);
        commit.StatusCode.ShouldBe(HttpStatusCode.OK, await commit.Content.ReadAsStringAsync());
        (await ReadJsonAsync(commit)).GetProperty("created").GetInt32().ShouldBe(1);

        var response = await UndoAutoAsync(editor, sessionId);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOfAsync(response)).ShouldBe("scan.session_closed");
    }

    [Fact]
    public async Task Full_undo_keeps_what_the_auto_adds_taught()
    {
        var library = await CreateLibraryAsync();
        var (autoCode, commitCode) = (ScanData.Upc(34144), ScanData.Upc(34145));
        Products.Know(autoCode, ScanData.Product("Auto game", autoCode));
        Games.Results.Add(ScanData.Game("Auto game"));
        await OwnAsync(library, "Commit game", commitCode);
        var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithAutoCommitAsync(editor, library.Id);
        var auto = (await ScanAsync(editor, sessionId, autoCode))[0];
        var autoRelease = auto.GetProperty("release").GetProperty("id").GetGuid();
        (await HasCodeAsync(autoRelease, autoCode)).ShouldBeTrue("the auto-add taught the link");
        var owned = (await ScanAsync(editor, sessionId, commitCode))[0];
        (await DecideAsync(editor, sessionId, Id(owned), new { decision = "accepted" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CommitAsync(editor, sessionId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var undo = new HttpRequestMessage(HttpMethod.Post, $"{SessionUrl(sessionId)}/commit:undo");
        undo.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        (await editor.SendAsync(undo)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await HasCodeAsync(autoRelease, autoCode)).ShouldBeTrue("the standing auto-add keeps what it taught");
    }
}
