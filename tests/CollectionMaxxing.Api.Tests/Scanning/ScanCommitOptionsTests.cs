using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;

namespace CollectionMaxxing.Api.Tests.Scanning;

// SCAN-1d (issue #105), contract item 2: Review commits in one transaction with the person's "Add to" choices. The tests marked
// LOCKED protect the library from a half-applied or cross-library commit; do not edit or weaken them. SCAN-1a's own locked
// tests (ScanCommitTests) stay as written: a commit without a body behaves exactly as before.
public class ScanCommitOptionsTests(ApiFixture api) : ScanTest(api)
{
    private static Task<HttpResponseMessage> CommitWithAsync(HttpClient client, Guid sessionId, object? body, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{SessionUrl(sessionId)}/commit") { Content = body is null ? null : Json(body) };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }

    private static Guid ReleaseOf(System.Text.Json.JsonElement candidate) => candidate.GetProperty("release").GetProperty("id").GetGuid();

    /// <summary>Two catalogue games scanned into a fresh session: the library, its editor, the session and the two candidates.</summary>
    private async Task<(TestLibrary Library, HttpClient Editor, Guid SessionId, System.Text.Json.JsonElement First, System.Text.Json.JsonElement Second)>
        TwoAcceptedAsync(int seed, object? sessionBody = null)
    {
        var library = await CreateLibraryAsync();
        var (a, b) = (ScanData.Upc(seed), ScanData.Upc(seed + 1));
        await CatalogAsync($"Game {a}", a);
        await CatalogAsync($"Game {b}", b);
        var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, sessionBody);
        var candidates = await ScanAsync(editor, sessionId, a, b);
        candidates.ShouldAllBe(c => Decision(c) == "accepted");
        return (library, editor, sessionId, candidates[0], candidates[1]);
    }

