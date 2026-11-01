using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Scanning.Identify;

// One item per place (issue #614). On a shelf shot the Vision pipeline reads every spine it can split and the picture model
// reads the ones it can see, so both often read one spine and name different books: Vision read the author's name or a sticker
// there, or the model read a title that is not on the shelf. One place holds one thing, so the read that grounds better is the
// row and the other is skipped. The reads only the model made carry `unconfirmed`, which is how the server tells the two
// engines apart: two reads one engine made of a place are a repeat, not a contest. A read only the model made is never added
// by itself, so the row that stands for it waits undecided for the person. Neutral titles throughout.
public class ContestedPlaceTests(ApiFixture api) : IdentifyTest(api)
{
    private const string OnTheShelf = "Quiet Harbor Atlas";
    private const string NotOnTheShelf = "Lantern Road Cookbook";
    private const string NoBook = "qxrtvz zzzblx";

    private static object Box(double x) => new { x, y = 0.1, width = 0.05, height = 0.8 };

    private static object WidePiece => new { x = 0.10, y = 0.1, width = 0.50, height = 0.8 };

    private static Dictionary<string, object?> ReadAt(string title, object box, bool onlyTheModel = false)
    {
        var read = Read(title: title, kind: "book", creator: "Mara Odell");
        read["box"] = box;
        if (onlyTheModel)
        {
            read["unconfirmed"] = true;
        }

        return read;
    }

    private static ProviderRelease Provided(string title, string externalId) => new()
    {
        Provider = "openlibrary",
        ExternalId = externalId,
        WorkKey = externalId,
        Title = title,
        Kind = WorkKind.Book,
        Year = 2015,
        Creators = ["Mara Odell"],
    };

    private async Task<(HttpClient Client, Guid SessionId)> StartAsync(params ProviderRelease[] releases)
    {
        var library = await CreateLibraryAsync();
        foreach (var release in releases)
        {
            Books.Results.Add(release);
        }

        var client = As(library, LibraryRole.Editor);
        return (client, await StartSessionWithoutAutoCommitAsync(client, library.Id));
    }

    private static async Task<List<JsonElement>> IdentifyOnePictureAsync(HttpClient client, Guid sessionId, params object[] reads)
    {
        var body = Body(Guid.NewGuid(), reads);
        body["shotKind"] = "shelf_segment";
        var response = await client.PostAsync(IdentifyUrl(sessionId), Json(body));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().ToList();
    }

    /// <summary>The steps the server stored for a read: they name "repeat" or "place" when the server skipped it.</summary>
    private async Task<string> TraceOfAsync(JsonElement candidate)
    {
        var id = candidate.GetProperty("id").GetGuid();
        await using var context = Db.CreateContext();
        return await context.ScanCandidates.IgnoreQueryFilters().Where(c => c.Id == id).Select(c => c.TraceJson).SingleAsync() ?? "";
    }

    [Fact]
    public async Task The_models_read_of_a_place_stands_where_the_Vision_read_there_found_nothing()
    {
        var (client, sessionId) = await StartAsync(Provided(OnTheShelf, "OL1W"));

        var candidates = await IdentifyOnePictureAsync(
            client, sessionId, ReadAt(NoBook, Box(0.30)), ReadAt(OnTheShelf, Box(0.31), onlyTheModel: true));

        Resolution(candidates[1]).ShouldBe("matched");
        Decision(candidates[1]).ShouldBe("undecided", "a read only the model made waits for the person, but it is the row for the spine");
        Decision(candidates[0]).ShouldBe("rejected", "Vision read nothing there, so it is no second row for the spine");
    }

    [Fact]
    public async Task The_Vision_read_of_a_place_stands_where_the_models_read_there_found_nothing()
    {
        var (client, sessionId) = await StartAsync(Provided(OnTheShelf, "OL1W"));

        var candidates = await IdentifyOnePictureAsync(
            client, sessionId, ReadAt(OnTheShelf, Box(0.30)), ReadAt(NoBook, Box(0.31), onlyTheModel: true));

        Decision(candidates[0]).ShouldBe("accepted");
        Decision(candidates[1]).ShouldBe("rejected");
    }

