using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Api.Tests.Scanning.Identify;

// A row the server skipped says why (skipReason), so Review can show it and offer to put it back. A row the person skipped, or one
// they put back, says nothing. Neutral names throughout.
public class SkipReasonTests(ApiFixture api) : IdentifyTest(api)
{
    private const string Clean = "Quiet Harbour Almanac";
    private const string Junk = "Quiet Harbour ALmanac ZXQWV";
    private string Name { get; } = $"Marlow Wickersham {Guid.NewGuid():N}";

    private static string? SkipReason(JsonElement candidate) =>
        candidate.TryGetProperty("skipReason", out var reason) && reason.ValueKind == JsonValueKind.String ? reason.GetString() : null;

    private static Dictionary<string, object?> Book(string title) => Read(title: title, kind: "book", creator: "Mara Odell");

    private static async Task<List<JsonElement>> IdentifyShelfAsync(HttpClient client, Guid sessionId, params object[] reads)
    {
        var body = Body(Guid.NewGuid(), reads);
        body["shotKind"] = "shelf_segment";
        var response = await client.PostAsync(IdentifyUrl(sessionId), Json(body));
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().ToList();
    }

    private object[] ShelfOfFour() =>
    [
        .. Enumerable.Range(0, 4).Select(place =>
        {
            var read = Read(title: place == 0 ? Name : $"{Name} Volume {place}", kind: place == 0 ? null : "book");
            read["box"] = new { x = 0.2 * place, y = 0.1, width = 0.05, height = 0.8 };
            return (object)read;
        }),
    ];

    [Fact]
    public async Task A_server_skipped_unfound_spine_carries_the_unfound_spine_reason()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(editor, library.Id);

        var candidates = await IdentifyShelfAsync(editor, sessionId, ShelfOfFour());

        candidates.ShouldAllBe(c => Decision(c) == "rejected" && SkipReason(c) == "unfound_spine");
        (await GetSessionAsync(editor, sessionId)).GetProperty("candidates").EnumerateArray()
            .ShouldAllBe(c => SkipReason(c) == "unfound_spine");
    }

    [Fact]
    public async Task A_server_skipped_repeat_read_carries_the_repeat_reason_and_the_kept_read_carries_none()
    {
        var library = await CreateLibraryAsync();
        Books.Results.Add(new ProviderRelease
        {
            Provider = "openlibrary",
            ExternalId = "OL1W",
            WorkKey = "OL1W",
            Title = Clean,
            Kind = WorkKind.Book,
            Year = 2015,
            Creators = ["Mara Odell"],
        });
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, new { autoCommitTiers = new[] { "T0", "T1" } });

        var candidates = await IdentifyShelfAsync(editor, sessionId, Book(Junk), Book(Clean));

        SkipReason(candidates[0]).ShouldBe("repeat");
        SkipReason(candidates[1]).ShouldBeNull();
    }

    [Fact]
    public async Task A_row_the_person_skipped_has_no_reason()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(editor, library.Id);
        var body = Body(Guid.NewGuid(), [Read(title: Name, kind: "book")]);
        body["kind"] = "cover";
        body["shotKind"] = "single_cover";
        var response = await editor.PostAsync(IdentifyUrl(sessionId), Json(body));
        var candidate = (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().Single();

        var skipped = await ReadJsonAsync(await DecideAsync(editor, sessionId, Id(candidate), new { decision = "rejected" }));

        Decision(skipped).ShouldBe("rejected");
        SkipReason(skipped).ShouldBeNull();
    }

    [Fact]
    public async Task A_server_skipped_row_put_back_and_skipped_again_by_the_person_has_no_reason()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(editor, library.Id);
        var first = (await IdentifyShelfAsync(editor, sessionId, ShelfOfFour()))[0];
        SkipReason(first).ShouldBe("unfound_spine");
        (await DecideAsync(editor, sessionId, Id(first), new { decision = "undecided" })).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);

        var again = await ReadJsonAsync(await DecideAsync(editor, sessionId, Id(first), new { decision = "rejected" }));

        Decision(again).ShouldBe("rejected");
        SkipReason(again).ShouldBeNull();
    }

    [Fact]
    public async Task A_release_the_library_already_owns_is_rejected_by_the_server_with_the_owned_reason()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(9410);
        await OwnAsync(library, "Harbour Almanac", code);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var candidate = (await ScanAsync(editor, sessionId, code)).Single();

        Decision(candidate).ShouldBe("rejected");
        SkipReason(candidate).ShouldBe("owned");
    }

    [Fact]
    public async Task A_release_on_the_wishlist_is_rejected_by_the_server_with_the_wishlist_reason()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(9411);
        await OwnAsync(library, "Harbour Almanac", code, status: CollectionMaxxing.Domain.Items.ItemStatus.Wishlist);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var candidate = (await ScanAsync(editor, sessionId, code)).Single();

        Decision(candidate).ShouldBe("rejected");
        SkipReason(candidate).ShouldBe("wishlist");
    }

    [Fact]
    public async Task An_owned_row_the_person_skips_after_taking_it_back_has_no_reason()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(9412);
        await OwnAsync(library, "Harbour Almanac", code);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidate = (await ScanAsync(editor, sessionId, code)).Single();

        var skipped = await ReadJsonAsync(await DecideAsync(editor, sessionId, Id(candidate), new { decision = "rejected" }));

        SkipReason(skipped).ShouldBeNull();
    }

    [Fact]
    public async Task A_skipped_row_put_back_has_no_reason_and_is_reviewable_in_the_session()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(editor, library.Id);
        var candidates = await IdentifyShelfAsync(editor, sessionId, ShelfOfFour());
        var first = candidates[0];
        SkipReason(first).ShouldBe("unfound_spine");

        var response = await DecideAsync(editor, sessionId, Id(first), new { decision = "undecided" });

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var back = await ReadJsonAsync(response);
        Decision(back).ShouldBe("undecided");
        SkipReason(back).ShouldBeNull();
        var inSession = (await GetSessionAsync(editor, sessionId)).GetProperty("candidates").EnumerateArray()
            .Single(c => Id(c) == Id(first));
        Decision(inSession).ShouldBe("undecided");
        SkipReason(inSession).ShouldBeNull();
    }
}
