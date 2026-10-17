using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Catalog;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Scanning.Identify;

public class IdentifyBehaviourTests(ApiFixture api) : IdentifyTest(api)
{
    private static string ReleaseIdOf(JsonElement candidate) => candidate.GetProperty("release").GetProperty("id").GetString()!;

    // ---- grounding and scoring -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_vhs_read_matches_the_vhs_release_not_the_dvd_of_the_same_film()
    {
        var library = await CreateLibraryAsync();
        var (_, vhs) = await CatalogSeed.AddAsync(Db, "The Matrix", WorkKind.Film, 1999, format: ReleaseFormat.Vhs);
        await CatalogSeed.AddAsync(Db, "The Matrix", WorkKind.Film, 1999, format: ReleaseFormat.Dvd);
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        var candidates = await IdentifyOkAsync(viewer, sessionId, Read(title: "The Matrix", kind: "film", format: "vhs", completeness: "complete"));

        Resolution(candidates[0]).ShouldBe("matched");
        ReleaseIdOf(candidates[0]).ShouldBe(vhs.ToString());
    }

    [Fact]
    public async Task A_vhs_read_never_comes_back_matched_to_a_dvd_when_that_is_all_there_is()
    {
        var library = await CreateLibraryAsync();
        await CatalogSeed.AddAsync(Db, "The Matrix", WorkKind.Film, 1999, format: ReleaseFormat.Dvd);
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        var candidates = await IdentifyOkAsync(viewer, sessionId, Read(title: "The Matrix", kind: "film", format: "vhs", completeness: "complete"));

        Resolution(candidates[0]).ShouldBe("ambiguous");
        Decision(candidates[0]).ShouldBe("undecided");
    }

    [Fact]
    public async Task An_invented_title_nothing_knows_is_not_found()
    {
        var library = await CreateLibraryAsync();
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        var candidates = await IdentifyOkAsync(viewer, sessionId, Read(title: "The Legend Of Zorblax Quest", kind: "game", platform: "SNES"));

        Resolution(candidates[0]).ShouldBe("not_found");
        candidates[0].GetProperty("visibleText").GetString().ShouldBe("The Legend Of Zorblax Quest");
    }

    [Fact]
    public async Task A_provider_that_times_out_leaves_only_that_item_pending_and_the_request_still_succeeds()
    {
        Options.ResolveBudget = TimeSpan.FromMilliseconds(300);
        Options.JobResolveBudget = TimeSpan.FromMilliseconds(300);
        var library = await CreateLibraryAsync();
        await CatalogAsync("Star Fox 64", ScanData.Upc(6001));
        Games.Behaviour = ct => Task.Delay(Timeout.Infinite, ct);
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        var candidates = await IdentifyOkAsync(
            viewer, sessionId, Read(title: "Star Fox 64", kind: "game"), Read(title: "The Legend Of Zorblax Quest", kind: "game"));

        Resolution(candidates[0]).ShouldBe("matched"); // the catalogue answered on its own
        Resolution(candidates[1]).ShouldBe("pending");

        await RunNextJobAsync(); // still down: the item gives up and the person can Fix it by hand
        var stored = (await GetSessionAsync(viewer, sessionId)).GetProperty("candidates");
        Resolution(stored[1]).ShouldBe("error");
        Resolution(stored[0]).ShouldBe("matched");
    }

    [Fact]
    public async Task A_pending_item_is_resolved_by_the_job_and_arrives_with_its_default_decision()
    {
        Options.ResolveBudget = TimeSpan.FromMilliseconds(300);
        var library = await CreateLibraryAsync();
        var slow = true;
        Games.Results.Add(ScanData.Game("Zorblax Quest IV", "Nintendo 64", 1998, "g-zorblax"));
        Games.Behaviour = async ct =>
        {
            if (slow)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
        };
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        Resolution((await IdentifyOkAsync(editor, sessionId, Read(title: "Zorblax Quest IV", kind: "game")))[0]).ShouldBe("pending");

        slow = false;
        await RunNextJobAsync();

        var resolved = (await GetSessionAsync(editor, sessionId)).GetProperty("candidates")[0];
        Resolution(resolved).ShouldBe("matched");
        Decision(resolved).ShouldBe("accepted");
    }

