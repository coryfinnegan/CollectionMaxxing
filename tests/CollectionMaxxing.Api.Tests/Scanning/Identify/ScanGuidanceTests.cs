using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Catalog;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Api.Tests.Scanning.Identify;

public class ScanGuidanceTests(ApiFixture api) : IdentifyTest(api)
{
    private static string? Guidance(JsonElement candidate) =>
        candidate.GetProperty("guidanceCode").ValueKind == JsonValueKind.Null
            ? null
            : candidate.GetProperty("guidanceCode").GetString();

    [Fact]
    public async Task A_reshoot_signal_is_recorded_on_the_row_and_echoed_back()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var body = Body(Guid.NewGuid(), [Read(title: "A Book That Is Nowhere In The Catalogue")]);
        body["fellBackFrom"] = "move_closer";

        var response = await editor.PostAsync(IdentifyUrl(sessionId), Json(body));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var candidate = (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().Single();

        Resolution(candidate).ShouldBe("not_found");
        Guidance(candidate).ShouldBe("move_closer");
    }

    [Fact]
    public async Task An_ambiguous_row_says_pick_one()
    {
        var library = await CreateLibraryAsync();
        await CatalogSeed.AddAsync(Db, "Dune Messiah", WorkKind.Book, 1969);
        await CatalogSeed.AddAsync(Db, "Dune Messiah", WorkKind.Book, 1969);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var candidate = (await IdentifyOkAsync(editor, sessionId, Read(title: "Dune Messiah", kind: "book")))[0];

        Resolution(candidate).ShouldBe("ambiguous");
        Guidance(candidate).ShouldBe("pick_match");
    }

    [Fact]
    public async Task A_matched_row_carries_no_guidance_even_when_the_phone_signalled_a_reshoot()
    {
        var library = await CreateLibraryAsync();
        var (_, releaseId) = await CatalogSeed.AddAsync(Db, "Dune Messiah", WorkKind.Book, 1969);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var body = Body(Guid.NewGuid(), [Read(title: "Dune Messiah", kind: "book", localMatchReleaseId: releaseId)]);
        body["fellBackFrom"] = "move_closer";

        var response = await editor.PostAsync(IdentifyUrl(sessionId), Json(body));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var candidate = (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().Single();

        Resolution(candidate).ShouldBe("matched");
        Guidance(candidate).ShouldBeNull();
    }

    [Fact]
    public async Task A_row_without_a_signal_says_shoot_the_cover()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var candidate = (await IdentifyOkAsync(editor, sessionId, Read(title: "A Book That Is Nowhere In The Catalogue")))[0];

        Resolution(candidate).ShouldBe("not_found");
        Guidance(candidate).ShouldBe("shoot_cover");
    }

    [Fact]
    public async Task An_unreadable_barcode_says_scan_it_again_but_an_unknown_code_says_shoot_the_cover()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var code = ScanData.Upc(34401);

        var unreadable = (await ScanAsync(editor, sessionId, "not-a-code"))[0];
        Resolution(unreadable).ShouldBe("error");
        Guidance(unreadable).ShouldBe("scan_barcode");

        var unknown = (await ScanAsync(editor, sessionId, code))[0];
        Resolution(unknown).ShouldBe("not_found");
        Guidance(unknown).ShouldBe("shoot_cover");
    }
}
