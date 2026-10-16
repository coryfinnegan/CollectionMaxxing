using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Catalog;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Scanning.Identify;

// LOCKED acceptance tests of SCAN-2a (issue #112): the server, not the on-device model, decides a match. Do not edit or weaken.
public class IdentifyLockedTests(ApiFixture api) : IdentifyTest(api)
{
    [Fact]
    public async Task A_high_confidence_read_with_no_catalogue_support_is_not_found_whatever_the_model_says()
    {
        var library = await CreateLibraryAsync();
        Games.Results.Add(ScanData.Game("Tetris", "Game Boy", 1989, "g-tetris"));
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        var confident = Read(title: "Zorblax Quest IV", kind: "game", platform: "Nintendo 64");
        confident["confidence"] = "high"; // not part of the contract; it must change nothing
        confident["marking"] = "high";
        var candidates = await IdentifyOkAsync(viewer, sessionId, confident);

        candidates.Count.ShouldBe(1);
        Resolution(candidates[0]).ShouldBe("not_found");
        Decision(candidates[0]).ShouldBe("undecided");
        candidates[0].GetProperty("release").ValueKind.ShouldBe(JsonValueKind.Null);
        candidates[0].GetProperty("visibleText").GetString().ShouldBe("Zorblax Quest IV"); // Fix it starts from what was read
    }