    [Fact]
    public async Task Add_to_and_status_apply_to_every_item_created()
    {
        var (library, editor, sessionId, _, _) = await TwoAcceptedAsync(7101);
        var shelf = await TestData.CreateLocationAsync(Db, library.Id, "Shelf");

        var response = await CommitWithAsync(editor, sessionId, new { addTo = shelf, status = "wishlist", condition = "good" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await ReadJsonAsync(response)).GetProperty("created").GetInt32().ShouldBe(2);
        var items = await ScanReaders.ItemsAsync(Db, library.Id);
        items.Count.ShouldBe(2);
        items.ShouldAllBe(i => i.LocationId == shelf && i.Status == ItemStatus.Wishlist && i.Condition == ItemCondition.Good);
    }

    [Fact]
    public async Task An_override_beats_the_default_for_its_candidate_only()
    {
        var (library, editor, sessionId, first, second) = await TwoAcceptedAsync(7111);
        var shelf = await TestData.CreateLocationAsync(Db, library.Id, "Shelf");
        var box = await TestData.CreateLocationAsync(Db, library.Id, "Box");

        var response = await CommitWithAsync(editor, sessionId, new
        {
            addTo = shelf,
            status = "owned",
            overrides = new[] { new { candidateId = Id(first), locationId = box, status = "lent", condition = "mint", copies = 2 } },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await ReadJsonAsync(response)).GetProperty("created").GetInt32().ShouldBe(3);
        var items = await ScanReaders.ItemsAsync(Db, library.Id);
        var ofFirst = items.Where(i => i.ReleaseId == ReleaseOf(first)).ToList();
        var ofSecond = items.Where(i => i.ReleaseId == ReleaseOf(second)).ToList();
        ofFirst.Count.ShouldBe(2);
        ofFirst.ShouldAllBe(i => i.LocationId == box && i.Status == ItemStatus.Lent && i.Condition == ItemCondition.Mint);
        ofSecond.Count.ShouldBe(1);
        ofSecond.ShouldAllBe(i => i.LocationId == shelf && i.Status == ItemStatus.Owned && i.Condition == null);
    }

    [Fact]
    public async Task Without_a_body_or_with_an_empty_one_the_session_defaults_apply_as_before()
    {
        var library = await CreateLibraryAsync();
        var shelf = await TestData.CreateLocationAsync(Db, library.Id, "Default shelf");
        var (a, b) = (ScanData.Upc(7121), ScanData.Upc(7122));
        await CatalogAsync("Game A", a);
        await CatalogAsync("Game B", b);
        using var editor = As(library, LibraryRole.Editor);
        var withBody = await StartSessionAsync(editor, library.Id, new { defaultLocationId = shelf, defaultStatus = "wishlist" });
        var withoutBody = await StartSessionAsync(editor, library.Id);
        await ScanAsync(editor, withBody, a);
        await ScanAsync(editor, withoutBody, b);

        (await CommitWithAsync(editor, withBody, new { })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CommitWithAsync(editor, withoutBody, null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var items = await ScanReaders.ItemsAsync(Db, library.Id);
        items.Count.ShouldBe(2);
        items.Single(i => i.LocationId == shelf).Status.ShouldBe(ItemStatus.Wishlist);
        items.Single(i => i.LocationId == null).Status.ShouldBe(ItemStatus.Owned);
    }

    [Fact]
    public async Task The_same_key_replays_the_same_options_and_refuses_different_ones()
    {
        var (library, editor, sessionId, _, _) = await TwoAcceptedAsync(7131);
        var shelf = await TestData.CreateLocationAsync(Db, library.Id, "Shelf");

        var first = await CommitWithAsync(editor, sessionId, new { addTo = shelf }, "key-1");
        var replay = await CommitWithAsync(editor, sessionId, new { addTo = shelf }, "key-1");
        var different = await CommitWithAsync(editor, sessionId, new { status = "sold" }, "key-1");

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadJsonAsync(replay)).GetProperty("itemIds").GetArrayLength().ShouldBe(2);
        different.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CountItemsAsync(library.Id)).ShouldBe(2);
    }

    [Fact]
    public async Task An_override_for_a_candidate_that_is_not_in_the_session_or_with_bad_copies_is_refused()
    {
        var (library, editor, sessionId, first, _) = await TwoAcceptedAsync(7141);

        var unknown = await CommitWithAsync(editor, sessionId, new { overrides = new[] { new { candidateId = Guid.NewGuid(), copies = 1 } } });
        var tooMany = await CommitWithAsync(editor, sessionId, new { overrides = new[] { new { candidateId = Id(first), copies = 999 } } });
        var twice = await CommitWithAsync(
            editor, sessionId, new { overrides = new[] { new { candidateId = Id(first), copies = 1 }, new { candidateId = Id(first), copies = 2 } } });

        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        tooMany.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        twice.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CountItemsAsync(library.Id)).ShouldBe(0);
    }

    [Fact]
    public async Task LOCKED_Commit_with_options_is_atomic_an_invalid_option_creates_no_items_and_changes_no_candidate()
    {
        var (library, editor, sessionId, first, second) = await TwoAcceptedAsync(7151);
        var other = await CreateLibraryAsync();
        var foreign = await TestData.CreateLocationAsync(Db, other.Id, "Elsewhere");
        var shelf = await TestData.CreateLocationAsync(Db, library.Id, "Shelf");

        var badRequests = new object[]
        {
            new { addTo = shelf, overrides = new[] { new { candidateId = Id(second), locationId = foreign } } }, // the first item is fine, the second is not
            new { overrides = new object[] { new { candidateId = Id(first), copies = 2 }, new { candidateId = Id(second), status = "bogus" } } },
            new { addTo = shelf, status = "bogus" },
            new { addTo = foreign },
        };
        foreach (var body in badRequests)
        {
            var failed = await CommitWithAsync(editor, sessionId, body);
            ((int)failed.StatusCode).ShouldBeInRange(400, 404, await failed.Content.ReadAsStringAsync());
            (await CountItemsAsync(library.Id)).ShouldBe(0);
            var session = await GetSessionAsync(editor, sessionId);
            session.GetProperty("status").GetString().ShouldBe("open");
            session.GetProperty("candidates").EnumerateArray().ShouldAllBe(c =>
                c.GetProperty("createdItemId").ValueKind == System.Text.Json.JsonValueKind.Null && c.GetProperty("copies").GetInt32() == 1);
        }

        // Once the options are valid the same session commits whole, and a second commit creates nothing.
        var ok = await CommitWithAsync(editor, sessionId, new { addTo = shelf });
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadJsonAsync(ok)).GetProperty("created").GetInt32().ShouldBe(2);
        var again = await CommitWithAsync(editor, sessionId, new { addTo = shelf });
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CountItemsAsync(library.Id)).ShouldBe(2);
    }

    [Fact]
    public async Task LOCKED_A_location_from_another_library_is_rejected_in_add_to_and_in_every_override()
    {
        var (library, editor, sessionId, first, second) = await TwoAcceptedAsync(7161);
        var other = await CreateLibraryAsync();
        var foreign = await TestData.CreateLocationAsync(Db, other.Id, "Elsewhere");

        var inAddTo = await CommitWithAsync(editor, sessionId, new { addTo = foreign });
        var inFirstOverride = await CommitWithAsync(editor, sessionId, new { overrides = new[] { new { candidateId = Id(first), locationId = foreign } } });
        var inLastOverride = await CommitWithAsync(
            editor,
            sessionId,
            new { overrides = new object[] { new { candidateId = Id(first), copies = 1 }, new { candidateId = Id(second), locationId = foreign } } });

        foreach (var response in new[] { inAddTo, inFirstOverride, inLastOverride })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync());
            (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("location.not_found");
        }

        (await CountItemsAsync(library.Id)).ShouldBe(0);
        (await CountItemsAsync(other.Id)).ShouldBe(0);
    }
}
