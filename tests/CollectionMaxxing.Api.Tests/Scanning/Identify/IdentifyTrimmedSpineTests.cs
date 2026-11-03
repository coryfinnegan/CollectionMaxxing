using System.Diagnostics;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Catalog;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Scanning.Identify;

// A spine on a shelf that found nothing is asked again without the stray words at its ends: Vision read OCR junk in front of and
// behind the title ("XQZTRWVKLMNBHD IRON FOX PLMKJNHBGVFCDS" is the book "Iron Fox"), so the whole string is nothing like any title
// (the catalogue's trigram search wants a third of its trigrams in common). The book that turns up that way is offered for the
// person to pick, never matched, so a trimmed find cannot add a book by itself.
public class IdentifyTrimmedSpineTests(ApiFixture api) : IdentifyTest(api)
{
    private const string Junk = "XQZTRWVKLMNBHD IRON FOX PLMKJNHBGVFCDS";

    private static Dictionary<string, object?> Book(string title) => Read(title: title, kind: "book");

    /// <summary>A shelf: four reads make it one, the first three of two words (never retried), and the one given.</summary>
    private static object[] Shelf(string last) => [Book("Alpha Quest"), Book("Beta Quest"), Book("Gamma Quest"), Book(last)];

    /// <summary>The same stray print, in front of the title only, so that the span without it is exactly "IRON FOX" (the catalogue's search wants a third of the trigrams in common).</summary>
    private const string Stray = "XQZTRWVKLMNBHDPLMKJNHB IRON FOX";

    private Task SeedIronFoxAsync() => CatalogSeed.AddAsync(Db, "Iron Fox", WorkKind.Book, 1999, format: ReleaseFormat.Paperback);

    private async Task<(HttpClient Client, Guid SessionId)> StartAsync(bool autoAdd = true, bool seed = true)
    {
        var library = await CreateLibraryAsync();
        if (seed)
        {
            await SeedIronFoxAsync();
        }

        var client = As(library, LibraryRole.Editor);
        return (client, autoAdd ? await StartSessionWithAutoCommitAsync(client, library.Id) : await StartSessionWithoutAutoCommitAsync(client, library.Id));
    }

    private static string? Tier(JsonElement candidate) =>
        candidate.TryGetProperty("tier", out var tier) && tier.ValueKind == JsonValueKind.String ? tier.GetString() : null;

    private static bool Added(JsonElement candidate) =>
        candidate.TryGetProperty("createdItemId", out var id) && id.ValueKind == JsonValueKind.String;

    private async Task<string?> TraceOfAsync(JsonElement candidate)
    {
        await using var context = Db.CreateContext();
        return (await context.ScanCandidates.IgnoreQueryFilters().SingleAsync(c => c.Id == Id(candidate))).TraceJson;
    }

    /// <summary>The outcome of each <c>trimmed</c> step of the stored trace: which span found the book, and which words it dropped.</summary>
    private async Task<string[]> TrimmedStepsOfAsync(JsonElement candidate)
    {
        using var trace = JsonDocument.Parse(await TraceOfAsync(candidate) ?? "{}");
        return trace.RootElement.TryGetProperty("steps", out var steps)
            ? [.. steps.EnumerateArray().Where(s => s.GetProperty("provider").GetString() == "trimmed").Select(s => s.GetProperty("outcome").GetString()!)]
            : [];
    }

    [Fact]
    public async Task A_spine_whose_whole_read_finds_nothing_is_offered_the_book_a_trimmed_span_found()
    {
        var (client, sessionId) = await StartAsync();

        var spine = (await IdentifyOkAsync(client, sessionId, Shelf(Junk)))[3];

        Resolution(spine).ShouldBe("ambiguous");
        AlternativeTitles(spine).ShouldBe(["Iron Fox"]);
    }

