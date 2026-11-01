using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Api.Tests.Scanning.Identify;

// One book, one candidate per photo. The engine sometimes reads one spine twice in one picture, once clean and once with OCR
// junk on the end, and both reads end at the same release. The strongest read stands; the other stays on the response (the
// phone draws a line for every item it read) but is skipped, so it can be neither auto-added nor committed as an extra copy.
// Neutral titles throughout.
public class RepeatReadTests(ApiFixture api) : IdentifyTest(api)
{
    private const string Clean = "Simple Beautiful Food";
    private const string Junk = "Simple Beautiful FOod FREDEROCKSOH";

    private static object OptIn() => new { autoCommitTiers = new[] { "T0", "T1" } };

    private static string? Tier(JsonElement candidate) =>
        candidate.TryGetProperty("tier", out var tier) && tier.ValueKind == JsonValueKind.String ? tier.GetString() : null;

    private static bool Added(JsonElement candidate) =>
        candidate.TryGetProperty("createdItemId", out var id) && id.ValueKind == JsonValueKind.String;

    private static Dictionary<string, object?> Book(string title) => Read(title: title, kind: "book", creator: "Mara Odell");

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

    private static async Task<List<JsonElement>> IdentifyOnePictureAsync(HttpClient client, Guid sessionId, params object[] reads)
    {
        var body = Body(Guid.NewGuid(), reads);
        body["shotKind"] = "shelf_segment";
        var response = await client.PostAsync(IdentifyUrl(sessionId), Json(body));
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().ToList();
    }

    [Fact]
    public async Task A_clean_matched_read_stands_and_the_ambiguous_repeat_read_before_it_is_skipped()
    {
        var library = await CreateLibraryAsync();
        Books.Results.Add(Provided(Clean, "OL1W"));
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, OptIn());

        var candidates = await IdentifyOnePictureAsync(editor, sessionId, Book(Junk), Book(Clean));

        candidates.Count.ShouldBe(2, "the phone drew a line for each read and needs a card for each");
        var (junk, clean) = (candidates[0], candidates[1]);
        Resolution(junk).ShouldBe("ambiguous");
        Resolution(clean).ShouldBe("matched");

        Decision(clean).ShouldBe("accepted");
        Tier(clean).ShouldBe("T1");
        Added(clean).ShouldBeTrue();

        Decision(junk).ShouldBe("rejected");
        Tier(junk).ShouldBe("T2");
        Added(junk).ShouldBeFalse();

        (await ScanReaders.ItemsAsync(Db, library.Id)).Count.ShouldBe(1);
        var counters = (await GetSessionAsync(editor, sessionId)).GetProperty("counters");
        counters.GetProperty("added").GetInt32().ShouldBe(1);
        counters.GetProperty("skipped").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task A_clean_matched_read_stands_and_the_ambiguous_repeat_read_after_it_is_skipped()
    {
        var library = await CreateLibraryAsync();
        Books.Results.Add(Provided(Clean, "OL1W"));
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, OptIn());

        var candidates = await IdentifyOnePictureAsync(editor, sessionId, Book(Clean), Book(Junk));