    [Fact]
    public async Task The_job_never_overwrites_a_decision_the_person_already_made()
    {
        Options.ResolveBudget = TimeSpan.FromMilliseconds(300);
        var library = await CreateLibraryAsync();
        var slow = true;
        Games.Results.Add(ScanData.Game("Zorblax Quest IV", "Nintendo 64", 1998, "g-zorblax"));
        Games.Behaviour = async ct =>
        {
            if (slow)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
        };
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var pending = (await IdentifyOkAsync(editor, sessionId, Read(title: "Zorblax Quest IV", kind: "game")))[0];

        await using (var context = Db.CreateContext())
        {
            var row = await context.ScanCandidates.IgnoreQueryFilters().SingleAsync(c => c.Id == Id(pending));
            row.Decision = CandidateDecision.Rejected; // the person skipped it while it was still resolving
            await context.SaveChangesAsync();
        }

        slow = false;
        await RunNextJobAsync();

        var resolved = (await GetSessionAsync(editor, sessionId)).GetProperty("candidates")[0];
        Resolution(resolved).ShouldBe("matched");
        Decision(resolved).ShouldBe("rejected");
    }

    [Fact]
    public async Task A_near_miss_pair_comes_back_as_alternatives_with_the_right_one_first()
    {
        var library = await CreateLibraryAsync();
        await CatalogSeed.AddAsync(Db, "Metroid Prime 2: Echoes", WorkKind.Game, 2004, format: ReleaseFormat.GameDisc);
        await CatalogSeed.AddAsync(Db, "Metroid Prime", WorkKind.Game, 2002, format: ReleaseFormat.GameDisc);
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        var candidates = await IdentifyOkAsync(viewer, sessionId, Read(title: "Metroid Prime", kind: "game", format: "game_disc"));

        var titles = AlternativeTitles(candidates[0]);
        titles.ShouldContain("Metroid Prime 2: Echoes");
        titles[0].ShouldBe("Metroid Prime");
    }

    [Fact]
    public async Task An_exactly_owned_release_defaults_to_skip_and_another_format_of_an_owned_work_defaults_to_add()
    {
        var library = await CreateLibraryAsync();
        await OwnAsync(library, "Metroid Prime", ScanData.Upc(6101), ReleaseFormat.GameDisc, year: 2002);
        await CatalogSeed.AddAsync(Db, "The Matrix", WorkKind.Film, 1999, format: ReleaseFormat.Vhs);
        await OwnAsync(library, "The Matrix", null, ReleaseFormat.Dvd, WorkKind.Film, 1999);
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        var candidates = await IdentifyOkAsync(
            viewer, sessionId,
            Read(title: "Metroid Prime", kind: "game", format: "game_disc"),
            Read(title: "The Matrix", kind: "film", format: "vhs"));

        Resolution(candidates[0]).ShouldBe("matched");
        candidates[0].GetProperty("alreadyOwnedCount").GetInt32().ShouldBe(1);
        Decision(candidates[0]).ShouldBe("rejected"); // Skip
        Resolution(candidates[1]).ShouldBe("matched");
        candidates[1].GetProperty("alreadyOwnedCount").GetInt32().ShouldBe(0);
        Decision(candidates[1]).ShouldBe("accepted"); // Add
    }

    // ---- the phone's own feature-print claim -----------------------------------------------------------------------

    [Fact]
    public async Task A_local_match_release_id_that_is_global_resolves_as_matched()
    {
        var library = await CreateLibraryAsync();
        var releaseId = await CatalogAsync("Star Fox 64", ScanData.Upc(6201));
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        var candidates = await IdentifyOkAsync(
            viewer, sessionId, Read(title: "A Misread Title", kind: "game", localMatchReleaseId: releaseId));

        Resolution(candidates[0]).ShouldBe("matched");
        ReleaseIdOf(candidates[0]).ShouldBe(releaseId.ToString());
        Decision(candidates[0]).ShouldBe("accepted");
    }

    [Fact]
    public async Task A_local_match_release_id_of_the_sessions_own_librarys_private_release_resolves_as_matched()
    {
        var library = await CreateLibraryAsync();
        var (_, releaseId) = await CatalogSeed.AddAsync(
            Db, "My Custom Cart", WorkKind.Game, 1999, ownerLibraryId: library.Id, format: ReleaseFormat.GameCartridge);
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        var candidates = await IdentifyOkAsync(viewer, sessionId, Read(kind: "game", text: "scrawled label", localMatchReleaseId: releaseId));

        Resolution(candidates[0]).ShouldBe("matched");
        ReleaseIdOf(candidates[0]).ShouldBe(releaseId.ToString());
    }

