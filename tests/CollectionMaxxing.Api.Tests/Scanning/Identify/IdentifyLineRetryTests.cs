using CollectionMaxxing.Api.Tests.Catalog;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Api.Tests.Scanning.Identify;

// A shelf spine whose chosen line finds nothing is asked again with another line of its spine, when the phone sends the spine's
// lines; what that finds is offered (never matched) only when the book's author is printed on the same spine. An older phone sends
// none and its spine is skipped as it always was. Names and titles are neutral.
public class IdentifyLineRetryTests(ApiFixture api) : IdentifyTest(api)
{
    private const string Garbled = "XQZVKLMNB WRTPLHJ";

    private static Dictionary<string, object?> Book(string title) => Read(title: title, kind: "book");

    private static Dictionary<string, object?> Spine(string[]? lines)
    {
        var read = Book(Garbled);
        if (lines is not null)
        {
            read["lines"] = lines;
        }

        return read;
    }

    /// <summary>A shelf: four reads make one, the first three of two words (never retried), and the one given.</summary>
    private static object[] Shelf(Dictionary<string, object?> last) =>
        [Book("Alpha Quest"), Book("Beta Quest"), Book("Gamma Quest"), last];

    private async Task<(HttpClient Client, Guid SessionId)> StartAsync()
    {
        var library = await CreateLibraryAsync();
        await CatalogSeed.AddAsync(Db, "Iron Fox", WorkKind.Book, 1999, format: ReleaseFormat.Paperback, creators: ["Mara Quillon"]);
        var client = As(library, LibraryRole.Editor);
        return (client, await StartSessionWithAutoCommitAsync(client, library.Id));
    }

    [Fact]
    public async Task An_identify_item_may_carry_its_spines_lines_and_a_title_line_whose_author_is_printed_is_offered()
    {
        var (client, sessionId) = await StartAsync();

        var spine = (await IdentifyOkAsync(client, sessionId, Shelf(Spine([Garbled, "IRON FOX", "MARA QUILLON"]))))[3];

        Resolution(spine).ShouldBe("ambiguous");
        Decision(spine).ShouldBe("undecided"); // offered, never added by itself
        AlternativeTitles(spine).ShouldBe(["Iron Fox"]);
    }

    [Fact]
    public async Task A_title_line_whose_author_is_not_printed_on_the_spine_is_not_offered()
    {
        var (client, sessionId) = await StartAsync();

        var spine = (await IdentifyOkAsync(client, sessionId, Shelf(Spine([Garbled, "IRON FOX", "SOMEONE ELSEWHERE"]))))[3];

        Resolution(spine).ShouldNotBe("ambiguous");
        AlternativeTitles(spine).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_spine_from_a_phone_that_sends_no_lines_finds_nothing_as_before()
    {
        var (client, sessionId) = await StartAsync();

        var spine = (await IdentifyOkAsync(client, sessionId, Shelf(Spine(null))))[3];

        Resolution(spine).ShouldNotBe("ambiguous");
        AlternativeTitles(spine).ShouldBeEmpty();
    }
}
