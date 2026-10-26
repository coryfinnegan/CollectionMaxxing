using System.Net;
using System.Net.Http.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;

namespace CollectionMaxxing.Api.Tests.Scanning.Provisional;

/// <summary>Kept rows are their library's own: another library's member names the victim's ids and gets 404, changing nothing.</summary>
public class ProvisionalIsolationTests : ProvisionalTest
{
    public ProvisionalIsolationTests(ApiFixture api)
        : base(api)
    {
    }

    [Fact]
    public async Task Another_librarys_member_cannot_list_touch_or_see_our_kept_rows()
    {
        var victim = await CreateLibraryAsync();
        var other = await CreateLibraryAsync();
        using var editor = As(victim, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, victim.Id);
        var candidateId = Id((await IdentifyUnreadableAsync(editor, sessionId))[0]);
        var cropResponse = await UploadCropAsync(editor, sessionId, candidateId, JpegFixtures.Plain());
        var cropId = (await ReadJsonAsync(cropResponse)).GetProperty("assetId").GetGuid();
        var keep = await KeepAsync(editor, sessionId, new { candidateId, cropAssetId = cropId });
        var provisionalId = (await ReadJsonAsync(keep)).GetProperty("provisional").GetProperty("id").GetGuid();

        using var attacker = As(other, LibraryRole.Owner);

        // Through their own library: our rows are invisible and untouchable.
        var list = await attacker.GetAsync(LibraryProvisionalsUrl(other.Id));
        list.StatusCode.ShouldBe(HttpStatusCode.OK, await list.Content.ReadAsStringAsync());
        (await ReadJsonAsync(list)).GetProperty("items").EnumerateArray().Count().ShouldBe(0);

        var promote = new HttpRequestMessage(HttpMethod.Post, $"{ProvisionalUrl(other.Id, provisionalId)}:promote")
        {
            Content = JsonContent.Create(new { }),
        };
        promote.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        (await attacker.SendAsync(promote)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await attacker.PostAsync($"{ProvisionalUrl(other.Id, provisionalId)}:resolve", Json(new { itemId = Guid.NewGuid() })))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await attacker.DeleteAsync(ProvisionalUrl(other.Id, provisionalId))).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Through our session: not theirs, so not found.
        (await UploadCropAsync(attacker, sessionId, candidateId, JpegFixtures.Plain())).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await KeepAsync(attacker, sessionId, new { candidateId })).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Nothing changed.
        var kept = (await ProvisionalAsync(provisionalId)).ShouldNotBeNull();
        kept.ResolvedAt.ShouldBeNull();
        (await AssetAsync(cropId)).ShouldNotBeNull();
        StoredFileExists((await AssetAsync(cropId))!).ShouldBeTrue();
    }

    [Fact]
    public async Task A_viewer_may_list_but_not_keep_or_act()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidateId = Id((await IdentifyUnreadableAsync(editor, sessionId))[0]);
        var keep = await KeepAsync(editor, sessionId, new { candidateId });
        var provisionalId = (await ReadJsonAsync(keep)).GetProperty("provisional").GetProperty("id").GetGuid();

        // Sessions are their owner's: the viewer works in a session of their own, where the role (not the owner) answers.
        using var viewer = As(library, LibraryRole.Viewer);
        var viewerSessionId = await StartSessionAsync(viewer, library.Id);
        var viewerCandidateId = Id((await IdentifyUnreadableAsync(viewer, viewerSessionId))[0]);

        (await viewer.GetAsync(LibraryProvisionalsUrl(library.Id))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await UploadCropAsync(viewer, viewerSessionId, viewerCandidateId, JpegFixtures.Plain())).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await KeepAsync(viewer, viewerSessionId, new { candidateId = viewerCandidateId })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await viewer.DeleteAsync(ProvisionalUrl(library.Id, provisionalId))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