    [Fact]
    public async Task What_a_span_found_is_never_matched_and_never_added_by_itself()
    {
        var (client, sessionId) = await StartAsync(autoAdd: true);

        // One long piece of junk in front: the span without it is exactly "IRON FOX", which asked on its own is a sure match.
        var spine = (await IdentifyOkAsync(client, sessionId, Shelf("XQZTRWVKLMNBHDPLMKJNHB IRON FOX")))[3];
        var exact = (await IdentifyOkAsync(client, sessionId, Shelf("IRON FOX")))[3];

        Resolution(exact).ShouldBe("matched");
        (await TrimmedStepsOfAsync(spine)).ShouldBe(["IRON FOX (dropped: XQZTRWVKLMNBHDPLMKJNHB)"]);
        Resolution(spine).ShouldBe("ambiguous");
        Decision(spine).ShouldBe("undecided");
        spine.GetProperty("release").ValueKind.ShouldBe(JsonValueKind.Null);
        AlternativeTitles(spine).ShouldBe(["Iron Fox"]);
    }

    [Fact]
    public async Task The_card_still_shows_what_was_read_and_the_trace_says_which_span_found_it()
    {
        var (client, sessionId) = await StartAsync();

        var spine = (await IdentifyOkAsync(client, sessionId, Shelf(Junk)))[3];

        spine.GetProperty("visibleText").GetString().ShouldBe(Junk);
        // The first span, one word off the start, already names the book well enough to offer; the later spans are never asked.
        (await TrimmedStepsOfAsync(spine)).ShouldBe(["IRON FOX PLMKJNHBGVFCDS (dropped: XQZTRWVKLMNBHD)"]);
    }

    [Fact]
    public async Task The_same_read_on_its_own_is_a_cover_and_finds_nothing_so_it_is_the_retry_that_found_the_book()
    {
        var (client, sessionId) = await StartAsync();

        var cover = (await IdentifyOkAsync(client, sessionId, Book(Junk))).Single();

        Resolution(cover).ShouldBe("not_found");
    }

