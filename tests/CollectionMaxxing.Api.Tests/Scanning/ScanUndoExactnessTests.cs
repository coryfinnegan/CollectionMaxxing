using System.Net;
using CollectionMaxxing.Api.Tests.Scanning.Photo;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace CollectionMaxxing.Api.Tests.Scanning;

// SCAN-1e fix round 1: Undo removes exactly the items, links and releases its commit made, by id, and a phone that synced learns they are gone.
public class ScanUndoExactnessTests : PhotoTest
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);

    public ScanUndoExactnessTests(ApiFixture api)
        : base(api)
    {
        ConfigureServices = services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(_clock);
        };
    }

    private static Task<HttpResponseMessage> UndoAsync(HttpClient client, Guid sessionId, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{SessionUrl(sessionId)}/commit:undo");
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }

    private async Task<(TestLibrary Library, HttpClient Editor, Guid SessionId, List<System.Text.Json.JsonElement> Candidates, string[] Codes)> ScannedAsync(
        int seed, int count = 3)
    {
        var library = await CreateLibraryAsync();
        var codes = Enumerable.Range(0, count).Select(n => ScanData.Upc(seed + n)).ToArray();
        foreach (var code in codes)
        {
            await CatalogAsync($"Exact game {code}", code);
        }

        var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(editor, library.Id); // these tests pin commit/undo, not auto-add (#341)
        var candidates = await ScanAsync(editor, sessionId, codes);
        return (library, editor, sessionId, candidates, codes);
    }

    private static async Task<List<Guid>> CommitOkAsync(HttpClient client, Guid sessionId, string? key = null)
    {
        var response = await CommitAsync(client, sessionId, key);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response)).GetProperty("itemIds").EnumerateArray().Select(e => e.GetGuid()).ToList();
    }

    private async Task<DateTimeOffset> CommittedAtAsync(Guid sessionId)
    {
        await using var context = Db.CreateContext();
        return (await context.ScanSessions.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == sessionId)).CommittedAt!.Value;
    }

    private async Task<Guid> InsertItemAsync(TestLibrary library, Guid createdBy, DateTimeOffset at)
    {
        var item = new Item
        {
            Id = Guid.CreateVersion7(),
            LibraryId = library.Id,
            ReleaseId = await TestData.CreateReleaseAsync(Db),
            Status = ItemStatus.Owned,
            CoverChoice = CoverChoice.Release,
            AddedVia = AddedVia.Manual,
            CreatedAt = at,
            CreatedBy = createdBy,
            UpdatedAt = at,
        };
        await using var context = Db.CreateContext();
        context.Items.Add(item);
        await context.SaveChangesAsync();
        return item.Id;
    }

    private async Task<HashSet<Guid>> ItemIdsAsync(Guid libraryId) => (await ScanReaders.ItemsAsync(Db, libraryId)).Select(i => i.Id).ToHashSet();

    // ---- 1. exact ids ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("same user, same instant")]
    [InlineData("same user, older")]
    [InlineData("another user, same instant")]
    public async Task Undo_deletes_exactly_the_items_the_commit_made_and_never_a_lookalike(string lookalike)
    {
        var (library, editor, sessionId, _, _) = await ScannedAsync(30000 + (Array.IndexOf(["same user, same instant", "same user, older", "another user, same instant"], lookalike) * 10));
        var made = await CommitOkAsync(editor, sessionId);
        var committedAt = await CommittedAtAsync(sessionId);
        var other = lookalike switch
        {
            "same user, same instant" => await InsertItemAsync(library, library.EditorId, committedAt),
            "same user, older" => await InsertItemAsync(library, library.EditorId, committedAt.AddHours(-1)),
            _ => await InsertItemAsync(library, library.OwnerId, committedAt),
        };

        var response = await UndoAsync(editor, sessionId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await ReadJsonAsync(response)).GetProperty("removed").GetInt32().ShouldBe(3);
        (await ItemIdsAsync(library.Id)).ShouldBe([other]);
        made.ShouldNotContain(other);
    }

    [Fact]
    public async Task Undo_is_409_and_changes_nothing_when_one_of_the_commits_items_is_gone_even_if_a_lookalike_makes_the_count_match()
    {
        var (library, editor, sessionId, candidates, _) = await ScannedAsync(30040, count: 2);
        (await DecideAsync(editor, sessionId, Id(candidates[0]), new { decision = "accepted", copies = 2 })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var made = await CommitOkAsync(editor, sessionId);
        made.Count.ShouldBe(3);
        var committedAt = await CommittedAtAsync(sessionId);
        await using (var context = Db.CreateContext())
        {
            await context.Items.IgnoreQueryFilters().Where(i => i.Id == made[1]).ExecuteDeleteAsync(); // the second copy vanished
        }

        var lookalike = await InsertItemAsync(library, library.EditorId, committedAt); // the same user, the same instant: three items again

        var response = await UndoAsync(editor, sessionId);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOfAsync(response)).ShouldBe("scan.undo_items_changed");
        (await ItemIdsAsync(library.Id)).ShouldBe([made[0], made[2], lookalike], ignoreOrder: true);
        (await GetSessionAsync(editor, sessionId)).GetProperty("status").GetString().ShouldBe("committed");
    }

    private static async Task<string> CodeOfAsync(HttpResponseMessage response) =>
        (await ReadJsonAsync(response)).GetProperty("code").GetString()!;

    // ---- 2. ghost ownership -------------------------------------------------------------------------------

    [Fact]
    public async Task A_phone_that_synced_after_the_commit_is_told_by_the_next_sync_that_the_undone_items_are_gone()
    {
        var (library, editor, sessionId, _, _) = await ScannedAsync(30050);
        var made = await CommitOkAsync(editor, sessionId);
        var viewer = As(library, LibraryRole.Viewer);
        var url = $"/api/v1/libraries/{library.Id}/ownership/snapshot";
        var first = await ReadJsonAsync(await viewer.GetAsync(url));
        first.GetProperty("entries").EnumerateArray().Where(e => !e.GetProperty("deleted").GetBoolean()).Select(e => e.GetProperty("itemId").GetGuid())
            .ShouldBe(made, ignoreOrder: true);
        var cursor = first.GetProperty("nextCursor").GetString()!;
        _clock.Advance(TimeSpan.FromSeconds(10));

        (await UndoAsync(editor, sessionId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var delta = await ReadJsonAsync(await viewer.GetAsync($"{url}?since={Uri.EscapeDataString(cursor)}"));
        var entries = delta.GetProperty("entries").EnumerateArray().ToList();
        entries.Select(e => e.GetProperty("itemId").GetGuid()).ShouldBe(made, ignoreOrder: true);
        entries.ShouldAllBe(e => e.GetProperty("deleted").GetBoolean());
        var next = delta.GetProperty("nextCursor").GetString()!;
        (await ReadJsonAsync(await viewer.GetAsync($"{url}?since={Uri.EscapeDataString(next)}"))).GetProperty("entries").GetArrayLength().ShouldBe(0);
        (await ReadJsonAsync(await viewer.GetAsync(url))).GetProperty("entries").GetArrayLength().ShouldBe(0); // a fresh phone sees none
    }

    // ---- 3. learned links ---------------------------------------------------------------------------------

    private async Task<bool> HasCodeAsync(Guid releaseId, string code)
    {
        Identifier.TryParse(code, out var id).ShouldBeTrue();
        await using var context = Db.CreateContext();
        return await context.ReleaseIdentifiers.AnyAsync(i => i.ReleaseId == releaseId && i.Value == id!.Value);
    }

    /// <summary>The person picked another release for a scanned code (what Fix it does): the candidate now points at <paramref name="releaseId"/>.</summary>
    private async Task RepickAsync(Guid candidateId, Guid releaseId)
    {
        await using var context = Db.CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync($"update scan_candidates set matched_release_id = {releaseId} where id = {candidateId}");
    }

    [Fact]
    public async Task Undo_forgets_the_barcode_link_its_commit_learned_so_a_repick_and_add_again_leaves_the_next_scan_a_clean_match()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(30060);
        var right = await CatalogAsync("Right release", code);
        var wrong = await CatalogAsync("Wrong release", ScanData.Upc(30069));
        var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(editor, library.Id);
        var candidate = Id((await ScanAsync(editor, sessionId, code))[0]);
        await RepickAsync(candidate, wrong);
        await CommitOkAsync(editor, sessionId);
        (await HasCodeAsync(wrong, code)).ShouldBeTrue("the commit learned the link");

        (await UndoAsync(editor, sessionId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await HasCodeAsync(wrong, code)).ShouldBeFalse("undo took back what the commit taught");
        (await HasCodeAsync(right, code)).ShouldBeTrue("the catalogue's own link stays");
        await RepickAsync(candidate, right);
        await CommitOkAsync(editor, sessionId);
        var next = await StartSessionWithoutAutoCommitAsync(editor, library.Id);
        var again = (await ScanAsync(editor, next, code))[0];
        Resolution(again).ShouldBe("matched");
        again.GetProperty("release").GetProperty("id").GetGuid().ShouldBe(right);
    }

    [Fact]
    public async Task Undo_keeps_a_link_an_earlier_commit_learned_even_when_the_undone_commit_used_it_too()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(30070);
        await CatalogAsync("Provider release", code);
        var taught = await CatalogAsync("Taught release", ScanData.Upc(30079));
        var editor = As(library, LibraryRole.Editor);

        var first = await StartSessionWithoutAutoCommitAsync(editor, library.Id);
        await RepickAsync(Id((await ScanAsync(editor, first, code))[0]), taught);
        await CommitOkAsync(editor, first);
        (await HasCodeAsync(taught, code)).ShouldBeTrue();

        var second = await StartSessionWithoutAutoCommitAsync(editor, library.Id);
        var candidate = (await ScanAsync(editor, second, code)).Select(Id).First(); // two releases carry the code now: the person picks
        (await DecideAsync(editor, second, candidate, new { decision = "accepted", releaseId = taught })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await CommitOkAsync(editor, second);

        var undo = await UndoAsync(editor, second);
        undo.StatusCode.ShouldBe(HttpStatusCode.OK, await undo.Content.ReadAsStringAsync());

        (await HasCodeAsync(taught, code)).ShouldBeTrue("the first commit's link is not this commit's to take back");
    }

    [Fact]
    public async Task Undo_deletes_the_private_release_the_commit_made_when_nothing_else_uses_it_and_keeps_it_when_something_does()
    {
        var (library, editor, sessionId, candidates, codes) = await ScannedAsync(30080, count: 1);
        (await DecideAsync(editor, sessionId, Id(candidates[0]), new { decision = "accepted", edition = "Collector Box" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await CommitOkAsync(editor, sessionId);
        (await PrivateReleasesAsync(library.Id)).Count.ShouldBe(1);

        (await UndoAsync(editor, sessionId)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await PrivateReleasesAsync(library.Id)).ShouldBeEmpty();

        // The same edit committed by two sessions shares one private release: undoing one leaves it for the other.
        var firstSession = await StartSessionWithoutAutoCommitAsync(editor, library.Id);
        var firstCandidate = (await ScanAsync(editor, firstSession, codes[0]))[0];
        (await DecideAsync(editor, firstSession, Id(firstCandidate), new { decision = "accepted", edition = "Collector Box" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await CommitOkAsync(editor, firstSession);
        var shared = (await PrivateReleasesAsync(library.Id)).Single();
        _clock.Advance(TimeSpan.FromSeconds(1));
        var secondSession = await StartSessionWithoutAutoCommitAsync(editor, library.Id);
        var secondCandidate = (await ScanAsync(editor, secondSession, codes[0]))[0];
        (await DecideAsync(editor, secondSession, Id(secondCandidate), new { decision = "accepted", edition = "Collector Box" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await CommitOkAsync(editor, secondSession);
        (await PrivateReleasesAsync(library.Id)).ShouldBe([shared]);

        (await UndoAsync(editor, firstSession)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await PrivateReleasesAsync(library.Id)).ShouldBe([shared]);
    }

    private async Task<List<Guid>> PrivateReleasesAsync(Guid libraryId)
    {
        await using var context = Db.CreateContext();
        return await context.Releases.IgnoreQueryFilters().AsNoTracking().Where(r => r.OwnerLibraryId == libraryId).Select(r => r.Id).ToListAsync();
    }

    // ---- 5. a late duplicate -------------------------------------------------------------------------------

    [Fact]
    public async Task A_late_duplicate_of_an_old_undo_key_after_the_person_added_again_does_not_undo_the_new_commit()
    {
        var (library, editor, sessionId, _, _) = await ScannedAsync(30090);
        await CommitOkAsync(editor, sessionId, "commit-1");
        var undone = await UndoAsync(editor, sessionId, "undo-1");
        undone.StatusCode.ShouldBe(HttpStatusCode.OK);
        var undoneBody = await undone.Content.ReadAsStringAsync();
        var again = await CommitOkAsync(editor, sessionId, "commit-2");

        var late = await UndoAsync(editor, sessionId, "undo-1");

        late.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await late.Content.ReadAsStringAsync()).ShouldBe(undoneBody);
        (await ItemIdsAsync(library.Id)).ShouldBe(again, ignoreOrder: true);
        (await GetSessionAsync(editor, sessionId)).GetProperty("status").GetString().ShouldBe("committed");
    }
}
