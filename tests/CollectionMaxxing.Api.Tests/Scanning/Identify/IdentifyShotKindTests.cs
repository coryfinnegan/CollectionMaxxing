using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Catalog;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static CollectionMaxxing.Api.Tests.Catalog.ScriptedByTitle;

namespace CollectionMaxxing.Api.Tests.Scanning.Identify;

// A spine on a shelf of books whose read grounded to films ("cnoi Emergency Contact o") is asked once more as a book, after the
// whole shelf has grounded: the spines beside it matched books, so only the book providers are asked. What that finds is offered
// for the person to pick, never matched, so it cannot add anything by itself, and the film stays one of the picks. The reads are
// the misreads of a stored eval; the providers are scripted and know their own kind only, as the real ones do.
public class IdentifyShotKindTests(ApiFixture api) : IdentifyTest(api)
{
    /// <summary>A word no other test can have left in the shared catalogue: a release a snap ensured stays there, and the local search would find it next time.</summary>
    private string Tag { get; } = $"Zq{Guid.NewGuid():N}"[..14];

    private string Misread => $"cnoi Emergency Contact {Tag} o";

    private readonly ScriptedByKind _books = new(WorkKind.Book);
    private readonly ScriptedByKind _films = new(WorkKind.Film);

    private Dictionary<string, object?> Spine(string title, string? creator = null) => Read(title: $"{title} {Tag}", creator: creator);

    private static Dictionary<string, object?> Lone(string title) => Read(title: title);

    private ProviderRelease Rel(string title, WorkKind kind, string id, int year, params string[] creators) =>
        Release($"{title} {Tag}", kind, $"{id}-{Tag}", year, creators);

    /// <summary>Three spines that name their author, which is what lets a book be Matched, then the reads given (a picture of four reads or more is a shelf).</summary>
    private object[] BookShelf(params Dictionary<string, object?>[] others) =>
        [Spine("A COURT OF MIST AND FURY", "Sarah J. Maas"), Spine("A COURT OF WINGS AND RUIN", "Sarah J. Maas"), Spine("ASSASSINS APPRENTICE", "Robin Hobb"), .. others];

    private void ArrangeBooksAndFilms()
    {
        _books
            .Know($"A COURT OF MIST AND FURY {Tag}", false, Rel("A Court of Mist and Fury", WorkKind.Book, "m1", 2016, "Sarah J. Maas"))
            .Know($"A COURT OF WINGS AND RUIN {Tag}", false, Rel("A Court of Wings and Ruin", WorkKind.Book, "m2", 2017, "Sarah J. Maas"))
            .Know($"ASSASSINS APPRENTICE {Tag}", false, Rel("Assassins Apprentice", WorkKind.Book, "m3", 1995, "Robin Hobb"))
            .Know(Misread, true, Rel("Emergency Contact", WorkKind.Book, "b1", 2018, "Mary H. K. Choi"));
        _films.Know(
            Misread,
            false,
            Rel("Emergency Contact", WorkKind.Film, "f1", 2019),
            Rel("Emergency Contact", WorkKind.Film, "f2", 2020),
            Rel("Emergency Contact", WorkKind.Film, "f3", 2021));
    }

    private async Task<(HttpClient Client, Guid SessionId, TestLibrary Library)> StartAsync(bool autoAdd = true)
    {
        ConfigureServices = services =>
        {
            services.RemoveAll<ICatalogProvider>();
            services.AddSingleton<ICatalogProvider>(_books);
            services.AddSingleton<ICatalogProvider>(_films);
        };
        var library = await CreateLibraryAsync();
        var client = As(library, LibraryRole.Editor);
        return (client, autoAdd ? await StartSessionWithAutoCommitAsync(client, library.Id) : await StartSessionWithoutAutoCommitAsync(client, library.Id), library);
    }

    private static List<string> AlternativeFormats(JsonElement candidate) =>
        [.. candidate.GetProperty("alternatives").EnumerateArray().Select(a => a.GetProperty("format").GetString()!)];

    private async Task<string[]> KindStepsOfAsync(JsonElement candidate)
    {
        await using var context = Db.CreateContext();
        var trace = await context.ScanCandidates.IgnoreQueryFilters().Where(c => c.Id == Id(candidate)).Select(c => c.TraceJson).SingleAsync();
        using var document = JsonDocument.Parse(trace ?? "{}");
        return document.RootElement.TryGetProperty("steps", out var steps)
            ? [.. steps.EnumerateArray().Where(s => s.GetProperty("provider").GetString() == "kind" && s.GetProperty("outcome").GetString() != "biased").Select(s => s.GetProperty("outcome").GetString()!)]
            : [];
    }

    private static async Task<List<JsonElement>> IdentifyPrintedAsync(HttpClient client, Guid sessionId, string printKind, params object[] items)
    {
        var body = Body(Guid.NewGuid(), items);
        body["printKind"] = printKind;
        var response = await client.PostAsync(IdentifyUrl(sessionId), Json(body));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().ToList();
    }