    [Fact]
    public async Task A_read_below_the_scorers_thresholds_is_never_auto_accepted_and_commit_ignores_it()
    {
        var library = await CreateLibraryAsync();
        await CatalogAsync("Metroid Prime 2: Echoes", ScanData.Upc(5101));
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var candidates = await IdentifyOkAsync(editor, sessionId, Read(title: "Metroid Fusion", kind: "game"));

        Resolution(candidates[0]).ShouldNotBe("matched");
        Resolution(candidates[0]).ShouldNotBe("pending");
        Decision(candidates[0]).ShouldBe("undecided");
        var commit = await CommitAsync(editor, sessionId);
        commit.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadJsonAsync(commit)).GetProperty("created").GetInt32().ShouldBe(0);
        (await CountItemsAsync(library.Id)).ShouldBe(0);
    }

    [Fact]
    public async Task A_local_match_release_id_that_is_not_the_librarys_to_use_is_ignored_never_echoed_and_leaks_nothing()
    {
        var library = await CreateLibraryAsync();
        var other = await CreateLibraryAsync();
        var (_, privateRelease) = await CatalogSeed.AddAsync(
            Db, "Secret Edition Of Hidden Title", WorkKind.Game, 1999, ownerLibraryId: other.Id, format: ReleaseFormat.GameCartridge);

        // A user who belongs to both libraries can see the other library's release through the filters; the claim must still fail.
        await using (var context = Db.CreateContext())
        {
            context.LibraryMembers.Add(new LibraryMember(other.Id, library.EditorId, LibraryRole.Viewer, DateTimeOffset.UtcNow));
            await context.SaveChangesAsync();
        }

        var missing = Guid.CreateVersion7();
        foreach (var (user, claim) in new[] { (library.ViewerId, privateRelease), (library.EditorId, privateRelease), (library.ViewerId, missing) })
        {
            using var client = AsUser(user);
            var sessionId = await StartSessionAsync(client, library.Id);

            var response = await IdentifyAsync(client, sessionId, null, Read(title: "Zorblax Quest IV", kind: "game", localMatchReleaseId: claim));
            var body = await response.Content.ReadAsStringAsync();

            response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
            body.ShouldNotContain(claim.ToString(), Case.Insensitive);
            body.ShouldNotContain("Secret Edition", Case.Insensitive);
            body.ShouldNotContain("Hidden Title", Case.Insensitive);
            var candidate = JsonDocument.Parse(body).RootElement.GetProperty("candidates")[0];
            Resolution(candidate).ShouldBe("not_found");
            candidate.GetProperty("release").ValueKind.ShouldBe(JsonValueKind.Null);
        }
    }

    [Fact]
    public async Task A_viewer_can_identify_and_sees_ownership_but_cannot_accept_patch_or_commit()
    {
        var library = await CreateLibraryAsync();
        var shelf = await TestData.CreateLocationAsync(Db, library.Id, "Shelf B");
        await OwnAsync(library, "Super Mario 64", ScanData.Upc(5201), locationId: shelf);
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        var candidates = await IdentifyOkAsync(viewer, sessionId, Read(title: "Super Mario 64", kind: "game"));

        Resolution(candidates[0]).ShouldBe("matched");
        candidates[0].GetProperty("alreadyOwnedCount").GetInt32().ShouldBe(1);
        candidates[0].GetProperty("ownedAt").EnumerateArray().Select(p => p.GetString()).ShouldBe(["Shelf B"]);
        var candidateId = Id(candidates[0]);
        (await DecideAsync(viewer, sessionId, candidateId, new { decision = "accepted" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await viewer.PostAsync($"{CandidateUrl(sessionId, candidateId)}:search", Json(new { text = "Mario" }))).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
        (await CommitAsync(viewer, sessionId)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CountItemsAsync(library.Id)).ShouldBe(1);
    }

    [Theory]
    [InlineData(LibraryRole.Viewer)]
    [InlineData(LibraryRole.Editor)]
    [InlineData(LibraryRole.Owner)]
    public async Task Identify_is_open_to_the_sessions_own_starter_in_every_library_role(LibraryRole role)
    {
        var library = await CreateLibraryAsync();
        using var client = As(library, role);
        var sessionId = await StartSessionAsync(client, library.Id);

        var response = await IdentifyAsync(client, sessionId, null, Read(title: "Anything", kind: "game"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Identify_is_404_for_another_librarys_or_another_users_session_and_401_when_signed_out()
    {
        var library = await CreateLibraryAsync();
        var other = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var item = Read(title: "Anything", kind: "game");

        using var strangersOwner = As(other, LibraryRole.Owner);
        (await IdentifyAsync(strangersOwner, sessionId, null, item)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var outsider = AsUser(library.OutsiderId);
        (await IdentifyAsync(outsider, sessionId, null, item)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var owner = As(library, LibraryRole.Owner); // outranks the editor, but the session is the editor's
        (await IdentifyAsync(owner, sessionId, null, item)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var anonymous = Factory.CreateClient();
        (await IdentifyAsync(anonymous, sessionId, null, item)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Nothing was written by any of them.
        (await CountCandidatesAsync(sessionId)).ShouldBe(0);
        (await CountSnapsAsync(sessionId)).ShouldBe(0);
    }

    [Fact]
    public async Task Re_posting_the_same_snap_id_returns_the_same_candidates_and_creates_no_duplicates()
    {
        var library = await CreateLibraryAsync();
        await CatalogAsync("Star Fox 64", ScanData.Upc(5301));
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);
        var snapId = Guid.NewGuid();
        var items = new object[] { Read(title: "Star Fox 64", kind: "game"), Read(title: "Zorblax Quest IV", kind: "game") };

        var one = await IdentifyAsync(viewer, sessionId, snapId, items);
        var two = await IdentifyAsync(viewer, sessionId, snapId, items);

        one.StatusCode.ShouldBe(HttpStatusCode.OK);
        two.StatusCode.ShouldBe(HttpStatusCode.OK);
        var first = (await ReadJsonAsync(one)).GetProperty("candidates").EnumerateArray().Select(Id).ToList();
        var again = (await ReadJsonAsync(two)).GetProperty("candidates").EnumerateArray().Select(Id).ToList();
        first.Count.ShouldBe(2);
        again.ShouldBe(first);
        (await CountCandidatesAsync(sessionId)).ShouldBe(2);
        (await CountSnapsAsync(sessionId)).ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_identical_posts_create_one_set_of_candidates()
    {
        var library = await CreateLibraryAsync();
        await CatalogAsync("Star Fox 64", ScanData.Upc(5302));
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);
        var snapId = Guid.NewGuid();
        var items = new object[] { Read(title: "Star Fox 64", kind: "game"), Read(title: "Zorblax Quest IV", kind: "game") };

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => IdentifyAsync(viewer, sessionId, snapId, items)));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
        var ids = new HashSet<Guid>();
        foreach (var response in responses)
        {
            foreach (var candidate in (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray())
            {
                ids.Add(Id(candidate));
            }
        }

        ids.Count.ShouldBe(2);
        (await CountCandidatesAsync(sessionId)).ShouldBe(2);
        (await CountSnapsAsync(sessionId)).ShouldBe(1);
    }

    [Fact]
    public async Task Item_text_titles_and_provider_keys_never_appear_in_logs_or_resolution_traces()
    {
        // The app's real provider adapters, configured with keys and pointed at a port nothing listens on: every call fails
        // the way an outage does, which is when the text and the key are most likely to be logged.
        UseRealProviders = true;
        Options.ResolveBudget = TimeSpan.FromSeconds(4);
        foreach (var provider in new[] { "OpenLibrary", "GoogleBooks", "UpcItemDb", "Tmdb", "Igdb" })
        {
            Settings[$"Providers:{provider}:BaseUrl"] = "http://127.0.0.1:9/";
            Settings[$"Providers:{provider}:RequestsPerSecond"] = "1000000000";
        }

        const string UpcKey = "upc-secret-key-7f3a9c";
        const string GoogleKey = "google-secret-key-4b81d2";
        const string TmdbKey = "tmdb-secret-key-90e6aa";
        const string IgdbSecret = "igdb-secret-value-c35f17";
        Settings["Providers:UpcItemDb:UserKey"] = UpcKey;
        Settings["Providers:GoogleBooks:ApiKey"] = GoogleKey;
        Settings["Providers:Tmdb:ApiKey"] = TmdbKey;
        Settings["Providers:Igdb:ClientId"] = "igdb-client-id";
        Settings["Providers:Igdb:ClientSecret"] = IgdbSecret;
        Settings["Providers:Igdb:TokenUrl"] = "http://127.0.0.1:9/oauth2/token";
        Settings["Providers:OpenLibrary:ContactEmail"] = "owner@example.test";
        Settings["Providers:Resilience:MaxRetryAttempts"] = "1";
        Settings["Providers:Resilience:RetryDelay"] = "00:00:00.050";
        Settings["Providers:Resilience:AttemptTimeout"] = "00:00:02";

        var library = await CreateLibraryAsync();
        await CatalogAsync("Chrono Trigger", ScanData.Upc(5401));
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        var response = await IdentifyAsync(
            viewer, sessionId, null,
            Read(title: "Zq Private Title 91", text: "zq-private-text-77 on a shelf", creator: "Zq Secret Author", kind: "book", localId: "zq-local-3"),
            Read(title: "Chrono Trigger", kind: "game", platform: "SNES"),
            Read(text: "zq-only-text-5521"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        await RunNextJobAsync();

        var secrets = new[]
        {
            "Zq Private Title 91", "Zq Private", "zq-private-text-77", "Zq Secret Author", "zq-only-text-5521", "zq-local-3",
            UpcKey, GoogleKey, TmdbKey, IgdbSecret,
        };
        Logs.Lines.Count.ShouldBeGreaterThan(0, "the capture should see the app's log lines");
        foreach (var secret in secrets)
        {
            Logs.Lines.Where(line => line.Contains(secret, StringComparison.OrdinalIgnoreCase))
                .ShouldBeEmpty($"a log line contains '{secret[..6]}…'");
        }

        await using var context = Db.CreateContext();
        var traces = await context.ScanCandidates.IgnoreQueryFilters().Select(c => c.TraceJson).ToListAsync();
        traces.ShouldNotBeEmpty();
        foreach (var secret in secrets)
        {
            traces.Where(t => t is not null && t.Contains(secret, StringComparison.OrdinalIgnoreCase))
                .ShouldBeEmpty($"a resolution trace contains '{secret[..6]}…'");
        }
    }
}