    // ---- the snap and the candidates it makes ----------------------------------------------------------------------

    [Theory]
    [InlineData("timeout", FallbackReason.Timeout)]
    [InlineData("unavailable", FallbackReason.Unavailable)]
    public async Task A_model_timeout_is_stored_apart_from_an_outage_so_stats_can_tell_them_apart(string sent, FallbackReason stored)
    {
        var library = await CreateLibraryAsync();
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);
        var snapId = Guid.NewGuid();
        var body = Body(snapId, [Read(title: "One", kind: "game")]);
        body["engine"] = "vision_pipeline";
        body["fellBackFrom"] = sent;

        var response = await viewer.PostAsync(IdentifyUrl(sessionId), Json(body));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        await using var context = Db.CreateContext();
        var snap = await context.ScanSnaps.IgnoreQueryFilters().SingleAsync(s => s.Id == snapId);
        snap.FellBack.ShouldBeTrue();
        snap.FallbackReason.ShouldBe(stored);
    }

    [Fact]
    public async Task A_snap_is_stored_as_metadata_only_with_its_trigger_engine_item_count_and_fallback_reason()
    {
        var library = await CreateLibraryAsync();
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);
        var snapId = Guid.NewGuid();
        var body = Body(snapId, [Read(title: "One", kind: "game"), Read(title: "Two", kind: "game")]);
        body["trigger"] = "shutter";
        body["engine"] = "vision_pipeline";
        body["fellBackFrom"] = "declined";

        var response = await viewer.PostAsync(IdentifyUrl(sessionId), Json(body));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        await using var context = Db.CreateContext();
        var snap = await context.ScanSnaps.IgnoreQueryFilters().SingleAsync(s => s.Id == snapId);
        snap.SessionId.ShouldBe(sessionId);
        snap.LibraryId.ShouldBe(library.Id);
        snap.Trigger.ShouldBe(SnapTrigger.Shutter);
        snap.Engine.ShouldBe(ScanEngine.VisionPipeline);
        snap.FellBack.ShouldBeTrue();
        snap.FallbackReason.ShouldBe(FallbackReason.Declined);
        snap.ItemCount.ShouldBe(2);
        snap.AssetId.ShouldBeNull();
        var candidates = await context.ScanCandidates.IgnoreQueryFilters().Where(c => c.SessionId == sessionId).ToListAsync();
        candidates.ShouldAllBe(c => c.SnapId == snapId && c.Source == CandidateSource.PictureText);
    }

    [Fact]
    public async Task The_apple_model_engine_makes_apple_model_candidates_and_a_clean_snap_did_not_fall_back()
    {
        var library = await CreateLibraryAsync();
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        var candidates = await IdentifyOkAsync(viewer, sessionId, Read(title: "One", kind: "game", localId: "item-1"));

        candidates[0].GetProperty("source").GetString().ShouldBe("picture_apple_model");
        candidates[0].GetProperty("localId").GetString().ShouldBe("item-1");
        await using var context = Db.CreateContext();
        (await context.ScanSnaps.IgnoreQueryFilters().SingleAsync(s => s.SessionId == sessionId)).FellBack.ShouldBeFalse();
    }

    [Fact]
    public async Task What_the_phone_read_is_kept_as_inferred_chips_the_card_can_show()
    {
        var library = await CreateLibraryAsync();
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        var candidates = await IdentifyOkAsync(
            viewer, sessionId, Read(title: "Zelda", kind: "game", format: "game_cartridge", platform: "N64", completeness: "cart_only", year: 1998));

        var inferred = candidates[0].GetProperty("inferred");
        inferred.GetProperty("completeness").GetString().ShouldBe("cart_only");
        inferred.GetProperty("platform").GetString().ShouldBe("N64");
        inferred.GetProperty("profile").GetString().ShouldBe("LooseCartridge");
    }

    [Fact]
    public async Task Control_characters_are_stripped_from_what_the_phone_read()
    {
        var library = await CreateLibraryAsync();
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        var candidates = await IdentifyOkAsync(viewer, sessionId, Read(title: "Ze\u0007lda\u0000 II", text: "ZELDA\u0001\u001f II", kind: "game"));

        candidates[0].GetProperty("visibleText").GetString().ShouldBe("ZELDA II");
    }

    [Fact]
    public async Task An_item_with_nothing_readable_is_not_found_without_searching()
    {
        var library = await CreateLibraryAsync();
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        var candidates = await IdentifyOkAsync(viewer, sessionId, Read(kind: "game", platform: "SNES"));

        Resolution(candidates[0]).ShouldBe("not_found");
    }

    // ---- limits ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Forty_items_are_accepted_and_forty_one_are_refused()
    {
        var library = await CreateLibraryAsync();
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        var forty = await IdentifyAsync(viewer, sessionId, null, Enumerable.Range(0, 40).Select(i => (object)Read(title: $"Title {i}", kind: "book")).ToArray());
        var fortyOne = await IdentifyAsync(viewer, sessionId, null, Enumerable.Range(0, 41).Select(i => (object)Read(title: $"Title {i}", kind: "book")).ToArray());

        forty.StatusCode.ShouldBe(HttpStatusCode.OK, await forty.Content.ReadAsStringAsync());
        fortyOne.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await fortyOne.Content.ReadAsStringAsync()).ShouldContain("urn:collectionmaxxing:problem:");
        (await CountCandidatesAsync(sessionId)).ShouldBe(40);
    }

    [Fact]
    public async Task An_item_over_sixteen_kilobytes_is_refused_with_a_problem()
    {
        var library = await CreateLibraryAsync();
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        var response = await IdentifyAsync(viewer, sessionId, null, Read(title: "Big", text: new string('x', 17 * 1024), kind: "book"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await CountSnapsAsync(sessionId)).ShouldBe(0);
    }

    [Fact]
    public async Task A_body_over_256_kilobytes_is_413_not_a_server_error()
    {
        var library = await CreateLibraryAsync();
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);
        var items = Enumerable.Range(0, 20).Select(i => (object)Read(title: $"Big {i}", text: new string('x', 14 * 1024), kind: "book")).ToArray();

        var response = await IdentifyAsync(viewer, sessionId, null, items);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await CountSnapsAsync(sessionId)).ShouldBe(0);
    }

    [Fact]
    public async Task An_image_is_refused_with_a_problem()
    {
        var library = await CreateLibraryAsync();
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);
        using var content = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46]);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");

        var response = await viewer.PostAsync(IdentifyUrl(sessionId), content);

        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        (await CountSnapsAsync(sessionId)).ShouldBe(0);
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_a_400_problem()
    {
        var library = await CreateLibraryAsync();
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        var response = await viewer.PostAsync(IdentifyUrl(sessionId), new StringContent("{ not json", Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_request_without_a_snap_id_or_without_items_is_refused()
    {
        var library = await CreateLibraryAsync();
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        var noSnap = Body(Guid.NewGuid(), [Read(title: "One", kind: "game")]);
        noSnap["snapId"] = null;
        (await viewer.PostAsync(IdentifyUrl(sessionId), Json(noSnap))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await IdentifyAsync(viewer, sessionId, null)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await viewer.PostAsync(IdentifyUrl(sessionId), Json(new { }))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Identify_on_a_finished_session_is_a_conflict_but_a_retry_of_an_earlier_snap_still_gets_its_answer()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var snapId = Guid.NewGuid();
        (await IdentifyAsync(editor, sessionId, snapId, Read(title: "One", kind: "game"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await editor.DeleteAsync(SessionUrl(sessionId))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await IdentifyAsync(editor, sessionId, snapId, Read(title: "One", kind: "game"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await IdentifyAsync(editor, sessionId, null, Read(title: "Two", kind: "game"))).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_snap_id_already_used_in_another_session_is_a_conflict_that_reveals_nothing()
    {
        var library = await CreateLibraryAsync();
        var other = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        using var stranger = As(other, LibraryRole.Editor);
        var snapId = Guid.NewGuid();
        var strangersSession = await StartSessionAsync(stranger, other.Id);
        (await IdentifyAsync(stranger, strangersSession, snapId, Read(title: "Theirs", kind: "game"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var response = await IdentifyAsync(editor, sessionId, snapId, Read(title: "Mine", kind: "game"));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).ShouldNotContain("Theirs");
        (await CountCandidatesAsync(sessionId)).ShouldBe(0);
    }
}
