using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Api.Tests.Scanning.Identify;

// A real shelf has several tapes whose spines read the same title. The phone sends each read with the box it sits in (x, y,
// width and height, 0 to 1, origin top left), so the server can tell the same spine read twice (one place) from two spines
// that read alike (two places). A box is only a hint, so a box that cannot be used is read as no box and never refuses the scan.
// Neutral titles throughout.
public class ReadPositionTests(ApiFixture api) : IdentifyTest(api)
{
    private const string Title = "Quiet Harbor Atlas";

    private static object Box(double x, double y = 0.1, double width = 0.05, double height = 0.8) => new { x, y, width, height };

    private static Dictionary<string, object?> BookAt(object? box, string title = Title)
    {
        var read = Read(title: title, kind: "book", creator: "Mara Odell");
        read["box"] = box;
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

    private async Task<(HttpClient Client, Guid SessionId, Guid LibraryId)> StartAsync()
    {
        var library = await CreateLibraryAsync();
        Books.Results.Add(Provided(Title, "OL1W"));
        var client = As(library, LibraryRole.Editor);
        return (client, await StartSessionWithoutAutoCommitAsync(client, library.Id), library.Id);
    }

    private static async Task<List<JsonElement>> IdentifyOnePictureAsync(HttpClient client, Guid sessionId, params object[] reads)
    {
        var body = Body(Guid.NewGuid(), reads);
        body["shotKind"] = "shelf_segment";
        var response = await client.PostAsync(IdentifyUrl(sessionId), Json(body));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().ToList();
    }

    // ---- position decides what a repeat is --------------------------------------------------------------------------------

    [Fact]
    public async Task Two_reads_of_one_title_at_different_places_in_one_picture_are_two_candidates_to_add()
    {
        var (client, sessionId, _) = await StartAsync();

        var candidates = await IdentifyOnePictureAsync(client, sessionId, BookAt(Box(0.10)), BookAt(Box(0.60)));

        candidates.Count.ShouldBe(2);
        candidates.ShouldAllBe(c => Resolution(c) == "matched");
        candidates.ShouldAllBe(c => Decision(c) == "accepted", "each spine is its own item, so neither is skipped");
    }

    [Fact]
    public async Task Two_reads_of_one_title_at_the_same_place_are_one_candidate_and_a_skipped_repeat()
    {
        var (client, sessionId, _) = await StartAsync();

        var candidates = await IdentifyOnePictureAsync(client, sessionId, BookAt(Box(0.30)), BookAt(Box(0.31)));

        candidates.Count.ShouldBe(2, "the phone drew a line for each read and needs a card for each");
        Decision(candidates[0]).ShouldBe("accepted");
        Decision(candidates[1]).ShouldBe("rejected");
    }

    [Fact]
    public async Task A_read_with_no_box_is_a_repeat_of_a_read_of_the_same_title_wherever_that_one_sits()
    {
        var (client, sessionId, _) = await StartAsync();

        var candidates = await IdentifyOnePictureAsync(client, sessionId, BookAt(Box(0.10)), BookAt(null));

        Decision(candidates[0]).ShouldBe("accepted");
        Decision(candidates[1]).ShouldBe("rejected");
    }

    [Fact]
    public async Task Three_spines_that_read_alike_with_a_twice_read_one_leave_two_items()
    {
        var (client, sessionId, _) = await StartAsync();

        var candidates = await IdentifyOnePictureAsync(
            client, sessionId, BookAt(Box(0.10)), BookAt(Box(0.11)), BookAt(Box(0.70)));

        candidates.Select(Decision).ShouldBe(["accepted", "rejected", "accepted"]);
    }

    [Fact]
    public async Task Committing_two_spines_of_one_title_adds_two_items()
    {
        var (client, sessionId, libraryId) = await StartAsync();
        var candidates = await IdentifyOnePictureAsync(client, sessionId, BookAt(Box(0.10)), BookAt(Box(0.60)));
        candidates.ShouldAllBe(c => Decision(c) == "accepted");

        var commit = await CommitAsync(client, sessionId);

        commit.StatusCode.ShouldBe(HttpStatusCode.OK, await commit.Content.ReadAsStringAsync());
        (await ScanReaders.ItemsAsync(Db, libraryId)).Count.ShouldBe(2);
    }

    // ---- a box is a hint: it is tidied, never refused --------------------------------------------------------------------------

    [Fact]
    public async Task A_well_formed_box_is_accepted_and_so_is_a_read_with_none()
    {
        var (client, sessionId, _) = await StartAsync();

        (await IdentifyAsync(client, sessionId, null, BookAt(Box(0.10)))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await IdentifyAsync(client, sessionId, null, BookAt(null))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await IdentifyAsync(client, sessionId, null, BookAt(Box(0.95, 0.0, 0.05, 1.0)))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(0.95, 0.1, 0.0505, 0.8)] // a hair past the right edge
    [InlineData(-0.005, 0.1, 0.05, 0.8)] // a hair before the left edge
    [InlineData(0.1, 0.95, 0.05, 0.0508)] // a hair past the bottom edge
    public async Task A_box_a_hair_outside_the_picture_is_moved_into_it_and_still_counts_as_a_place(double x, double y, double width, double height)
    {
        var (client, sessionId, _) = await StartAsync();

        // Kept as a box (not dropped as "no box"), it is a place of its own, apart from a read of the same title somewhere else.
        var candidates = await IdentifyOnePictureAsync(client, sessionId, BookAt(new { x, y, width, height }), BookAt(Box(0.50, 0.1, 0.05, 0.5)));

        candidates.Select(Decision).ShouldBe(["accepted", "accepted"]);
    }

    public static TheoryData<string, object> UnusableBoxes => new()
    {
        { "x below 0", new { x = -0.1, y = 0.1, width = 0.05, height = 0.8 } },
        { "x above 1", new { x = 1.5, y = 0.1, width = 0.05, height = 0.8 } },
        { "y below 0", new { x = 0.1, y = -0.5, width = 0.05, height = 0.8 } },
        { "width above 1", new { x = 0.0, y = 0.1, width = 1.5, height = 0.8 } },
        { "height above 1", new { x = 0.1, y = 0.0, width = 0.05, height = 1.2 } },
        { "zero width", new { x = 0.1, y = 0.1, width = 0.0, height = 0.8 } },
        { "zero height", new { x = 0.1, y = 0.1, width = 0.05, height = 0.0 } },
        { "negative width", new { x = 0.5, y = 0.1, width = -0.1, height = 0.8 } },
        { "runs past the right edge", new { x = 0.9, y = 0.1, width = 0.2, height = 0.8 } },
        { "runs past the bottom edge", new { x = 0.1, y = 0.9, width = 0.05, height = 0.3 } },
        { "no x", new { y = 0.1, width = 0.05, height = 0.8 } },
        { "no y", new { x = 0.1, width = 0.05, height = 0.8 } },
        { "no width", new { x = 0.1, y = 0.1, height = 0.8 } },
        { "no height", new { x = 0.1, y = 0.1, width = 0.05 } },
        { "an empty box", new { } },
        { "x is text", new { x = "left", y = 0.1, width = 0.05, height = 0.8 } },
        { "x is null", new { x = (double?)null, y = 0.1, width = 0.05, height = 0.8 } },
        { "x is a list", new { x = new[] { 0.1 }, y = 0.1, width = 0.05, height = 0.8 } },
        { "a member is an object", new { x = new { deeper = 1 }, y = 0.1, width = 0.05, height = 0.8 } },
        { "the box is text", "left" },
        { "the box is a number", 0.5 },
        { "the box is a list", new[] { 0.1, 0.1, 0.05, 0.8 } },
    };

    [Theory]
    [MemberData(nameof(UnusableBoxes))]
    public async Task A_box_that_cannot_be_used_never_refuses_the_scan_and_the_read_counts_as_having_no_box(string why, object box)
    {
        var (client, sessionId, _) = await StartAsync();

        // With no box the read is compared as reads always were, so it is a repeat of the same title read elsewhere.
        var response = await IdentifyAsync(client, sessionId, null, BookAt(Box(0.10)), BookAt(box));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, why + ": " + await response.Content.ReadAsStringAsync());
        var candidates = (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().ToList();
        candidates.Count.ShouldBe(2, why);
        candidates.Select(Decision).ShouldBe(["accepted", "rejected"], why);
        (await CountCandidatesAsync(sessionId)).ShouldBe(2);
    }

    [Fact]
    public async Task A_box_with_an_unwanted_member_is_read_by_the_members_it_has()
    {
        var (client, sessionId, _) = await StartAsync();

        var candidates = await IdentifyOnePictureAsync(
            client, sessionId, BookAt(new { x = 0.10, y = 0.1, width = 0.05, height = 0.8, note = "left spine" }), BookAt(Box(0.60)));

        candidates.Select(Decision).ShouldBe(["accepted", "accepted"]);
    }
}
