using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Scanning.Identify;

// A shelf spine the catalogue found nothing for is skipped with its own step, never auto-added, and kept on the response so the
// person can un-skip it. A cover read, a pending read and an ambiguous read are all left as they were. Neutral names throughout.
public class UnfoundSpineReadTests(ApiFixture api) : IdentifyTest(api)
{
    private string Name { get; } = $"Marlow Wickersham {Guid.NewGuid():N}";

    private static object Box(double x) => new { x, y = 0.1, width = 0.05, height = 0.8 };

    /// <summary>Four reads, each in a place of its own: a shelf needs at least four before they are scored as spines.</summary>
    private object[] ShelfOfFour() =>
    [
        .. Enumerable.Range(0, 4).Select(place =>
        {
            var read = Read(title: place == 0 ? Name : $"{Name} Volume {place}", kind: place == 0 ? null : "book");
            read["box"] = Box(0.2 * place);
            return (object)read;
        }),
    ];

    private async Task<List<JsonElement>> IdentifyBodyAsync(Dictionary<string, object?> body)
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(editor, library.Id);
        var response = await editor.PostAsync(IdentifyUrl(sessionId), Json(body));
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().ToList();
    }

    private async Task<string> TraceOfAsync(JsonElement candidate)
    {
        var id = candidate.GetProperty("id").GetGuid();
        await using var context = Db.CreateContext();
        return await context.ScanCandidates.IgnoreQueryFilters().Where(c => c.Id == id).Select(c => c.TraceJson).SingleAsync() ?? "";
    }

    private Dictionary<string, object?> ShelfBody()
    {
        var body = Body(Guid.NewGuid(), ShelfOfFour());
        body["shotKind"] = "shelf_segment";
        return body;
    }

    [Fact]
    public async Task A_spine_read_the_catalogue_found_nothing_for_is_skipped_and_kept_on_the_response()
    {
        var candidates = await IdentifyBodyAsync(ShelfBody());

        candidates.Count.ShouldBe(4, "the phone drew a line for each read and needs a card for each");
        foreach (var candidate in candidates)
        {
            Resolution(candidate).ShouldBe("not_found");
            Decision(candidate).ShouldBe("rejected");
            (await TraceOfAsync(candidate)).ShouldContain("unfound-spine");
        }
    }

    [Fact]
    public async Task A_cover_read_that_found_nothing_still_shows()
    {
        var body = Body(Guid.NewGuid(), [Read(title: Name, kind: "book")]);
        body["kind"] = "cover";
        body["shotKind"] = "single_cover";

        var candidate = (await IdentifyBodyAsync(body)).ShouldHaveSingleItem();

        Resolution(candidate).ShouldBe("not_found");
        Decision(candidate).ShouldNotBe("rejected");
        (await TraceOfAsync(candidate)).ShouldNotContain("unfound-spine");
    }

    [Fact]
    public async Task Covers_laid_out_that_found_nothing_still_show()
    {
        var body = Body(Guid.NewGuid(), ShelfOfFour());
        body["kind"] = "cover";

        var candidates = await IdentifyBodyAsync(body);

        candidates.Count.ShouldBe(4);
        candidates.ShouldAllBe(c => Decision(c) != "rejected");
    }

    [Fact]
    public async Task A_pending_spine_read_is_not_skipped()
    {
        Options.ResolveBudget = TimeSpan.FromMilliseconds(300);
        Books.Behaviour = ct => Task.Delay(Timeout.Infinite, ct);

        var candidates = await IdentifyBodyAsync(ShelfBody());

        candidates.ShouldAllBe(c => Resolution(c) == "pending");
        candidates.ShouldAllBe(c => Decision(c) != "rejected");
    }

    [Fact]
    public async Task An_ambiguous_spine_read_is_not_skipped()
    {
        foreach (var id in new[] { "u1", "u2" })
        {
            Books.Results.Add(new ProviderRelease { Provider = "openlibrary", ExternalId = id, WorkKey = id, Title = Name, Kind = WorkKind.Book, Year = 2001, Creators = ["Iris Fenwick"] });
        }

        var candidates = await IdentifyBodyAsync(ShelfBody());

        var first = candidates[0];
        Resolution(first).ShouldBe("ambiguous");
        Decision(first).ShouldNotBe("rejected");
        (await TraceOfAsync(first)).ShouldNotContain("unfound-spine");
    }
}
