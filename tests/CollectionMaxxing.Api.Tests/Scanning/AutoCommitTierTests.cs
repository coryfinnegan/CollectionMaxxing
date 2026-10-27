using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;

namespace CollectionMaxxing.Api.Tests.Scanning;

// Issue #341: the T0 tier matrix. A barcode-proof hit (a normalized identifier resolving to exactly one release,
// learned or provider) auto-adds with tier T0 when the session opts in; anything else lands T2 for the person.
// Since #452 nothing auto-adds by default: every capture waits for the Review commit.
public class AutoCommitTierTests : ScanTest
{
    public AutoCommitTierTests(ApiFixture api)
        : base(api)
    {
    }

    private static string? Tier(JsonElement candidate) =>
        candidate.TryGetProperty("tier", out var tier) && tier.ValueKind == JsonValueKind.String ? tier.GetString() : null;

    private static Guid? CreatedItemId(JsonElement candidate) =>
        candidate.TryGetProperty("createdItemId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetGuid() : null;

    [Fact]
    public async Task Unique_catalogue_hit_auto_adds_with_tier_T0()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(34101);
        var releaseId = await CatalogAsync("T0 game", code);
        var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithAutoCommitAsync(editor, library.Id);

        var candidate = (await ScanAsync(editor, sessionId, code))[0];

        Resolution(candidate).ShouldBe("matched");
        Tier(candidate).ShouldBe("T0");
        Decision(candidate).ShouldBe("accepted");
        CreatedItemId(candidate).ShouldNotBeNull();
        var items = await ScanReaders.ItemsAsync(Db, library.Id);
        items.Count.ShouldBe(1);
        items[0].ReleaseId.ShouldBe(releaseId);
        items[0].Status.ShouldBe(CollectionMaxxing.Domain.Items.ItemStatus.Owned);
        items[0].AddedVia.ShouldBe(CollectionMaxxing.Domain.Items.AddedVia.Barcode);
    }

    [Fact]
    public async Task Unique_provider_hit_auto_adds_with_tier_T0()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(34102);
        Products.Know(code, ScanData.Product("Wave Race 64", code));
        Games.Results.Add(ScanData.Game("Wave Race 64"));
        var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithAutoCommitAsync(editor, library.Id);

        var candidate = (await ScanAsync(editor, sessionId, code))[0];

        Resolution(candidate).ShouldBe("matched");
        Tier(candidate).ShouldBe("T0");
        CreatedItemId(candidate).ShouldNotBeNull();
        (await ScanReaders.ItemsAsync(Db, library.Id)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Reused_barcode_is_T2_for_the_person_to_pick()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(34103);
        await CatalogAsync("First release", code);
        await CatalogAsync("Second release", code);
        var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var candidate = (await ScanAsync(editor, sessionId, code))[0];

        Resolution(candidate).ShouldBe("ambiguous");
        Tier(candidate).ShouldBe("T2");
        Decision(candidate).ShouldBe("undecided");
        CreatedItemId(candidate).ShouldBeNull();
        (await ScanReaders.ItemsAsync(Db, library.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Exact_owned_hit_is_T2_and_answered_on_its_card_with_nothing_added()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(34104);
        await OwnAsync(library, "Owned game", code);
        var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var candidate = (await ScanAsync(editor, sessionId, code))[0];

        Resolution(candidate).ShouldBe("matched");
        Tier(candidate).ShouldBe("T2");
        Decision(candidate).ShouldBe("rejected");
        candidate.GetProperty("alreadyOwnedCount").GetInt32().ShouldBe(1);
        CreatedItemId(candidate).ShouldBeNull();
        (await ScanReaders.ItemsAsync(Db, library.Id)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Copies_mode_auto_adds_an_owned_release_as_another_copy()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(34105);
        var (releaseId, _) = await OwnAsync(library, "Owned game", code);
        var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, new { autoCommitTiers = new[] { "T0" }, autoCommitCopies = true });

        var candidate = (await ScanAsync(editor, sessionId, code))[0];

        Tier(candidate).ShouldBe("T0");
        Decision(candidate).ShouldBe("accepted");
        CreatedItemId(candidate).ShouldNotBeNull();
        var items = await ScanReaders.ItemsAsync(Db, library.Id);
        items.Count.ShouldBe(2);
        items.ShouldAllBe(i => i.ReleaseId == releaseId);
    }

    [Fact]
    public async Task Unique_hit_waits_in_review_with_nothing_added_by_default()
    {
        // Issue #452: nothing enters the library straight from a scan. A barcode-proof hit resolves at scan time
        // (the match preselected) and waits for the Review commit, which is what creates the item.
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(45201);
        var releaseId = await CatalogAsync("Review game", code);
        var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var candidate = (await ScanAsync(editor, sessionId, code))[0];

        Resolution(candidate).ShouldBe("matched");
        Tier(candidate).ShouldBe("T0");
        Decision(candidate).ShouldBe("accepted");
        CreatedItemId(candidate).ShouldBeNull();
        candidate.GetProperty("release").GetProperty("id").GetGuid().ShouldBe(releaseId);
        (await CountItemsAsync(library.Id)).ShouldBe(0);
        var counters = (await GetSessionAsync(editor, sessionId)).GetProperty("counters");
        counters.GetProperty("added").GetInt32().ShouldBe(0);
        counters.GetProperty("toReview").GetInt32().ShouldBe(1);

        var commit = await CommitAsync(editor, sessionId);
        commit.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadJsonAsync(commit)).GetProperty("created").GetInt32().ShouldBe(1);
        var items = await ScanReaders.ItemsAsync(Db, library.Id);
        items.Count.ShouldBe(1);
        items[0].ReleaseId.ShouldBe(releaseId);
    }

    [Fact]
    public async Task Empty_tiers_leave_a_unique_hit_for_review_with_nothing_added()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(34106);
        await CatalogAsync("T0 game", code);
        var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, new { autoCommitTiers = Array.Empty<string>() });

