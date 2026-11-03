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

// A spine that prints only a series' subtitle ("nEMESIS", "GENERATIONS", "ILVER FLAMES") finds the wrong book or none, and the
// spine beside it names the series. Once the whole shelf has grounded, such a spine is asked again as "<series> <words read>".
// What that finds is offered for the person to pick, never matched, so it cannot add anything by itself. The reads are the
// misreads of a stored eval; the provider is scripted, so each test says what is asked and what is not.
public class IdentifySeriesNeighbourTests(ApiFixture api) : IdentifyTest(api)
{
    private readonly ScriptedByTitle _provider = new();

    private static Dictionary<string, object?> Spine(string title, double x, string kind = "film")
    {
        var read = Read(title: title, kind: kind);
        read["box"] = new { x, y = 0.1, width = 0.05, height = 0.8 };
        return read;
    }

    /// <summary>Two spines far from the rest, which make a shelf of four (a picture of four reads or more is a shelf).</summary>
    private static object[] Shelf(string kind, params Dictionary<string, object?>[] spines) =>
        [.. spines, Spine("ALPHA QUEST", 0.70, kind), Spine("BETA QUEST", 0.80, kind)];

    private static readonly ProviderRelease FirstContact = Release("Star Trek: First Contact", WorkKind.Film, "f1", 1996);

    private async Task<(HttpClient Client, Guid SessionId, TestLibrary Library)> StartAsync(bool autoAdd = true)
    {
        ConfigureServices = services =>
        {
            services.RemoveAll<ICatalogProvider>();
            services.AddSingleton<ICatalogProvider>(_provider);
        };
        var library = await CreateLibraryAsync();
        var client = As(library, LibraryRole.Editor);
        return (client, autoAdd ? await StartSessionWithAutoCommitAsync(client, library.Id) : await StartSessionWithoutAutoCommitAsync(client, library.Id), library);
    }

    private async Task<string[]> StepsOfAsync(JsonElement candidate, string provider)
    {
        await using var context = Db.CreateContext();
        var trace = await context.ScanCandidates.IgnoreQueryFilters().Where(c => c.Id == Id(candidate)).Select(c => c.TraceJson).SingleAsync();
        using var document = JsonDocument.Parse(trace ?? "{}");
        return document.RootElement.TryGetProperty("steps", out var steps)
            ? [.. steps.EnumerateArray().Where(s => s.GetProperty("provider").GetString() == provider).Select(s => s.GetProperty("outcome").GetString()!)]
            : [];
    }

    private ScriptedByTitle StarTrekShelf() => _provider
        .Know("STAR TREK FIRST CONTACT", FirstContact)
        .Know("Star Trek nEMESIS", Release("Star Trek: Nemesis", WorkKind.Film, "f2", 2002))
        .Know("Star Trek GENERATIONS", Release("Star Trek Generations", WorkKind.Film, "f3", 1994));

