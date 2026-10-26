using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Api.Tests.Scanning.Identify;

// Issue #343: the T1 tier matrix. A high-confidence spine/cover match auto-adds flagged edition-unverified,
// but only in a session that opted into T1; anything else lands pre-checked in review.
public class AutoCommitT1Tests : IdentifyTest
{
    public AutoCommitT1Tests(ApiFixture api)
        : base(api)
    {
    }

    private static string? Tier(JsonElement candidate) =>
        candidate.TryGetProperty("tier", out var tier) && tier.ValueKind == JsonValueKind.String ? tier.GetString() : null;

    private static Guid? CreatedItemId(JsonElement candidate) =>
        candidate.TryGetProperty("createdItemId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetGuid() : null;

    private static object OptIn() => new { autoCommitTiers = new[] { "T0", "T1" } };

    /// <summary>Posts one picture read with a shot kind and returns the candidates of the 200 response, in order.</summary>
    private static async Task<List<JsonElement>> IdentifyShotAsync(HttpClient client, Guid sessionId, string shotKind, params object[] items)
    {
        var body = Body(Guid.NewGuid(), items);
        body["shotKind"] = shotKind;
        var response = await client.PostAsync(IdentifyUrl(sessionId), Json(body));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().ToList();
    }

    [Fact]
    public async Task T1_opt_in_auto_adds_a_high_confidence_spine_match_flagged_edition_unverified()
    {
        var library = await CreateLibraryAsync();
        await CatalogAsync("Star Fox 64", ScanData.Upc(34301));
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, OptIn());

        var candidate = (await IdentifyShotAsync(
            editor, sessionId, "shelf_segment", Read(title: "Star Fox 64", kind: "game", format: "game_cartridge", year: 1996)))[0];

        Resolution(candidate).ShouldBe("matched");
        Tier(candidate).ShouldBe("T1");
        Decision(candidate).ShouldBe("accepted");
        CreatedItemId(candidate).ShouldNotBeNull();
        var items = await ScanReaders.ItemsAsync(Db, library.Id);
        items.Count.ShouldBe(1);
        items[0].EditionVerified.ShouldBeFalse();
        items[0].AddedVia.ShouldBe(AddedVia.Picture);
        items[0].Status.ShouldBe(ItemStatus.Owned);

        var session = await GetSessionAsync(editor, sessionId);
        session.GetProperty("counters").GetProperty("added").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Without_T1_opt_in_a_T1_eligible_match_lands_prechecked_in_review()
    {
        var library = await CreateLibraryAsync();
        await CatalogAsync("Star Fox 64", ScanData.Upc(34302));
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var candidate = (await IdentifyShotAsync(
            editor, sessionId, "shelf_segment", Read(title: "Star Fox 64", kind: "game", format: "game_cartridge", year: 1996)))[0];

        Resolution(candidate).ShouldBe("matched");
        Tier(candidate).ShouldBe("T1");
        Decision(candidate).ShouldBe("accepted");
        CreatedItemId(candidate).ShouldBeNull();
        (await ScanReaders.ItemsAsync(Db, library.Id)).ShouldBeEmpty();

        var session = await GetSessionAsync(editor, sessionId);
        session.GetProperty("candidates").GetArrayLength().ShouldBe(1);
        session.GetProperty("counters").GetProperty("toReview").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task A_match_with_two_works_in_the_running_is_T2_for_review()
    {
        var library = await CreateLibraryAsync();
        await CatalogAsync("Dune", ScanData.Upc(34303), ReleaseFormat.Paperback, WorkKind.Book, 1965);
        Books.Results.Add(new ProviderRelease { Provider = "openlibrary", ExternalId = "messiah", Title = "Dune Messiah", Kind = WorkKind.Book, Year = 1965 });
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, OptIn());

        var candidate = (await IdentifyShotAsync(
            editor, sessionId, "shelf_segment", Read(title: "Dune", kind: "book", year: 1965)))[0];

        Resolution(candidate).ShouldBe("matched");
        candidate.GetProperty("alternatives").GetArrayLength().ShouldBe(2);
        Tier(candidate).ShouldBe("T2");
        Decision(candidate).ShouldBe("accepted");
        CreatedItemId(candidate).ShouldBeNull();
        (await ScanReaders.ItemsAsync(Db, library.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Exact_owned_hit_is_T2_with_nothing_added_despite_T1_opt_in()
    {
        var library = await CreateLibraryAsync();
        await OwnAsync(library, "Star Fox 64", ScanData.Upc(34304));
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, OptIn());

        var candidate = (await IdentifyShotAsync(
            editor, sessionId, "shelf_segment", Read(title: "Star Fox 64", kind: "game", format: "game_cartridge", year: 1996)))[0];

        Resolution(candidate).ShouldBe("matched");
        Tier(candidate).ShouldBe("T2");
        Decision(candidate).ShouldBe("rejected");
        candidate.GetProperty("alreadyOwnedCount").GetInt32().ShouldBe(1);
        CreatedItemId(candidate).ShouldBeNull();
        (await ScanReaders.ItemsAsync(Db, library.Id)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Copies_mode_does_not_promote_an_owned_T1_match()
    {
        var library = await CreateLibraryAsync();
        await OwnAsync(library, "Star Fox 64", ScanData.Upc(34305));
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, new { autoCommitTiers = new[] { "T0", "T1" }, autoCommitCopies = true });

        var candidate = (await IdentifyShotAsync(
            editor, sessionId, "shelf_segment", Read(title: "Star Fox 64", kind: "game", format: "game_cartridge", year: 1996)))[0];

        Tier(candidate).ShouldBe("T2", "copies mode is a T0 condition (docs/16 §15); a T1 never adds onto an owned release");
        CreatedItemId(candidate).ShouldBeNull();
        (await ScanReaders.ItemsAsync(Db, library.Id)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Viewer_scan_resolves_T1_but_never_auto_adds()
    {
        var library = await CreateLibraryAsync();
        await CatalogAsync("Star Fox 64", ScanData.Upc(34306));
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id, OptIn());

        var candidate = (await IdentifyShotAsync(
            viewer, sessionId, "shelf_segment", Read(title: "Star Fox 64", kind: "game", format: "game_cartridge", year: 1996)))[0];

        Resolution(candidate).ShouldBe("matched");
        Tier(candidate).ShouldBe("T1");
        CreatedItemId(candidate).ShouldBeNull();
        (await ScanReaders.ItemsAsync(Db, library.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Unknown_shot_kind_counts_as_a_shelf_read()
    {
        var library = await CreateLibraryAsync();
        await CatalogAsync("Star Fox 64", ScanData.Upc(34307));
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, OptIn());

        var candidate = (await IdentifyOkAsync(
            editor, sessionId, Read(title: "Star Fox 64", kind: "game", format: "game_cartridge", year: 1996)))[0];

        candidate.GetProperty("shotKind").GetString().ShouldBe("unknown");
        Tier(candidate).ShouldBe("T1");
        CreatedItemId(candidate).ShouldNotBeNull();
        (await ScanReaders.ItemsAsync(Db, library.Id)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_background_job_auto_adds_a_T1_match()
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
        var sessionId = await StartSessionAsync(editor, library.Id, OptIn());
        Resolution((await IdentifyOkAsync(editor, sessionId, Read(title: "Zorblax Quest IV", kind: "game")))[0]).ShouldBe("pending");

        slow = false;
        await RunNextJobAsync();

        var resolved = (await GetSessionAsync(editor, sessionId)).GetProperty("candidates")[0];
        Resolution(resolved).ShouldBe("matched");
        Tier(resolved).ShouldBe("T1");
        CreatedItemId(resolved).ShouldNotBeNull();
        var items = await ScanReaders.ItemsAsync(Db, library.Id);
        items.Count.ShouldBe(1);
        items[0].EditionVerified.ShouldBeFalse();
    }

    [Fact]
    public async Task Undo_takes_back_a_T1_auto_add_like_a_T0_one()
    {
        var library = await CreateLibraryAsync();
        await CatalogAsync("Star Fox 64", ScanData.Upc(34308));
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, OptIn());
        var candidate = (await IdentifyShotAsync(
            editor, sessionId, "shelf_segment", Read(title: "Star Fox 64", kind: "game", format: "game_cartridge", year: 1996)))[0];
        CreatedItemId(candidate).ShouldNotBeNull();

        var request = new HttpRequestMessage(HttpMethod.Post, $"{SessionUrl(sessionId)}/auto-commit:undo")
        {
            Content = Json(new { candidateId = Id(candidate) }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await editor.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var undone = await ReadJsonAsync(response);
        undone.GetProperty("removed").GetInt32().ShouldBe(1);
        (await ScanReaders.ItemsAsync(Db, library.Id)).ShouldBeEmpty();
        var again = undone.GetProperty("candidate");
        again.GetProperty("createdItemId").ValueKind.ShouldBe(JsonValueKind.Null);
        again.GetProperty("decision").GetString().ShouldBe("undecided");
        again.GetProperty("resolution").GetString().ShouldBe("matched");
        again.GetProperty("tier").GetString().ShouldBe("T1");
    }

    [Fact]
    public async Task Review_commit_of_a_T1_row_creates_a_verified_item()
    {
        var library = await CreateLibraryAsync();
        await CatalogAsync("Star Fox 64", ScanData.Upc(34309));
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidate = (await IdentifyShotAsync(
            editor, sessionId, "shelf_segment", Read(title: "Star Fox 64", kind: "game", format: "game_cartridge", year: 1996)))[0];
        Tier(candidate).ShouldBe("T1");

        var commit = await CommitAsync(editor, sessionId);

        commit.StatusCode.ShouldBe(HttpStatusCode.OK, await commit.Content.ReadAsStringAsync());
        var items = await ScanReaders.ItemsAsync(Db, library.Id);
        items.Count.ShouldBe(1);
        items[0].EditionVerified.ShouldBeTrue("the person reviewed it at commit");
    }

    [Fact]
    public async Task Barcode_and_manual_adds_never_carry_the_flag()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(34310);
        var releaseId = await CatalogAsync("T0 game", code);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, OptIn());

        var scanned = (await ScanAsync(editor, sessionId, code))[0];
        scanned.GetProperty("tier").GetString().ShouldBe("T0");

        var create = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/libraries/{library.Id}/items")
        {
            Content = Json(new { releaseId }),
        };
        create.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        (await editor.SendAsync(create)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var items = await ScanReaders.ItemsAsync(Db, library.Id);
        items.Count.ShouldBe(2);
        items.ShouldAllBe(i => i.EditionVerified);
    }
}