        var candidate = (await ScanAsync(editor, sessionId, code))[0];

        Resolution(candidate).ShouldBe("matched");
        Tier(candidate).ShouldBe("T0");
        Decision(candidate).ShouldBe("accepted");
        CreatedItemId(candidate).ShouldBeNull();
        (await ScanReaders.ItemsAsync(Db, library.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Viewer_scan_resolves_but_never_auto_adds()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(34107);
        await CatalogAsync("T0 game", code);
        var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        var candidate = (await ScanAsync(viewer, sessionId, code))[0];

        Resolution(candidate).ShouldBe("matched");
        Tier(candidate).ShouldBe("T0");
        CreatedItemId(candidate).ShouldBeNull();
        (await ScanReaders.ItemsAsync(Db, library.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Unknown_code_is_T2_with_nothing_added()
    {
        var library = await CreateLibraryAsync();
        var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var candidate = (await ScanAsync(editor, sessionId, ScanData.Upc(34108)))[0];

        Resolution(candidate).ShouldBe("not_found");
        Tier(candidate).ShouldBe("T2");
        CreatedItemId(candidate).ShouldBeNull();
        (await ScanReaders.ItemsAsync(Db, library.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Multi_option_matched_without_a_series_clash_stays_matched_for_review()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(34109);
        Products.Know(code, ScanData.Product("Wave Race 64", code));
        Games.Results.Add(ScanData.Game("Wave Race 64", externalId: "g1"));
        Games.Results.Add(ScanData.Game("Excitebike 64", externalId: "g2"));
        var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var candidate = (await ScanAsync(editor, sessionId, code))[0];

        Resolution(candidate).ShouldBe("matched");
        Tier(candidate).ShouldBe("T2");
        Decision(candidate).ShouldBe("accepted");
        CreatedItemId(candidate).ShouldBeNull();
        (await ScanReaders.ItemsAsync(Db, library.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Session_defaults_to_no_auto_add_without_copies()
    {
        var library = await CreateLibraryAsync();
        var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var session = await GetSessionAsync(editor, sessionId);

        session.GetProperty("autoCommitTiers").EnumerateArray().ShouldBeEmpty();
        session.GetProperty("autoCommitCopies").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Session_rejects_T2_in_auto_commit_tiers()
    {
        var library = await CreateLibraryAsync();
        var editor = As(library, LibraryRole.Editor);

        var response = await editor.PostAsync(SessionsUrl(library.Id), Json(new { autoCommitTiers = new[] { "T2" } }));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Decide_on_an_auto_added_candidate_is_409_until_its_add_is_undone()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(34110);
        await CatalogAsync("T0 game", code);
        var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithAutoCommitAsync(editor, library.Id);
        var candidate = (await ScanAsync(editor, sessionId, code))[0];
        CreatedItemId(candidate).ShouldNotBeNull();

        var response = await DecideAsync(editor, sessionId, Id(candidate), new { decision = "rejected" });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("scan.auto_committed");
    }

    [Fact]
    public async Task Bound_rescan_of_an_auto_added_row_is_409_until_its_add_is_undone()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(34111);
        await CatalogAsync("T0 game", code);
        var other = ScanData.Upc(34112);
        await CatalogAsync("Other game", other);
        var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithAutoCommitAsync(editor, library.Id);
        var candidate = (await ScanAsync(editor, sessionId, code))[0];
        CreatedItemId(candidate).ShouldNotBeNull();

        var request = new HttpRequestMessage(HttpMethod.Post, $"{SessionUrl(sessionId)}/barcodes")
        {
            Content = Json(new { codes = new[] { new { value = other, symbology = "ean_13" } }, candidateId = Id(candidate) }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await editor.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("scan.auto_committed");
    }
}