    // ---- the cases of the stored eval ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_read_beside_matched_books_that_found_three_films_is_offered_the_book_first_and_never_added()
    {
        ArrangeBooksAndFilms();
        var (client, sessionId, library) = await StartAsync();

        var shelf = await IdentifyOkAsync(client, sessionId, BookShelf(Lone(Misread)));

        shelf.Take(3).Select(Resolution).ShouldAllBe(r => r == "matched");
        Resolution(shelf[3]).ShouldBe("ambiguous");
        AlternativeFormats(shelf[3]).ShouldBe(["paperback", "dvd", "dvd"]); // the book first, the films after it
        Decision(shelf[3]).ShouldBe("undecided");
        shelf[3].GetProperty("release").ValueKind.ShouldBe(JsonValueKind.Null);
        (await KindStepsOfAsync(shelf[3])).ShouldBe([$"{Misread} (kind: book)"]);
        shelf[3].GetProperty("visibleText").GetString().ShouldBe(Misread);
        (await ScanReaders.ItemsAsync(Db, library.Id)).ShouldBeEmpty(); // nothing the shelf read is added by itself, and the offered book least of all
        _books.Asked.ShouldContain((Misread, WorkKind.Book));
    }

    [Fact]
    public async Task The_shelfs_own_print_is_enough_when_no_spine_matched()
    {
        ArrangeBooksAndFilms();
        var (client, sessionId, _) = await StartAsync(autoAdd: false);

        var shelf = await IdentifyPrintedAsync(client, sessionId, "book", Lone(Misread), Lone("GAMMA QUEST"), Lone("DELTA QUEST"), Lone("EPSILON QUEST"));

        Resolution(shelf[0]).ShouldBe("ambiguous");
        AlternativeFormats(shelf[0]).ShouldBe(["paperback", "dvd", "dvd"]);
    }

    // ---- when it must not ask ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_shelf_of_films_is_never_asked_as_books()
    {
        ArrangeBooksAndFilms();
        var (client, sessionId, _) = await StartAsync(autoAdd: false);

        var shelf = await IdentifyPrintedAsync(client, sessionId, "film", Lone(Misread), Lone("GAMMA QUEST"), Lone("DELTA QUEST"), Lone("EPSILON QUEST"));

        Resolution(shelf[0]).ShouldBe("ambiguous");
        AlternativeFormats(shelf[0]).ShouldBe(["dvd", "dvd", "dvd"]);
        _books.Asked.ShouldNotContain(ask => ask.Kind == WorkKind.Book);
        (await KindStepsOfAsync(shelf[0])).ShouldBeEmpty();
    }

    [Fact]
    public async Task Two_matched_books_are_not_a_shelf_and_nothing_is_asked_as_a_book()
    {
        ArrangeBooksAndFilms();
        var (client, sessionId, _) = await StartAsync(autoAdd: false);

        var shelf = await IdentifyOkAsync(
            client, sessionId, Spine("A COURT OF MIST AND FURY", "Sarah J. Maas"), Spine("ASSASSINS APPRENTICE", "Robin Hobb"), Lone(Misread), Lone("GAMMA QUEST"));

        AlternativeFormats(shelf[2]).ShouldBe(["dvd", "dvd", "dvd"]);
        _books.Asked.ShouldNotContain(ask => ask.Kind == WorkKind.Book);
    }

    [Fact]
    public async Task A_read_that_names_its_own_kind_is_never_asked_again()
    {
        ArrangeBooksAndFilms();
        var (client, sessionId, _) = await StartAsync(autoAdd: false);

        var shelf = await IdentifyOkAsync(client, sessionId, BookShelf(Read(title: Misread, kind: "film")));

        AlternativeFormats(shelf[3]).ShouldBe(["dvd", "dvd", "dvd"]);
        (await KindStepsOfAsync(shelf[3])).ShouldBeEmpty();
    }

    // ---- the budget ----------------------------------------------------------------------------------------------------------------

    // The pass has its own fixed budget (ShotKindRule.Budget), whatever is left of the request's: the pending job cannot make this
    // pass (it grounds one read at a time, with no neighbours' answers), so a pass that runs out leaves every answer as it was, never
    // Pending. The request budget stays at the default two minutes in these tests, so an ask that never answers ends the request
    // only because the pass's own budget cancels it.

    [Fact]
    public async Task A_pass_that_ran_out_of_time_leaves_the_read_with_its_films()
    {
        ArrangeBooksAndFilms();
        _books.Hangs = (title, kind) => title == Misread && kind == WorkKind.Book;
        var (client, sessionId, _) = await StartAsync(autoAdd: false);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var shelf = await IdentifyOkAsync(client, sessionId, BookShelf(Lone(Misread)));

        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30)); // the pass ended itself; the request budget is two minutes
        Resolution(shelf[3]).ShouldBe("ambiguous");
        AlternativeFormats(shelf[3]).ShouldBe(["dvd", "dvd", "dvd"]);
        (await KindStepsOfAsync(shelf[3])).ShouldBeEmpty();
        _books.Asked.ShouldContain((Misread, WorkKind.Book));
    }
}
