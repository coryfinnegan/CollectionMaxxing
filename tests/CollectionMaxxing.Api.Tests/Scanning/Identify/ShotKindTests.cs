using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Scanning.Identify;

public class ShotKindTests(ApiFixture api) : IdentifyTest(api)
{
    [Fact]
    public async Task A_shot_kind_sent_with_identify_is_stored_on_the_snap_and_its_candidates_and_echoed_back()
    {
        var library = await CreateLibraryAsync();
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);
        var snapId = Guid.NewGuid();
        var segmentId = Guid.NewGuid().ToString("N");
        var body = Body(snapId, [Read(title: "Star Fox 64", kind: "game", segmentId: segmentId), Read(title: "Mario Kart 64", kind: "game", segmentId: segmentId)]);
        body["shotKind"] = "shelf_segment";

        var response = await viewer.PostAsync(IdentifyUrl(sessionId), Json(body));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var candidates = (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().ToList();

        candidates.Count.ShouldBe(2);
        candidates.ShouldAllBe(c => c.GetProperty("shotKind").GetString() == "shelf_segment");
        candidates.ShouldAllBe(c => c.GetProperty("segmentId").GetString() == segmentId);

        await using var context = Db.CreateContext();
        (await context.ScanSnaps.IgnoreQueryFilters().SingleAsync(s => s.Id == snapId)).ShotKind.ShouldBe(ShotKind.ShelfSegment);
        var stored = await context.ScanCandidates.IgnoreQueryFilters()
            .Where(c => c.SessionId == sessionId).OrderBy(c => c.Seq).ToListAsync();
        stored.Count.ShouldBe(2);
        stored.ShouldAllBe(c => c.ShotKind == ShotKind.ShelfSegment);
        stored.ShouldAllBe(c => c.SegmentId == segmentId);
    }

    [Fact]
    public async Task Each_item_keeps_its_own_segment_id()
    {
        var library = await CreateLibraryAsync();
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);
        var body = Body(
            Guid.NewGuid(),
            [Read(title: "Star Fox 64", kind: "game", segmentId: "left-half"), Read(title: "Mario Kart 64", kind: "game", segmentId: "right-half")]);
        body["shotKind"] = "shelf_segment";

        var response = await viewer.PostAsync(IdentifyUrl(sessionId), Json(body));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var candidates = (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().ToList();

        candidates.Select(c => c.GetProperty("segmentId").GetString()).ShouldBe(["left-half", "right-half"]);
    }

    [Fact]
    public async Task An_identify_without_a_shot_kind_is_stored_as_unknown()
    {
        var library = await CreateLibraryAsync();
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);
        var snapId = Guid.NewGuid();

        var response = await IdentifyAsync(viewer, sessionId, snapId, Read(title: "Star Fox 64", kind: "game"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var candidates = (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().ToList();

        candidates.Count.ShouldBe(1);
        candidates[0].GetProperty("shotKind").GetString().ShouldBe("unknown");
        candidates[0].GetProperty("segmentId").ValueKind.ShouldBe(JsonValueKind.Null);

        await using var context = Db.CreateContext();
        (await context.ScanSnaps.IgnoreQueryFilters().SingleAsync(s => s.Id == snapId)).ShotKind.ShouldBe(ShotKind.Unknown);
        (await context.ScanCandidates.IgnoreQueryFilters().SingleAsync(c => c.SessionId == sessionId)).ShotKind.ShouldBe(ShotKind.Unknown);
    }

    [Fact]
    public async Task A_shot_kind_the_server_does_not_know_is_refused_as_a_bad_body()
    {
        var library = await CreateLibraryAsync();
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);
        var body = Body(Guid.NewGuid(), [Read(title: "Star Fox 64", kind: "game")]);
        body["shotKind"] = "panorama";

        var response = await viewer.PostAsync(IdentifyUrl(sessionId), Json(body));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("scan.body_invalid");
    }
}
