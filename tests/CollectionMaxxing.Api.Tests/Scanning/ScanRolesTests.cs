using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;

namespace CollectionMaxxing.Api.Tests.Scanning;

// LOCKED acceptance tests of SCAN-1a (issue #61): who may scan and who may decide and commit. Do not edit or weaken.
public class ScanRolesTests(ApiFixture api) : ScanTest(api)
{
    [Fact]
    public async Task A_viewer_can_create_a_session_and_submit_barcodes_and_sees_ownership_but_cannot_accept_patch_or_commit()
    {
        var library = await CreateLibraryAsync();
        var shelf = await TestData.CreateLocationAsync(Db, library.Id, "Shelf B");
        var code = ScanData.Upc(1001);
        await OwnAsync(library, "Super Mario 64", code, locationId: shelf);
        using var viewer = As(library, LibraryRole.Viewer);

        var sessionId = await StartSessionAsync(viewer, library.Id);
        var candidates = await ScanAsync(viewer, sessionId, code);

        candidates.Count.ShouldBe(1);
        Resolution(candidates[0]).ShouldBe("matched");
        candidates[0].GetProperty("alreadyOwnedCount").GetInt32().ShouldBe(1);
        candidates[0].GetProperty("ownedAt").EnumerateArray().Select(p => p.GetString()).ShouldBe(["Shelf B"]);

        var candidateId = Id(candidates[0]);
        (await DecideAsync(viewer, sessionId, candidateId, new { decision = "accepted" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await viewer.PostAsync($"{CandidateUrl(sessionId, candidateId)}:search", Json(new { text = "Mario" }))).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
        (await CommitAsync(viewer, sessionId)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await viewer.DeleteAsync(SessionUrl(sessionId))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await CountItemsAsync(library.Id)).ShouldBe(1); // nothing was added
        (await GetSessionAsync(viewer, sessionId)).GetProperty("status").GetString().ShouldBe("open");
    }

    [Fact]
    public async Task An_editor_can_accept_patch_and_commit()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(1002);
        await CatalogAsync("Super Mario 64", code);
        using var editor = As(library, LibraryRole.Editor);

        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidates = await ScanAsync(editor, sessionId, code);
        var candidateId = Id(candidates[0]);

        (await DecideAsync(editor, sessionId, candidateId, new { decision = "accepted", copies = 2 })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var commit = await CommitAsync(editor, sessionId);

        commit.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = await ReadJsonAsync(commit);
        result.GetProperty("created").GetInt32().ShouldBe(2);
        result.GetProperty("itemIds").GetArrayLength().ShouldBe(2);
        (await CountItemsAsync(library.Id)).ShouldBe(2);
    }

    [Fact]
    public async Task The_owner_can_commit_too()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(1003);
        await CatalogAsync("Wave Race 64", code);
        using var owner = As(library, LibraryRole.Owner);

        var sessionId = await StartSessionAsync(owner, library.Id);
        await ScanAsync(owner, sessionId, code);

        (await CommitAsync(owner, sessionId)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CountItemsAsync(library.Id)).ShouldBe(1);
    }
}