    // ---- the cases of the stored eval ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_nemesis_beside_First_Contact_that_found_only_the_novels_is_offered_the_film_and_never_added()
    {
        StarTrekShelf().Know("nEMESIS", Release("Nemesis", WorkKind.Film, "n1", 1998), Release("Nemesis", WorkKind.Film, "n2", 2005));
        var (client, sessionId, library) = await StartAsync();

        var shelf = await IdentifyOkAsync(client, sessionId, Shelf("film", Spine("STAR TREK FIRST CONTACT", 0.30), Spine("nEMESIS", 0.36)));

        Resolution(shelf[0]).ShouldBe("matched");
        Resolution(shelf[1]).ShouldBe("ambiguous");
        AlternativeTitles(shelf[1]).ShouldBe(["Star Trek: Nemesis"]);
        Decision(shelf[1]).ShouldBe("undecided");
        shelf[1].GetProperty("release").ValueKind.ShouldBe(JsonValueKind.Null);
        (await StepsOfAsync(shelf[1], "series")).ShouldBe(["Star Trek nEMESIS (series: Star Trek)"]);
        shelf[1].GetProperty("visibleText").GetString().ShouldBe("nEMESIS");
        (await ScanReaders.ItemsAsync(Db, library.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_generations_beside_First_Contact_that_found_nothing_is_offered_the_film()
    {
        StarTrekShelf();
        var (client, sessionId, _) = await StartAsync();

        var shelf = await IdentifyOkAsync(client, sessionId, Shelf("film", Spine("GENERATIONS", 0.24), Spine("STAR TREK FIRST CONTACT", 0.30)));

        Resolution(shelf[0]).ShouldBe("ambiguous");
        AlternativeTitles(shelf[0]).ShouldBe(["Star Trek Generations"]);
    }

    [Fact]
    public async Task Silver_Flames_between_two_Court_books_is_offered_the_book_found_with_their_leading_words()
    {
        _provider
            .Know("A COURT OF MIST AND FURY", Release("A Court of Mist and Fury", WorkKind.Book, "b1", 2016))
            .Know("A COURT OF WINGS AND RUIN", Release("A Court of Wings and Ruin", WorkKind.Book, "b2", 2017))
            .Know("A Court of ILVER FLAMES", Release("A Court of Silver Flames", WorkKind.Book, "b3", 2021));
        var (client, sessionId, _) = await StartAsync();

        var shelf = await IdentifyOkAsync(
            client, sessionId, Shelf("book", Spine("A COURT OF MIST AND FURY", 0.20, "book"), Spine("ILVER FLAMES", 0.26, "book"), Spine("A COURT OF WINGS AND RUIN", 0.32, "book")));

        Resolution(shelf[1]).ShouldBe("ambiguous");
        AlternativeTitles(shelf[1]).ShouldBe(["A Court of Silver Flames"]);
        (await StepsOfAsync(shelf[1], "series")).ShouldBe(["A Court of ILVER FLAMES (series: A Court of)"]);
    }

    // ---- when it must not ask ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_lone_spine_with_no_neighbour_carrying_a_series_asks_nothing()
    {
        StarTrekShelf().Know("nEMESIS", Release("Nemesis", WorkKind.Film, "n1", 1998), Release("Nemesis", WorkKind.Film, "n2", 2005));
        var (client, sessionId, _) = await StartAsync();

        var shelf = await IdentifyOkAsync(client, sessionId, Shelf("film", Spine("nEMESIS", 0.36)));

        Resolution(shelf[0]).ShouldBe("ambiguous");
        AlternativeTitles(shelf[0]).ShouldBe(["Nemesis", "Nemesis"]);
        _provider.Asked.ShouldNotContain(title => title.Contains("Star Trek", StringComparison.OrdinalIgnoreCase));
        (await StepsOfAsync(shelf[0], "series")).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_neighbour_whose_best_release_has_no_series_gives_the_spine_nothing_to_ask_with()
    {
        _provider.Know("CASABLANCA", Release("Casablanca", WorkKind.Film, "c1", 1942));
        var (client, sessionId, _) = await StartAsync();

        var shelf = await IdentifyOkAsync(client, sessionId, Shelf("film", Spine("CASABLANCA", 0.30), Spine("nEMESIS", 0.36)));

        Resolution(shelf[1]).ShouldBe("not_found");
        _provider.Asked.ShouldNotContain(title => title.StartsWith("Casablanca n", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_read_that_matched_is_never_asked_again_whatever_its_neighbours_carry()
    {
        StarTrekShelf().Know("nEMESIS", Release("Nemesis", WorkKind.Film, "n1", 1998));
        var (client, sessionId, _) = await StartAsync();

        var shelf = await IdentifyOkAsync(client, sessionId, Shelf("film", Spine("STAR TREK FIRST CONTACT", 0.30), Spine("nEMESIS", 0.36)));

        Resolution(shelf[1]).ShouldBe("matched");
        _provider.Asked.ShouldNotContain("Star Trek nEMESIS");
    }

    [Fact]
    public async Task A_read_with_no_box_never_asks()
    {
        StarTrekShelf();
        var (client, sessionId, _) = await StartAsync();
        var noBox = Read(title: "nEMESIS", kind: "film");

        var shelf = await IdentifyOkAsync(client, sessionId, [Spine("STAR TREK FIRST CONTACT", 0.30), noBox, Spine("ALPHA QUEST", 0.70), Spine("BETA QUEST", 0.80)]);

        Resolution(shelf[1]).ShouldBe("not_found");
        _provider.Asked.ShouldNotContain("Star Trek nEMESIS");
    }

    // ---- the budget ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_series_ask_the_budget_cut_short_leaves_the_read_pending_and_the_job_finishes_it_without_the_series()
    {
        Options.ResolveBudget = TimeSpan.FromSeconds(2);
        Options.JobResolveBudget = TimeSpan.FromSeconds(30);
        StarTrekShelf();
        _provider.Hangs = title => title == "Star Trek nEMESIS";
        var (client, sessionId, _) = await StartAsync(autoAdd: false);

        var shelf = await IdentifyOkAsync(client, sessionId, Shelf("film", Spine("STAR TREK FIRST CONTACT", 0.30), Spine("nEMESIS", 0.36)));

        Resolution(shelf[0]).ShouldBe("matched");
        Resolution(shelf[1]).ShouldBe("pending");
        (await StepsOfAsync(shelf[1], "series")).ShouldBe(["cut_short"]);

        // The background job grounds one read on its own: a stored read has no box, so it cannot ask with a neighbour's series.
        _provider.Hangs = null;
        await RunNextJobAsync();
        var stored = (await GetSessionAsync(client, sessionId)).GetProperty("candidates");
        var spine = stored.EnumerateArray().Single(c => c.GetProperty("visibleText").GetString() == "nEMESIS");
        Resolution(spine).ShouldBe("not_found");
    }

    [Fact]
    public async Task An_ambiguous_read_whose_series_ask_the_budget_cut_short_keeps_its_answer()
    {
        Options.ResolveBudget = TimeSpan.FromSeconds(2);
        StarTrekShelf().Know("nEMESIS", Release("Nemesis", WorkKind.Film, "n1", 1998), Release("Nemesis", WorkKind.Film, "n2", 2005));
        _provider.Hangs = title => title == "Star Trek nEMESIS";
        var (client, sessionId, _) = await StartAsync(autoAdd: false);

        var shelf = await IdentifyOkAsync(client, sessionId, Shelf("film", Spine("STAR TREK FIRST CONTACT", 0.30), Spine("nEMESIS", 0.36)));

        Resolution(shelf[1]).ShouldBe("ambiguous");
        AlternativeTitles(shelf[1]).ShouldBe(["Nemesis", "Nemesis"]);
    }
}
