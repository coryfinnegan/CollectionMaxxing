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

    private async Task<(HttpClient Client, Guid SessionId)> StartAsync(bool autoAdd = true)
    {
        var library = await CreateLibraryAsync();
        await CatalogSeed.AddAsync(Db, "Iron Fox", WorkKind.Book, 1999, format: ReleaseFormat.Paperback);
        var client = As(library, LibraryRole.Editor);
        return (client, autoAdd ? await StartSessionWithAutoCommitAsync(client, library.Id) : await StartSessionWithoutAutoCommitAsync(client, library.Id));
    }

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
    public async Task A_retry_the_providers_never_answer_ends_with_the_requests_own_budget_and_the_not_found_stands()
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
        Resolution(spine).ShouldBe("not_found");
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
}
