using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Scanning.Artwork;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Api.Tests.Scanning.Identify;

public class CoverIdentifyTests(ApiFixture api) : ArtworkTest(api)
{
    [Fact]
    public async Task A_cover_read_answers_ambiguous_with_the_top_three_and_their_artwork_and_a_tap_binds_it()
    {
        var library = await CreateLibraryAsync();
        var platformId = await PlatformAsync();
        var releases = new[]
        {
            await ReleaseAsync("Pokemon Stadium", platformId),
            await ReleaseAsync("Pokemon Stadium", platformId, edition: "Player's Choice"),
            await ReleaseAsync("Pokemon Stadium", platformId, edition: "Greatest Hits"),
        };
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var body = Body(Guid.NewGuid(), [Read(title: "Pokemon Stadium", kind: "game", platform: N64, year: 1999)]);
        body["kind"] = "cover";
        body["shotKind"] = "single_cover";

        var response = await editor.PostAsync(IdentifyUrl(sessionId), Json(body));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var candidate = (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().Single();

        Resolution(candidate).ShouldBe("ambiguous");
        candidate.GetProperty("needsArt").GetBoolean().ShouldBeTrue();
        var alternatives = Alternatives(candidate);
        alternatives.Count.ShouldBe(3);
        foreach (var alternative in alternatives)
        {
            alternative.GetProperty("artworkUrl").GetString().ShouldStartWith("/media/covers/");
        }
        alternatives.Select(a => a.GetProperty("releaseId").GetGuid()).Order().ShouldBe(releases.Select(r => r.Id).Order());

        var pick = alternatives[1].GetProperty("releaseId").GetGuid();
        var decided = await ReadOkAsync(await DecideAsync(editor, sessionId, Id(candidate), new { releaseId = pick }));

        Id(decided).ShouldBe(Id(candidate));
        Resolution(decided).ShouldBe("matched");
        decided.GetProperty("release").GetProperty("id").GetGuid().ShouldBe(pick);
        Decision(decided).ShouldBe("accepted");
        decided.GetProperty("guidanceCode").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_bound_cover_upgrades_the_row_in_place()
    {
        var library = await CreateLibraryAsync();
        var platformId = await PlatformAsync();
        var release = await ReleaseAsync("Pokemon Stadium", platformId);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var row = (await IdentifyOkAsync(editor, sessionId, Read(title: "A Game That Is Nowhere In The Catalogue")))[0];
        Resolution(row).ShouldBe("not_found");

        var bound = Body(Guid.NewGuid(), [Read(title: "Pokemon Stadium", kind: "game", platform: N64, year: 1999)]);
        bound["kind"] = "cover";
        bound["shotKind"] = "single_cover";
        bound["candidateId"] = Id(row);
        var response = await editor.PostAsync(IdentifyUrl(sessionId), Json(bound));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var upgraded = (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().Single();

        Id(upgraded).ShouldBe(Id(row));
        (await CountCandidatesAsync(sessionId)).ShouldBe(1);
        Resolution(upgraded).ShouldBe("matched");
        upgraded.GetProperty("release").GetProperty("id").GetGuid().ShouldBe(release.Id);
        Decision(upgraded).ShouldBe("accepted");
        upgraded.GetProperty("visibleText").GetString().ShouldBe("Pokemon Stadium");
        upgraded.GetProperty("guidanceCode").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("shelf", 1, "scan.bound_cover_kind")]
    [InlineData("cover", 2, "scan.bound_single_item")]
    public async Task A_bound_cover_needs_kind_cover_and_exactly_one_item(string kind, int items, string code)
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var row = (await IdentifyOkAsync(editor, sessionId, Read(title: "A Game That Is Nowhere In The Catalogue")))[0];
        var reads = Enumerable.Range(0, items).Select(n => Read(title: $"Pokemon Stadium {n}")).ToArray();
        var bound = Body(Guid.NewGuid(), reads);
        bound["kind"] = kind;
        bound["candidateId"] = Id(row);

        var response = await editor.PostAsync(IdentifyUrl(sessionId), Json(bound));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe(code);
    }

    [Fact]
    public async Task A_bound_cover_for_an_unknown_row_is_404()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var bound = Body(Guid.NewGuid(), [Read(title: "Pokemon Stadium")]);
        bound["kind"] = "cover";
        bound["candidateId"] = Guid.NewGuid();

        var response = await editor.PostAsync(IdentifyUrl(sessionId), Json(bound));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Repeating_a_bound_cover_replays_the_same_row()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var row = (await IdentifyOkAsync(editor, sessionId, Read(title: "A Game That Is Nowhere In The Catalogue")))[0];
        var bound = Body(Guid.NewGuid(), [Read(title: "Pokemon Stadium")]);
        bound["kind"] = "cover";
        bound["candidateId"] = Id(row);

        var first = await editor.PostAsync(IdentifyUrl(sessionId), Json(bound));
        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        var retry = await editor.PostAsync(IdentifyUrl(sessionId), Json(bound));
        retry.StatusCode.ShouldBe(HttpStatusCode.OK, await retry.Content.ReadAsStringAsync());

        Id((await ReadJsonAsync(retry)).GetProperty("candidates").EnumerateArray().Single()).ShouldBe(Id(row));
        (await CountCandidatesAsync(sessionId)).ShouldBe(1);
        (await CountSnapsAsync(sessionId)).ShouldBe(2);
    }

    [Fact]
    public async Task A_kind_the_server_does_not_know_is_refused_as_a_bad_body()
    {
        var library = await CreateLibraryAsync();
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);
        var body = Body(Guid.NewGuid(), [Read(title: "Star Fox 64", kind: "game")]);
        body["kind"] = "panorama";

        var response = await viewer.PostAsync(IdentifyUrl(sessionId), Json(body));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("scan.body_invalid");
    }

    [Fact]
    public async Task A_cover_read_is_scored_as_a_cover_whatever_the_item_count()
    {
        var library = await CreateLibraryAsync();
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);
        var items = Enumerable.Range(0, 4).Select(n => Read(title: $"Nowhere Book {n}")).ToArray();
        var cover = Body(Guid.NewGuid(), items);
        cover["kind"] = "cover";

        var coverResponse = await viewer.PostAsync(IdentifyUrl(sessionId), Json(cover));
        coverResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await coverResponse.Content.ReadAsStringAsync());
        var coverCandidates = (await ReadJsonAsync(coverResponse)).GetProperty("candidates").EnumerateArray().ToList();
        var shelfCandidates = await IdentifyOkAsync(viewer, sessionId, items);

        coverCandidates.ShouldAllBe(c => c.GetProperty("inferred").GetProperty("profile").GetString() == "Cover");
        shelfCandidates.ShouldAllBe(c => c.GetProperty("inferred").GetProperty("profile").GetString() == "ShelfSpine");
    }
}
