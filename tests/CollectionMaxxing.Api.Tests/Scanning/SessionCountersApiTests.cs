using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Scanning.Identify;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Api.Tests.Scanning;

/// <summary>The session carries its live counters and per-snap segments (docs/16 §5).</summary>
public class SessionCountersApiTests(ApiFixture api) : IdentifyTest(api)
{
    private static int Counter(JsonElement session, string name) => session.GetProperty("counters").GetProperty(name).GetInt32();

    [Fact]
    public async Task A_fresh_session_counts_zero_and_lists_no_segments()
    {
        var library = await CreateLibraryAsync();
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        var session = await GetSessionAsync(viewer, sessionId);

        Counter(session, "captured").ShouldBe(0);
        Counter(session, "added").ShouldBe(0);
        Counter(session, "toReview").ShouldBe(0);
        Counter(session, "owned").ShouldBe(0);
        Counter(session, "skipped").ShouldBe(0);
        session.GetProperty("segments").EnumerateArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task Counters_reconcile_at_rest_after_commit()
    {
        var library = await CreateLibraryAsync();
        var added = ScanData.Upc(9201);
        var owned = ScanData.Upc(9202);
        var review = ScanData.Upc(9203);
        var skipped = ScanData.Upc(9204);
        await CatalogAsync("Counted Game", added);
        await OwnAsync(library, "Owned Game", owned);
        Products.Know(review, ScanData.Product("Obscure Game", review));
        await CatalogAsync("Skipped Game", skipped);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(editor, library.Id);
        var candidates = await ScanAsync(editor, sessionId, added, owned, review, skipped);
        var byCode = candidates.ToDictionary(c => c.GetProperty("rawCode").GetString()!);
        Resolution(byCode[ScanData.Gtin13(added)]).ShouldBe("matched");
        Resolution(byCode[ScanData.Gtin13(owned)]).ShouldBe("matched");
        Resolution(byCode[ScanData.Gtin13(review)]).ShouldBe("not_found");
        (await DecideAsync(editor, sessionId, Id(byCode[ScanData.Gtin13(skipped)]), new { decision = "rejected" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CommitAsync(editor, sessionId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var session = await GetSessionAsync(editor, sessionId);

        Counter(session, "captured").ShouldBe(4);
        Counter(session, "added").ShouldBe(1);
        Counter(session, "toReview").ShouldBe(1);
        Counter(session, "owned").ShouldBe(1);
        Counter(session, "skipped").ShouldBe(1);
        (Counter(session, "added") + Counter(session, "toReview") + Counter(session, "owned") + Counter(session, "skipped"))
            .ShouldBe(Counter(session, "captured"));
    }

    [Fact]
    public async Task Counters_and_segments_cover_the_whole_session_when_since_filters_candidates()
    {
        var library = await CreateLibraryAsync();
        var first = ScanData.Upc(9211);
        var second = ScanData.Upc(9212);
        await CatalogAsync("First Game", first);
        await CatalogAsync("Second Game", second);
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);
        await ScanAsync(viewer, sessionId, first);
        var seenUpTo = (await GetSessionAsync(viewer, sessionId)).GetProperty("candidates").EnumerateArray()
            .Max(c => c.GetProperty("changeSeq").GetInt32());
        await ScanAsync(viewer, sessionId, second);

        var later = await GetSessionAsync(viewer, sessionId, since: seenUpTo);

        later.GetProperty("candidates").EnumerateArray().Count().ShouldBe(1);
        Counter(later, "captured").ShouldBe(2);
    }

    [Fact]
    public async Task Segments_list_each_snap_in_shoot_order_with_state()
    {
        var library = await CreateLibraryAsync();
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);
        var firstSnap = Guid.NewGuid();
        var secondSnap = Guid.NewGuid();
        (await IdentifyAsync(viewer, sessionId, firstSnap, Read(title: "The Legend Of Zorblax Quest", kind: "game", platform: "SNES")))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await IdentifyAsync(
            viewer, sessionId, secondSnap,
            Read(title: "Zorblax Quest II Electric Boogaloo", kind: "game"), Read(title: "Zorblax Quest III The Reckoning", kind: "game")))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var session = await GetSessionAsync(viewer, sessionId);
        var segments = session.GetProperty("segments").EnumerateArray().ToList();

        segments.Count.ShouldBe(2);
        segments[0].GetProperty("snapId").GetGuid().ShouldBe(firstSnap);
        segments[0].GetProperty("trigger").GetString().ShouldBe("auto");
        segments[0].GetProperty("shotKind").GetString().ShouldBe("unknown");
        segments[0].GetProperty("itemCount").GetInt32().ShouldBe(1);
        segments[0].GetProperty("state").GetString().ShouldBe("needs_eye");
        segments[0].GetProperty("candidateIds").EnumerateArray().Count().ShouldBe(1);
        segments[1].GetProperty("snapId").GetGuid().ShouldBe(secondSnap);
        segments[1].GetProperty("itemCount").GetInt32().ShouldBe(2);
        segments[1].GetProperty("candidateIds").EnumerateArray().Count().ShouldBe(2);
    }

    [Fact]
    public async Task A_segment_is_reviewed_once_every_candidate_is_decided()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var snapId = Guid.NewGuid();
        var response = await IdentifyAsync(editor, sessionId, snapId, Read(title: "The Legend Of Zorblax Quest", kind: "game"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var candidateId = (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().First().GetProperty("id").GetGuid();
        (await DecideAsync(editor, sessionId, candidateId, new { decision = "rejected" })).StatusCode.ShouldBe(HttpStatusCode.OK);

        var segments = (await GetSessionAsync(editor, sessionId)).GetProperty("segments").EnumerateArray().ToList();

        segments.Count.ShouldBe(1);
        segments[0].GetProperty("state").GetString().ShouldBe("reviewed");
    }
}