    [Fact]
    public async Task A_spine_a_provider_could_not_answer_stays_pending_and_is_not_retried()
    {
        Options.ResolveBudget = TimeSpan.FromMilliseconds(300);
        Options.JobResolveBudget = TimeSpan.FromMilliseconds(300);
        Books.Behaviour = ct => Task.Delay(Timeout.Infinite, ct);
        var (client, sessionId) = await StartAsync();

        var spine = (await IdentifyOkAsync(client, sessionId, Shelf(Junk)))[3];

        Resolution(spine).ShouldBe("pending");
        (await TrimmedStepsOfAsync(spine)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_spine_none_of_whose_spans_finds_a_book_stays_not_found()
    {
        var (client, sessionId) = await StartAsync();

        var spine = (await IdentifyOkAsync(client, sessionId, Shelf("XQZTRWVKLMN PQZTRWVKLMN WVKLMNXQZTR")))[3];

        Resolution(spine).ShouldBe("not_found");
        (await TrimmedStepsOfAsync(spine)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_retry_the_providers_never_answer_ends_with_the_requests_own_budget_and_the_read_goes_pending_not_final_not_found()
    {
        Options.ResolveBudget = TimeSpan.FromSeconds(1);
        var calls = 0;

        // The shelf's first asks take 6 calls (one for each short read, three for the spine's own ladder); the retry's first never answers.
        Books.Behaviour = async ct =>
        {
            if (Interlocked.Increment(ref calls) > 6)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
        };
        var (client, sessionId) = await StartAsync();
        var clock = Stopwatch.StartNew();

        var spine = (await IdentifyOkAsync(client, sessionId, Shelf("XQZTRWVKLMNBHD PLMKJNHBGVFCDS WVKLMNBHQZ")))[3];

        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10)); // it ended: the budget cut the retry, nothing waited for it
        calls.ShouldBeGreaterThan(6); // the retry did ask
        Resolution(spine).ShouldBe("pending"); // the time ran out before the retry could finish: the background job finishes it
        (await TrimmedStepsOfAsync(spine)).ShouldBe(["cut_short"]);
    }

    [Fact]
    public async Task A_retry_the_requests_budget_cut_short_is_finished_by_the_background_job_with_its_own_budget()
    {
        Options.ResolveBudget = TimeSpan.FromSeconds(1);
        Options.JobResolveBudget = TimeSpan.FromSeconds(30);
        var calls = 0;
        var blocking = true;

        // As above, the shelf's first asks take 6 calls and the retry's first never answers (while the request lasts). Nothing
        // knows the book yet; it reaches the catalogue after the request, so only a retry that is run again can find it.
        Books.Behaviour = async ct =>
        {
            if (blocking && Interlocked.Increment(ref calls) > 6)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
        };
        var (client, sessionId) = await StartAsync(autoAdd: false, seed: false);
        Resolution((await IdentifyOkAsync(client, sessionId, Shelf(Stray)))[3]).ShouldBe("pending");

        blocking = false;
        await SeedIronFoxAsync();
        for (var job = 0; job < 2; job++)
        {
            await RunNextJobAsync();
        }

        var stored = (await GetSessionAsync(client, sessionId)).GetProperty("candidates");
        var spine = stored.EnumerateArray().Single(c => c.GetProperty("visibleText").GetString() == Stray);
        Resolution(spine).ShouldBe("ambiguous", "the job runs the whole grounding again, retry included, and finishes it");
        AlternativeTitles(spine).ShouldBe(["Iron Fox"]);
        (await TrimmedStepsOfAsync(spine)).ShouldBe(["IRON FOX (dropped: XQZTRWVKLMNBHDPLMKJNHB)"]);
    }

    [Fact]
    public async Task A_model_only_read_is_not_confirmed_by_a_Vision_read_that_found_the_same_book_only_through_a_trimmed_span()
    {
        var library = await CreateLibraryAsync();
        await SeedIronFoxAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id, new { autoCommitTiers = new[] { "T0", "T1" } });
        var model = Book("Iron Fox");
        model["unconfirmed"] = true;
        var body = Body(Guid.NewGuid(), [.. Shelf(Stray), model]);
        body["shotKind"] = "shelf_segment";

        var response = await editor.PostAsync(IdentifyUrl(sessionId), Json(body));
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var candidates = (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().ToList();

        var (vision, modelRead) = (candidates[3], candidates[4]);
        Resolution(vision).ShouldBe("ambiguous"); // Vision's read found the book only through the span "IRON FOX"
        (await TrimmedStepsOfAsync(vision)).ShouldBe(["IRON FOX (dropped: XQZTRWVKLMNBHDPLMKJNHB)"]);
        Decision(vision).ShouldBe("rejected"); // and is the weaker read of the book in that place, so it is skipped
        Resolution(modelRead).ShouldBe("matched");
        Tier(modelRead).ShouldBe("T2", "a trimmed find is no second engine agreeing: the model read is still model-only");
        Decision(modelRead).ShouldBe("undecided");
        Added(modelRead).ShouldBeFalse();
        (await ScanReaders.ItemsAsync(Db, library.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_book_the_pending_job_finds_through_a_span_is_offered_the_same_way()
    {
        Options.ResolveBudget = TimeSpan.FromMilliseconds(300);
        var slow = true;
        Books.Behaviour = async ct =>
        {
            if (slow)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
        };
        var (client, sessionId) = await StartAsync(autoAdd: false);
        Resolution((await IdentifyOkAsync(client, sessionId, Shelf(Junk)))[3]).ShouldBe("pending");

        slow = false;
        for (var job = 0; job < 4; job++)
        {
            await RunNextJobAsync(); // one resolve job for each of the four reads of the shelf, all of which were pending
        }

        var stored = (await GetSessionAsync(client, sessionId)).GetProperty("candidates");

        var spine = stored.EnumerateArray().Single(c => c.GetProperty("visibleText").GetString() == Junk);
        Resolution(spine).ShouldBe("ambiguous");
        AlternativeTitles(spine).ShouldBe(["Iron Fox"]);
    }

    // ---- a spine whose title is the author line: asked once more with its creator as the title ---------------------------------------

    private const string AuthorLine = "TESS HARROWGATE";

    /// <summary>The real title, sent as the creator: the sanitiser drops an author line with "the" in it, so only the creator as read still has it.</summary>
    private const string TitleAsCreator = "THE COPPER LANTERN";

    private static Dictionary<string, object?> SpineBy(string title, string creator) => Read(title: title, kind: "book", creator: creator);

    private Task SeedCopperLanternAsync() =>
        CatalogSeed.AddAsync(Db, "The Copper Lantern", WorkKind.Book, 1999, format: ReleaseFormat.Paperback, creators: ["Tess Harrowgate"]);

    [Fact]
    public async Task A_spine_whose_title_is_the_author_line_is_offered_the_book_its_creator_names()
    {
        var library = await CreateLibraryAsync();
        await SeedCopperLanternAsync();
        using var client = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithAutoCommitAsync(client, library.Id);

        var spine = (await IdentifyOkAsync(client, sessionId, [Book("Alpha Quest"), Book("Beta Quest"), Book("Gamma Quest"), SpineBy(AuthorLine, TitleAsCreator)]))[3];

        Resolution(spine).ShouldBe("ambiguous"); // the book scores a sure match, and is only offered
        Decision(spine).ShouldBe("undecided");
        Added(spine).ShouldBeFalse();
        spine.GetProperty("release").ValueKind.ShouldBe(JsonValueKind.Null);
        AlternativeTitles(spine).ShouldBe(["The Copper Lantern"]);
        spine.GetProperty("visibleText").GetString().ShouldBe(AuthorLine);
        (await SwappedStepsOfAsync(spine)).ShouldBe([$"{TitleAsCreator} (creator as read; title read: {AuthorLine})"]);
        (await ScanReaders.ItemsAsync(Db, library.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_same_title_without_a_creator_to_swap_with_stays_not_found()
    {
        var library = await CreateLibraryAsync();
        await SeedCopperLanternAsync();
        using var client = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithAutoCommitAsync(client, library.Id);

        var spine = (await IdentifyOkAsync(client, sessionId, [Book("Alpha Quest"), Book("Beta Quest"), Book("Gamma Quest"), Book(AuthorLine)]))[3];

        Resolution(spine).ShouldBe("not_found");
        (await SwappedStepsOfAsync(spine)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_book_the_pending_job_finds_by_the_creator_as_the_title_is_offered_the_same_way()
    {
        Options.ResolveBudget = TimeSpan.FromMilliseconds(300);
        var slow = true;
        Books.Behaviour = async ct =>
        {
            if (slow)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
        };
        var library = await CreateLibraryAsync();
        await SeedCopperLanternAsync();
        using var client = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(client, library.Id);
        Resolution((await IdentifyOkAsync(client, sessionId, [Book("Alpha Quest"), Book("Beta Quest"), Book("Gamma Quest"), SpineBy(AuthorLine, TitleAsCreator)]))[3])
            .ShouldBe("pending");

        slow = false;
        for (var job = 0; job < 4; job++)
        {
            await RunNextJobAsync(); // one resolve job for each of the four reads of the shelf, all of which were pending
        }

        var stored = (await GetSessionAsync(client, sessionId)).GetProperty("candidates");
        var spine = stored.EnumerateArray().Single(c => c.GetProperty("visibleText").GetString() == AuthorLine);
        Resolution(spine).ShouldBe("ambiguous", "the job asks with the creator as read, which the stored read kept");
        AlternativeTitles(spine).ShouldBe(["The Copper Lantern"]);
    }

    /// <summary>The outcome of each <c>swapped</c> step of the stored trace: the title it was asked with and the title it was read as.</summary>
    private async Task<string[]> SwappedStepsOfAsync(JsonElement candidate)
    {
        using var trace = JsonDocument.Parse(await TraceOfAsync(candidate) ?? "{}");
        return trace.RootElement.TryGetProperty("steps", out var steps)
            ? [.. steps.EnumerateArray().Where(s => s.GetProperty("provider").GetString() == "swapped").Select(s => s.GetProperty("outcome").GetString()!)]
            : [];
    }
}
