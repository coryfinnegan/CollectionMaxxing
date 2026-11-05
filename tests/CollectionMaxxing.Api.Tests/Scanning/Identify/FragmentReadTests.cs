using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Scanning.Identify;

// A spine read that only found itself: an author's name whose answer is a release titled with that same name. The row stays on
// the response (the phone drew a line for the read) but is skipped, so it can be neither auto-added nor committed. A title that
// merely matches a release of the same title, by another author, is a book and is kept. Neutral names throughout.
public class FragmentReadTests(ApiFixture api) : IdentifyTest(api)
{
    /// <summary>A name no other test can have left in the shared catalogue.</summary>
    private string Name { get; } = $"Marlow Wickersham {Guid.NewGuid():N}";

    private void ArrangeTwoReleasesTitledWithTheName(string creator)
    {
        Books.Results.Add(new ProviderRelease { Provider = "openlibrary", ExternalId = "f1", WorkKey = "f1", Title = Name, Kind = WorkKind.Book, Year = 2001, Creators = [creator] });
        Books.Results.Add(new ProviderRelease { Provider = "openlibrary", ExternalId = "f2", WorkKey = "f2", Title = Name, Kind = WorkKind.Book, Year = 2002, Creators = [creator] });
    }

    private static object Box(double x) => new { x, y = 0.1, width = 0.05, height = 0.8 };

    /// <summary>A book read in a place of its own: a shelf needs at least four reads before they are scored as spines.</summary>
    private static Dictionary<string, object?> FillerAt(int place)
    {
        var read = Read(title: $"Orchard Gate Volume {place}", kind: "book");
        read["box"] = Box(0.2 * place);
        return read;
    }

    private async Task<JsonElement> IdentifyOneSpineAsync()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(editor, library.Id);
        var spine = Read(title: Name);
        spine["box"] = Box(0.0);
        var body = Body(Guid.NewGuid(), [spine, .. Enumerable.Range(1, 3).Select(i => FillerAt(i))]);
        body["shotKind"] = "shelf_segment";
        var response = await editor.PostAsync(IdentifyUrl(sessionId), Json(body));
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().First();
    }

    private async Task<string> TraceOfAsync(JsonElement candidate)
    {
        var id = candidate.GetProperty("id").GetGuid();
        await using var context = Db.CreateContext();
        return await context.ScanCandidates.IgnoreQueryFilters().Where(c => c.Id == id).Select(c => c.TraceJson).SingleAsync() ?? "";
    }

    [Fact]
    public async Task An_author_name_read_answered_by_a_release_titled_with_that_name_is_skipped_and_kept_on_the_response()
    {
        ArrangeTwoReleasesTitledWithTheName(Name);

        var candidate = await IdentifyOneSpineAsync();

        Resolution(candidate).ShouldBe("ambiguous");
        Decision(candidate).ShouldBe("rejected");
        candidate.GetProperty("tier").GetString().ShouldBe("T2");
        (await TraceOfAsync(candidate)).ShouldContain("fragment");
    }

    [Fact]
    public async Task A_read_answered_by_releases_of_that_title_by_another_author_is_not_skipped()
    {
        ArrangeTwoReleasesTitledWithTheName("Iris Fenwick");

        var candidate = await IdentifyOneSpineAsync();

        Resolution(candidate).ShouldBe("ambiguous");
        Decision(candidate).ShouldNotBe("rejected");
    }
}