    [Fact]
    public async Task Reads_of_two_books_in_two_places_both_stand()
    {
        var (client, sessionId) = await StartAsync(Provided(OnTheShelf, "OL1W"), Provided(NotOnTheShelf, "OL2W"));

        var candidates = await IdentifyOnePictureAsync(
            client, sessionId, ReadAt(OnTheShelf, Box(0.10)), ReadAt(NotOnTheShelf, Box(0.60), onlyTheModel: true));

        candidates.ShouldAllBe(c => Resolution(c) == "matched");
        candidates.Select(Decision).ShouldBe(["accepted", "undecided"]);
    }

    [Fact]
    public async Task Two_reads_one_engine_made_of_a_place_both_stand_because_that_is_a_repeat_not_a_contest()
    {
        var (client, sessionId) = await StartAsync(Provided(OnTheShelf, "OL1W"));

        var candidates = await IdentifyOnePictureAsync(
            client, sessionId, ReadAt(OnTheShelf, Box(0.30)), ReadAt(NoBook, Box(0.31)));

        candidates.Select(Decision).ShouldBe(["accepted", "undecided"], "neither is skipped: one engine reading a place twice is not two engines disagreeing");
    }

    [Fact]
    public async Task A_Vision_piece_covering_several_spines_loses_its_place_and_every_model_read_inside_it_stands()
    {
        var (client, sessionId) = await StartAsync(Provided(OnTheShelf, "OL1W"), Provided(NotOnTheShelf, "OL2W"));

        var candidates = await IdentifyOnePictureAsync(
            client,
            sessionId,
            ReadAt(NoBook, WidePiece),
            ReadAt(OnTheShelf, Box(0.12), onlyTheModel: true),
            ReadAt(NotOnTheShelf, Box(0.50), onlyTheModel: true));

        candidates.Select(Decision).ShouldBe(["rejected", "undecided", "undecided"]);
        candidates.Skip(1).ShouldAllBe(c => Resolution(c) == "matched");
    }

    [Fact]
    public async Task A_Vision_piece_covering_several_spines_loses_its_place_even_when_it_is_the_read_that_matched()
    {
        var (client, sessionId) = await StartAsync(Provided(OnTheShelf, "OL1W"), Provided(NotOnTheShelf, "OL2W"));

        var candidates = await IdentifyOnePictureAsync(
            client,
            sessionId,
            ReadAt(OnTheShelf, WidePiece),
            ReadAt(NotOnTheShelf, Box(0.12), onlyTheModel: true),
            ReadAt(NoBook, Box(0.50), onlyTheModel: true));

        Decision(candidates[0]).ShouldBe("rejected", "one piece over several spines is no place of its own");
        candidates.Skip(1).ShouldAllBe(c => Decision(c) == "undecided");
    }

    [Fact]
    public async Task A_read_that_lost_its_place_says_so_in_its_trace_and_the_row_stays_for_the_person()
    {
        var (client, sessionId) = await StartAsync(Provided(OnTheShelf, "OL1W"));

        var candidates = await IdentifyOnePictureAsync(
            client, sessionId, ReadAt(NoBook, Box(0.30)), ReadAt(OnTheShelf, Box(0.31), onlyTheModel: true));

        var trace = await TraceOfAsync(candidates[0]);
        trace.ShouldContain("place");
        trace.ShouldNotContain("repeat");
        candidates[0].GetProperty("tier").GetString().ShouldBe("T2", "the row stays on the card, so the person can still un-skip it");
    }

    [Fact]
    public async Task A_second_read_of_the_same_book_in_one_place_is_still_a_repeat_not_a_lost_place()
    {
        var (client, sessionId) = await StartAsync(Provided(OnTheShelf, "OL1W"));

        var candidates = await IdentifyOnePictureAsync(
            client, sessionId, ReadAt(OnTheShelf, Box(0.30)), ReadAt(OnTheShelf, Box(0.31), onlyTheModel: true));

        Decision(candidates[1]).ShouldBe("rejected");
        (await TraceOfAsync(candidates[1])).ShouldContain("repeat");
    }
}
