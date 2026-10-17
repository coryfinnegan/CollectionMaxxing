using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Scanning.Photo;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Scanning;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace CollectionMaxxing.Api.Tests.Scanning;

// LOCKED acceptance tests of SCAN-1e (issue #149): Undo of a scan commit removes the items it created for good and puts the results back
// in Review. The tests marked LOCKED are the brief's six; do not edit or weaken them.
public class ScanUndoCommitTests : PhotoTest
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);

    public ScanUndoCommitTests(ApiFixture api)
        : base(api)
    {
        ConfigureServices = services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(_clock);
        };
    }

    private static string UndoUrl(Guid sessionId) => $"{SessionUrl(sessionId)}/commit:undo";

    private static Task<HttpResponseMessage> UndoAsync(HttpClient client, Guid sessionId, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, UndoUrl(sessionId));
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }

    private static async Task<string> CodeOfAsync(HttpResponseMessage response) =>
        (await ReadJsonAsync(response)).GetProperty("code").GetString()!;

    /// <summary>Scans <paramref name="count"/> catalogue games into a fresh session as the library's editor. All are accepted.</summary>
    private async Task<(TestLibrary Library, HttpClient Editor, Guid SessionId, List<JsonElement> Candidates)> ScannedAsync(int seed, int count = 3)
    {
        var library = await CreateLibraryAsync();
        var codes = Enumerable.Range(0, count).Select(n => ScanData.Upc(seed + n)).ToArray();
        foreach (var code in codes)
        {
            await CatalogAsync($"Undo game {code}", code);
        }

        var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidates = await ScanAsync(editor, sessionId, codes);
        candidates.ShouldAllBe(c => Decision(c) == "accepted");
        return (library, editor, sessionId, candidates);
    }

    private static async Task<List<Guid>> CommitOkAsync(HttpClient client, Guid sessionId, string? key = null)
    {
        var response = await CommitAsync(client, sessionId, key);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response)).GetProperty("itemIds").EnumerateArray().Select(e => e.GetGuid()).ToList();
    }

    /// <summary>The candidates as the API shows them, keyed by id, without the change counter (which moves with every change).</summary>
    private static Dictionary<Guid, string> Shown(JsonElement session) => session.GetProperty("candidates").EnumerateArray().ToDictionary(
        c => c.GetProperty("id").GetGuid(),
        c => JsonSerializer.Serialize(c.EnumerateObject().Where(p => p.Name != "changeSeq").ToDictionary(p => p.Name, p => p.Value)));

    private async Task<List<Item>> EverythingAsync(Guid libraryId) => await ScanReaders.ItemsAsync(Db, libraryId);

    private static async Task<int> DeletedListCountAsync(HttpClient client, Guid libraryId)
    {
        var response = await client.GetAsync($"/api/v1/libraries/{libraryId}/items:deleted");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await ReadJsonAsync(response)).GetProperty("items").GetArrayLength();
    }

    // ---- LOCKED 1 ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Undo_removes_the_three_new_items_for_good_and_reopens_the_session_with_every_decision_edit_and_copy_as_before()
    {
        var (library, editor, sessionId, candidates) = await ScannedAsync(29100, count: 4);
        var kept = await TestData.CreateItemAsync(Db, library); // an older item of the library: undo never touches it
        (await DecideAsync(editor, sessionId, Id(candidates[3]), new { decision = "rejected" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await DecideAsync(editor, sessionId, Id(candidates[1]), new { decision = "accepted", edition = "Player's Choice" })).StatusCode
            .ShouldBe(HttpStatusCode.OK);
        var before = await GetSessionAsync(editor, sessionId);
        var itemIds = await CommitOkAsync(editor, sessionId);
        itemIds.Count.ShouldBe(3);
        (await EverythingAsync(library.Id)).Count.ShouldBe(4);

        var response = await UndoAsync(editor, sessionId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await ReadJsonAsync(response)).GetProperty("removed").GetInt32().ShouldBe(3);
        var items = await EverythingAsync(library.Id); // query filters ignored: not even a soft-deleted row is left
        items.Select(i => i.Id).ShouldBe([kept]);
        (await DeletedListCountAsync(editor, library.Id)).ShouldBe(0); // not in Recently deleted

        var after = await GetSessionAsync(editor, sessionId);
        after.GetProperty("status").GetString().ShouldBe("open");
        after.GetProperty("committedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        Shown(after).ShouldBe(Shown(before)); // decisions, edits, copies and matches exactly as before, no created item ids
        after.GetProperty("candidates").EnumerateArray().ShouldAllBe(c => c.GetProperty("createdItemId").ValueKind == JsonValueKind.Null);
    }

    // ---- LOCKED 2 ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Undo_after_the_window_is_409_and_nothing_changes()
    {
        var (library, editor, sessionId, _) = await ScannedAsync(29110);
        await CommitOkAsync(editor, sessionId);
        _clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));

        var response = await UndoAsync(editor, sessionId);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOfAsync(response)).ShouldBe("scan.undo_window_passed");
        (await EverythingAsync(library.Id)).Count.ShouldBe(3);
        (await EverythingAsync(library.Id)).ShouldAllBe(i => i.DeletedAt == null);
        var session = await GetSessionAsync(editor, sessionId);
        session.GetProperty("status").GetString().ShouldBe("committed");
        session.GetProperty("candidates").EnumerateArray().ShouldAllBe(c => c.GetProperty("createdItemId").ValueKind == JsonValueKind.String);
    }

    [Fact]
    public async Task Undo_just_inside_the_window_works()
    {
        var (library, editor, sessionId, _) = await ScannedAsync(29120);
        await CommitOkAsync(editor, sessionId);
        _clock.Advance(TimeSpan.FromSeconds(119));

        (await UndoAsync(editor, sessionId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await EverythingAsync(library.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_window_can_be_configured()
    {
        Options.UndoWindow = TimeSpan.FromSeconds(30);
        var (library, editor, sessionId, _) = await ScannedAsync(29125);
        await CommitOkAsync(editor, sessionId);
        _clock.Advance(TimeSpan.FromSeconds(31));

        (await UndoAsync(editor, sessionId)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await EverythingAsync(library.Id)).Count.ShouldBe(3);
    }

    // ---- LOCKED 3 ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Undo_after_a_created_item_was_edited_is_409_and_nothing_changes()
    {
        var (library, editor, sessionId, _) = await ScannedAsync(29130);
        var itemIds = await CommitOkAsync(editor, sessionId);
        var itemUrl = $"/api/v1/libraries/{library.Id}/items/{itemIds[1]}";
        _clock.Advance(TimeSpan.FromSeconds(5)); // the frozen test clock would stamp the edit with the very moment of the commit
        var read = await editor.GetAsync(itemUrl);
        read.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var patch = new HttpRequestMessage(HttpMethod.Patch, itemUrl) { Content = new StringContent("""{"notes":"Mine now"}""", Encoding.UTF8, "application/json") };
        patch.Headers.TryAddWithoutValidation("If-Match", read.Headers.ETag!.Tag);
        (await editor.SendAsync(patch)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await UndoAsync(editor, sessionId);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOfAsync(response)).ShouldBe("scan.undo_items_changed");
        var items = await EverythingAsync(library.Id);
        items.Count.ShouldBe(3); // not even the two untouched ones went
        items.Single(i => i.Id == itemIds[1]).Notes.ShouldBe("Mine now");
        (await GetSessionAsync(editor, sessionId)).GetProperty("status").GetString().ShouldBe("committed");
    }

    [Fact]
    public async Task Undo_after_a_created_item_was_deleted_moved_or_tagged_is_409_and_nothing_changes()
    {
        foreach (var change in new[] { "deleted", "moved", "tagged" })
        {
            var (library, editor, sessionId, _) = await ScannedAsync(29140 + (Array.IndexOf(new[] { "deleted", "moved", "tagged" }, change) * 10));
            var itemIds = await CommitOkAsync(editor, sessionId);
            _clock.Advance(TimeSpan.FromSeconds(1)); // the frozen test clock would stamp the change with the very moment of the commit
            switch (change)
            {
                case "deleted":
                    var itemUrl = $"/api/v1/libraries/{library.Id}/items/{itemIds[0]}";
                    var read = await editor.GetAsync(itemUrl);
                    using (var delete = new HttpRequestMessage(HttpMethod.Delete, itemUrl))
                    {
                        delete.Headers.TryAddWithoutValidation("If-Match", read.Headers.ETag!.Tag);
                        (await editor.SendAsync(delete)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
                    }

                    break;
                case "moved":
                    var shelf = await TestData.CreateLocationAsync(Db, library.Id, "Shelf");
                    var bulk = await editor.PostAsync($"/api/v1/libraries/{library.Id}/items:bulk", Json(new { ids = new[] { itemIds[0] }, action = "patch", patch = new { locationId = shelf } }));
                    bulk.StatusCode.ShouldBe(HttpStatusCode.OK, await bulk.Content.ReadAsStringAsync());
                    break;
                default:
                    var tag = await TestData.CreateTagAsync(Db, library.Id, "Seeded tag");
                    var tagged = await editor.PostAsync($"/api/v1/libraries/{library.Id}/items:bulk", Json(new { ids = new[] { itemIds[0] }, action = "patch", patch = new { addTagIds = new[] { tag } } }));
                    tagged.StatusCode.ShouldBe(HttpStatusCode.OK, await tagged.Content.ReadAsStringAsync());
                    break;
            }

            var response = await UndoAsync(editor, sessionId);

            response.StatusCode.ShouldBe(HttpStatusCode.Conflict, change);
            (await EverythingAsync(library.Id)).Count.ShouldBe(3, change);
            (await GetSessionAsync(editor, sessionId)).GetProperty("status").GetString().ShouldBe("committed", change);
        }
    }

    [Fact]
    public async Task Undo_after_an_item_got_its_own_photo_is_409_and_nothing_changes()
    {
        var (library, editor, sessionId, _) = await ScannedAsync(29170);
        var itemIds = await CommitOkAsync(editor, sessionId);
        await using (var context = Db.CreateContext())
        {
            // A different cover choice, however it got there (the item's own photo is the one change the rest of the API can't make yet).
            await context.Items.IgnoreQueryFilters().Where(i => i.Id == itemIds[0])
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.CoverChoice, CoverChoice.MyPhoto).SetProperty(i => i.UpdatedAt, i => i.UpdatedAt.AddSeconds(1)));
        }

        (await UndoAsync(editor, sessionId)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await EverythingAsync(library.Id)).Count.ShouldBe(3);
    }

    // ---- LOCKED 4 ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Another_librarys_session_and_another_users_session_are_404_a_viewer_is_403_and_anonymous_is_401_and_nothing_changes()
    {
        var (library, editor, sessionId, _) = await ScannedAsync(29180);
        await CommitOkAsync(editor, sessionId);
        var stranger = await CreateLibraryAsync();
        using var elsewhere = As(stranger, LibraryRole.Owner); // owns another library: every role there
        using var owner = As(library, LibraryRole.Owner); // outranks the editor who started the session, but it is the editor's
        using var viewer = As(library, LibraryRole.Viewer);
        using var anonymous = Factory.CreateClient();

        (await UndoAsync(elsewhere, sessionId)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await UndoAsync(owner, sessionId)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await UndoAsync(viewer, sessionId)).StatusCode.ShouldBe(HttpStatusCode.NotFound); // someone else's session is 404 whatever the role
        (await UndoAsync(anonymous, sessionId)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // A viewer's own session: the role is what stops them.
        using var ownViewer = As(library, LibraryRole.Viewer);
        var viewerSession = await StartSessionAsync(ownViewer, library.Id);
        (await UndoAsync(ownViewer, viewerSession)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await EverythingAsync(library.Id)).Count.ShouldBe(3);
        (await GetSessionAsync(editor, sessionId)).GetProperty("status").GetString().ShouldBe("committed");
    }

    // ---- LOCKED 5 ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_photo_that_became_a_cover_is_back_on_its_snap_after_undo_and_its_file_is_kept_even_by_the_daily_clean_up()
    {
        var library = await CreateLibraryAsync();
        var (sessionId, candidateId, _) = await ScanOneAsync(library, number: 30);
        using var editor = As(library, LibraryRole.Editor);
        (await UploadAsync(editor, sessionId, candidateId, JpegFixtures.Plain())).StatusCode.ShouldBe(HttpStatusCode.OK);
        var photo = (await PhotoOfAsync(candidateId))!;
        var snapBefore = (await CandidateAsync(candidateId)).SnapId;
        var itemIds = await CommitOkAsync(editor, sessionId);
        (await ItemAsync(itemIds[0])).CustomCoverAssetId.ShouldBe(photo.Id);

        (await UndoAsync(editor, sessionId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await CountItemsAsync(library.Id)).ShouldBe(0);
        var candidate = await CandidateAsync(candidateId);
        candidate.SnapId.ShouldBe(snapBefore);
        (await PhotoOfAsync(candidateId))!.Id.ShouldBe(photo.Id); // the snap holds the asset again, as before the commit
        StoredFileExists(photo).ShouldBeTrue();
        using (var scope = Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<CollectionMaxxing.Application.Scanning.Learning.CleanUpScanSessions>()
                .HandleAsync(new CollectionMaxxing.Application.Scanning.Learning.CleanUpScanSessions.Job(), CancellationToken.None);
        }

        StoredFileExists(photo).ShouldBeTrue("the daily clean-up must not take a photo its snap still holds");
        (await AssetsOfAsync(library.Id)).Select(a => a.Id).ShouldBe([photo.Id]);

        // And it is a cover again after the person adds the session once more.
        var again = await CommitOkAsync(editor, sessionId);
        (await ItemAsync(again[0])).CustomCoverAssetId.ShouldBe(photo.Id);
    }

    // ---- LOCKED 6 ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Undoing_twice_replays_the_same_answer_for_the_same_key_and_is_409_not_committed_for_a_new_key_and_removes_nothing_more()
    {
        var (library, editor, sessionId, _) = await ScannedAsync(29190);
        await CommitOkAsync(editor, sessionId);
        var first = await UndoAsync(editor, sessionId, "undo-key-1");
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var firstBody = await first.Content.ReadAsStringAsync();
        var newItem = await TestData.CreateItemAsync(Db, library); // added by hand after the undo: a repeat must not touch it

        var replay = await UndoAsync(editor, sessionId, "undo-key-1");
        var another = await UndoAsync(editor, sessionId, "undo-key-2");

        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await replay.Content.ReadAsStringAsync()).ShouldBe(firstBody); // the same key gets the first answer back
        another.StatusCode.ShouldBe(HttpStatusCode.Conflict); // a new request: the session isn't committed
        (await CodeOfAsync(another)).ShouldBe("scan.not_committed");
        (await EverythingAsync(library.Id)).Select(i => i.Id).ShouldBe([newItem]);
        (await GetSessionAsync(editor, sessionId)).GetProperty("status").GetString().ShouldBe("open");
    }

    // ---- More ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Undo_of_a_session_that_was_never_committed_is_409_and_changes_nothing()
    {
        var (_, editor, sessionId, candidates) = await ScannedAsync(29200, count: 1);

        var response = await UndoAsync(editor, sessionId);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOfAsync(response)).ShouldBe("scan.not_committed");
        var session = await GetSessionAsync(editor, sessionId);
        session.GetProperty("status").GetString().ShouldBe("open");
        Decision(session.GetProperty("candidates")[0]).ShouldBe(Decision(candidates[0]));
    }

    [Fact]
    public async Task Undo_needs_an_idempotency_key()
    {
        var (library, editor, sessionId, _) = await ScannedAsync(29205, count: 1);
        await CommitOkAsync(editor, sessionId);

        var response = await editor.PostAsync(UndoUrl(sessionId), content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await EverythingAsync(library.Id)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Undo_removes_every_copy_and_everything_that_hangs_from_the_items_but_nothing_else()
    {
        var (library, editor, sessionId, candidates) = await ScannedAsync(29210, count: 2);
        (await DecideAsync(editor, sessionId, Id(candidates[0]), new { decision = "accepted", copies = 3 })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var itemIds = await CommitOkAsync(editor, sessionId);
        itemIds.Count.ShouldBe(4);
        var releaseId = (await ItemAsync(itemIds[0])).ReleaseId;
        var tag = await TestData.CreateTagAsync(Db, library.Id, "Stays");
        await using (var context = Db.CreateContext())
        {
            // A feature print the phone backed up for a new item: derived data, so it goes with the item. (A tag would be a change: see above.)
            context.ItemFeaturePrints.Add(new ItemFeaturePrint
            {
                Id = Guid.CreateVersion7(), LibraryId = library.Id, ItemId = itemIds[0], ReleaseId = releaseId, Data = [1, 2, 3], Revision = 1, CreatedAt = DateTimeOffset.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        var response = await UndoAsync(editor, sessionId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await ReadJsonAsync(response)).GetProperty("removed").GetInt32().ShouldBe(4);
        (await EverythingAsync(library.Id)).ShouldBeEmpty();
        await using var check = Db.CreateContext();
        (await check.ItemFeaturePrints.IgnoreQueryFilters().CountAsync(p => p.LibraryId == library.Id)).ShouldBe(0);
        (await check.ScanCandidates.IgnoreQueryFilters().Where(c => c.SessionId == sessionId).ToListAsync()).ShouldAllBe(c => c.CreatedItemId == null);
        (await check.Tags.IgnoreQueryFilters().AnyAsync(t => t.Id == tag)).ShouldBeTrue(); // the library's own tags are not the items'
        (await check.Releases.IgnoreQueryFilters().AnyAsync(r => r.Id == releaseId)).ShouldBeTrue(); // the catalogue is not touched
    }

    [Fact]
    public async Task The_reopened_session_and_its_candidates_replay_through_the_change_counter()
    {
        var (_, editor, sessionId, _) = await ScannedAsync(29220);
        await CommitOkAsync(editor, sessionId);
        var committed = await GetSessionAsync(editor, sessionId);
        var seen = committed.GetProperty("candidates").EnumerateArray().Max(c => c.GetProperty("changeSeq").GetInt32());

        (await UndoAsync(editor, sessionId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var since = await GetSessionAsync(editor, sessionId, since: seen);
        since.GetProperty("status").GetString().ShouldBe("open");
        var changed = since.GetProperty("candidates").EnumerateArray().ToList();
        changed.Count.ShouldBe(3); // every candidate that gave up its item is sent again
        changed.ShouldAllBe(c => c.GetProperty("createdItemId").ValueKind == JsonValueKind.Null && c.GetProperty("changeSeq").GetInt32() > seen);
    }

    [Fact]
    public async Task After_undo_the_person_can_fix_things_and_add_again_with_the_old_or_a_new_key()
    {
        var (library, editor, sessionId, candidates) = await ScannedAsync(29230);
        await CommitOkAsync(editor, sessionId, "commit-1");
        (await UndoAsync(editor, sessionId)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await DecideAsync(editor, sessionId, Id(candidates[2]), new { decision = "rejected" })).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The key of the first commit answered with items that no longer exist: it must not hand that answer back.
        var again = await CommitOkAsync(editor, sessionId, "commit-1");

        again.Count.ShouldBe(2);
        (await EverythingAsync(library.Id)).Select(i => i.Id).Order().ShouldBe(again.Order());
        (await GetSessionAsync(editor, sessionId)).GetProperty("status").GetString().ShouldBe("committed");
    }

    [Fact]
    public async Task Undo_is_recorded_in_the_audit_trail_with_counts_only()
    {
        var (library, editor, sessionId, _) = await ScannedAsync(29240);
        await CommitOkAsync(editor, sessionId);

        (await UndoAsync(editor, sessionId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var context = Db.CreateContext();
        var events = await context.AuditEvents.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.Action == "scan.commit_undone" && e.LibraryId == library.Id).ToListAsync();
        events.Count.ShouldBe(1);
        events[0].EntityId.ShouldBe(sessionId);
        events[0].DetailsJson.ShouldNotBeNull().ShouldContain("3");
    }
}
