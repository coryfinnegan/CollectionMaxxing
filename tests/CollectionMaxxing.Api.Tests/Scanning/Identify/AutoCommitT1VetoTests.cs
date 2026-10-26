using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Api.Tests.Scanning.Identify;

// LOCKED tests for the T1 exclusions (issue #343, docs/16 §6, §12, §16): a vetoed match and a loose-item
// match land T2 and are never auto-added, however high they scored. Do not weaken, skip or delete.
public class AutoCommitT1VetoTests : IdentifyTest
{
    public AutoCommitT1VetoTests(ApiFixture api)
        : base(api)
    {
    }

    private static async Task<List<System.Text.Json.JsonElement>> IdentifyShotAsync(HttpClient client, Guid sessionId, string shotKind, params object[] items)
    {
        var body = Body(Guid.NewGuid(), items);
        body["shotKind"] = shotKind;
        var response = await client.PostAsync(IdentifyUrl(sessionId), Json(body));
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().ToList();
    }

    [Fact]
    public async Task Series_volumes_in_an_identify_match_land_T2_for_the_person_to_pick()
    {
        var library = await CreateLibraryAsync();
        await CatalogAsync("One Piece Vol. 1", ScanData.Upc(34320), ReleaseFormat.Paperback, WorkKind.Book, 2003);
        await CatalogAsync("One Piece Vol. 2", ScanData.Upc(34321), ReleaseFormat.Paperback, WorkKind.Book, 2003);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, new { autoCommitTiers = new[] { "T0", "T1" } });

        var candidate = (await IdentifyShotAsync(
            editor, sessionId, "shelf_segment", Read(title: "One Piece Vol. 1", kind: "book", year: 2003)))[0];

        candidate.GetProperty("score").GetDouble().ShouldBeGreaterThanOrEqualTo(0.90, "the top hit clears Matched, and still loses");
        Resolution(candidate).ShouldBe("ambiguous");
        candidate.GetProperty("tier").GetString().ShouldBe("T2");
        Decision(candidate).ShouldBe("undecided");
        candidate.GetProperty("createdItemId").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        (await ScanReaders.ItemsAsync(Db, library.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_provider_runner_up_in_the_same_series_is_a_veto()
    {
        var library = await CreateLibraryAsync();
        await CatalogAsync("One Piece Vol. 1", ScanData.Upc(34322), ReleaseFormat.Paperback, WorkKind.Book, 2003);
        Books.Results.Add(new ProviderRelease { Provider = "openlibrary", ExternalId = "v2", Title = "One Piece Vol. 2", Kind = WorkKind.Book, Year = 2003 });
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, new { autoCommitTiers = new[] { "T0", "T1" } });

        var candidate = (await IdentifyShotAsync(
            editor, sessionId, "shelf_segment", Read(title: "One Piece Vol. 1", kind: "book", year: 2003)))[0];

        candidate.GetProperty("score").GetDouble().ShouldBeGreaterThanOrEqualTo(0.90, "the top hit clears Matched, and still loses");
        Resolution(candidate).ShouldBe("ambiguous");
        candidate.GetProperty("tier").GetString().ShouldBe("T2");
        Decision(candidate).ShouldBe("undecided");
        candidate.GetProperty("createdItemId").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        (await ScanReaders.ItemsAsync(Db, library.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Loose_item_matches_are_always_T2_never_auto_added()
    {
        var library = await CreateLibraryAsync();
        await CatalogAsync("Star Fox 64", ScanData.Upc(34323));
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, new { autoCommitTiers = new[] { "T0", "T1" } });

        var candidate = (await IdentifyShotAsync(
            editor, sessionId, "loose_item", Read(title: "Star Fox 64", kind: "game", format: "game_cartridge", year: 1996)))[0];

        Resolution(candidate).ShouldBe("matched");
        candidate.GetProperty("shotKind").GetString().ShouldBe("loose_item");
        candidate.GetProperty("tier").GetString().ShouldBe("T2");
        candidate.GetProperty("createdItemId").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        (await ScanReaders.ItemsAsync(Db, library.Id)).ShouldBeEmpty();
    }
}