        Decision(candidates[0]).ShouldBe("accepted");
        Added(candidates[0]).ShouldBeTrue();
        Decision(candidates[1]).ShouldBe("rejected");
        Added(candidates[1]).ShouldBeFalse();
        (await ScanReaders.ItemsAsync(Db, library.Id)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Committing_a_session_with_a_repeat_read_adds_the_book_once()
    {
        var library = await CreateLibraryAsync();
        Books.Results.Add(Provided(Clean, "OL1W"));
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(editor, library.Id);

        var candidates = await IdentifyOnePictureAsync(editor, sessionId, Book(Junk), Book(Clean));
        Decision(candidates[1]).ShouldBe("accepted");
        Decision(candidates[0]).ShouldBe("rejected");

        var commit = await CommitAsync(editor, sessionId);

        commit.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK, await commit.Content.ReadAsStringAsync());
        (await ScanReaders.ItemsAsync(Db, library.Id)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Between_two_ambiguous_reads_of_one_book_the_one_with_the_higher_score_is_the_candidate_to_pick_from()
    {
        var library = await CreateLibraryAsync();
        Books.Results.Add(Provided(Clean, "OL1W"));
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(editor, library.Id);

        var candidates = await IdentifyOnePictureAsync(
            editor, sessionId, Book("Simple Beautiful FOod FREDEROCKSOH SIMON"), Book("Simple Beautiful FOod FREDEROCKSOH"));

        Resolution(candidates[0]).ShouldBe("ambiguous");
        Resolution(candidates[1]).ShouldBe("ambiguous");
        var scores = candidates.Select(c => c.GetProperty("score").GetDouble()).ToList();
        scores[1].ShouldBeGreaterThan(scores[0]);
        Decision(candidates[0]).ShouldBe("rejected");
        Decision(candidates[1]).ShouldBe("undecided");
    }

    [Fact]
    public async Task Reads_of_two_different_books_stay_two_candidates()
    {
        var library = await CreateLibraryAsync();
        Books.Results.Add(Provided(Clean, "OL1W"));
        Books.Results.Add(Provided("Quiet Harbor Atlas", "OL2W"));
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, OptIn());

        var candidates = await IdentifyOnePictureAsync(editor, sessionId, Book(Clean), Book("Quiet Harbor Atlas"));

        candidates.ShouldAllBe(c => Resolution(c) == "matched");
        candidates.ShouldAllBe(c => Decision(c) == "accepted");
        candidates.ShouldAllBe(c => Added(c));
        (await ScanReaders.ItemsAsync(Db, library.Id)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Reads_that_found_nothing_are_never_skipped_as_repeats()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, OptIn());

        var candidates = await IdentifyOnePictureAsync(editor, sessionId, Book("Zq Nothing Like It 1"), Book("Zq Nothing Like It 1"));

        candidates.ShouldAllBe(c => Resolution(c) == "not_found");
        candidates.ShouldAllBe(c => Decision(c) == "undecided");
    }

    [Fact]
    public async Task The_same_book_in_a_second_picture_is_not_a_repeat_of_the_first()
    {
        var library = await CreateLibraryAsync();
        Books.Results.Add(Provided(Clean, "OL1W"));
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(editor, library.Id);

        var first = await IdentifyOnePictureAsync(editor, sessionId, Book(Clean));
        var second = await IdentifyOnePictureAsync(editor, sessionId, Book(Clean));

        Decision(first[0]).ShouldBe("accepted");
        Decision(second[0]).ShouldBe("accepted");
    }

    // ---- a cross-engine repeat of the same book confirms the read that stands (#622) ---------------------------------------

    [Fact]
    public async Task A_model_read_that_wins_a_repeat_of_the_same_book_Vision_also_read_is_not_model_only()
    {
        var library = await CreateLibraryAsync();
        Books.Results.Add(Provided(Clean, "OL1W"));
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, OptIn());
        var vision = Book(Junk);
        var model = Book(Clean);
        model["unconfirmed"] = true;

        var candidates = await IdentifyOnePictureAsync(editor, sessionId, vision, model);

        var (visionRead, modelRead) = (candidates[0], candidates[1]);
        Resolution(visionRead).ShouldBe("ambiguous");
        Resolution(modelRead).ShouldBe("matched");
        Decision(modelRead).ShouldBe("accepted", "Vision read the same book there, so the model's winning read is not model-only");
        Tier(modelRead).ShouldBe("T1");
        Added(modelRead).ShouldBeTrue();
        Decision(visionRead).ShouldBe("rejected");
        (await ScanReaders.ItemsAsync(Db, library.Id)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_model_read_that_loses_a_repeat_of_the_same_book_to_Vision_stays_as_it_was()
    {
        var library = await CreateLibraryAsync();
        Books.Results.Add(Provided(Clean, "OL1W"));
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, OptIn());
        var vision = Book(Clean);
        var model = Book(Junk);
        model["unconfirmed"] = true;

        var candidates = await IdentifyOnePictureAsync(editor, sessionId, vision, model);

        Decision(candidates[0]).ShouldBe("accepted");
        Tier(candidates[0]).ShouldBe("T1");
        Decision(candidates[1]).ShouldBe("rejected", "the model's read lost the repeat, so it is skipped as any weaker repeat is");
    }

    [Fact]
    public async Task Two_model_reads_of_the_same_book_in_one_place_are_not_confirmed_by_each_other()
    {
        var library = await CreateLibraryAsync();
        Books.Results.Add(Provided(Clean, "OL1W"));
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, OptIn());
        var junk = Book(Junk);
        junk["unconfirmed"] = true;
        var clean = Book(Clean);
        clean["unconfirmed"] = true;

        var candidates = await IdentifyOnePictureAsync(editor, sessionId, junk, clean);

        Tier(candidates[1]).ShouldBe("T2", "one engine reading the spine twice is not two engines agreeing on it");
        Decision(candidates[1]).ShouldBe("undecided");
    }

    [Fact]
    public async Task A_barcode_read_of_a_book_is_not_a_repeat_of_a_picture_read_of_it()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(55801);
        await CatalogAsync(Clean, code, ReleaseFormat.Paperback, WorkKind.Book, 2015);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(editor, library.Id);

        var scanned = (await ScanAsync(editor, sessionId, code))[0];
        var read = (await IdentifyOnePictureAsync(editor, sessionId, Book(Clean)))[0];

        Decision(scanned).ShouldBe("accepted");
        Resolution(read).ShouldBe("matched");
        Decision(read).ShouldBe("accepted");
    }
}
