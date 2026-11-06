using System.Text.Json;
using CollectionMaxxing.Api.Tests.Catalog;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static CollectionMaxxing.Api.Tests.Catalog.ScriptedByTitle;

namespace CollectionMaxxing.Api.Tests.Evals;

/// <summary>
/// The eval measures what <c>/identify</c> does: a spine that is only a series' subtitle ("nEMESIS", "ILVER FLAMES") is asked
/// again with the series its neighbour carries by the one piece of code both run, so the same shelf gets the same verdicts and
/// the same releases from either. Real Postgres for the local catalogue (the trigram search, and the titles of the releases
/// the neighbours found), a scripted provider.
/// </summary>
public class EvalGroundingSeriesTests(ApiFixture api) : EvalTest(api)
{
    private static (string Title, double X)[] StarTrekShelf =>
        [("STAR TREK FIRST CONTACT", 0.30), ("nEMESIS", 0.36), ("ALPHA QUEST", 0.70), ("BETA QUEST", 0.80)];

    private static Dictionary<string, object?> BoxOf(Dictionary<string, object?> read, double x)
    {
        read["box"] = new { x, y = 0.1, width = 0.05, height = 0.8 };
        return read;
    }

    private async Task<IReadOnlyList<GroundedItem>> GroundedByTheEvalAsync((string Title, double X)[] shelf)
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<EvalGrounder>().GroundAsync(
            ScanEngine.VisionPipeline,
            [.. shelf.Select(s => new EvalReadItemInput(
                new IdentifyItemInput(
                    null, null, s.Title, s.Title, null, null, null, WorkKind.Film, null, null, null, null,
                    Box: new ReadBoxInput(s.X, 0.1, 0.05, 0.8)),
                null))],
            new EvalGroundingMemo(), printKind: null, CancellationToken.None);
    }

    private static async Task<List<JsonElement>> IdentifiedAsync(HttpClient client, Guid sessionId, (string Title, double X)[] shelf) =>
        await IdentifyOkAsync(client, sessionId, [.. shelf.Select(s => (object)BoxOf(Read(title: s.Title, kind: "film"), s.X))]);

    [Fact]
    public async Task The_eval_and_identify_give_a_shelf_with_a_provider_found_series_the_same_offer_of_the_next_film()
    {
        var provider = new ScriptedByTitle()
            .Know("STAR TREK FIRST CONTACT", Release("Star Trek: First Contact", WorkKind.Film, "f1", 1996))
            .Know("nEMESIS", Release("Nemesis", WorkKind.Film, "n1", 1998), Release("Nemesis", WorkKind.Film, "n2", 2005))
            .Know("Star Trek nEMESIS", Release("Star Trek: Nemesis", WorkKind.Film, "f2", 2002));
        ConfigureServices = services =>
        {
            services.RemoveAll<ICatalogProvider>();
            services.AddSingleton<ICatalogProvider>(provider);
        };
        var library = await CreateLibraryAsync();
        var client = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(client, library.Id);

        var eval = await GroundedByTheEvalAsync(StarTrekShelf); // a dry run: it leaves nothing in the catalogue for the live request to find
        var live = await IdentifiedAsync(client, sessionId, StarTrekShelf);

        var shown = live.Where(c => Decision(c) != "rejected").ToList(); // /identify skips the unfound spines; the eval does not score them
        eval.Select(i => i.Verdict).ShouldBe(shown.Select(Resolution));
        eval.Select(i => i.Candidates.Select(c => c.Title).ToList()).ShouldBe(shown.Select(AlternativeTitles));
        Resolution(live[1]).ShouldBe("ambiguous"); // and it was the series ask that found the film
        eval[1].Candidates.Select(c => c.Title).ShouldBe(["Star Trek: Nemesis", "Nemesis", "Nemesis"]);
    }

    [Fact]
    public async Task The_eval_and_identify_leave_a_shelf_as_it_was_when_the_series_round_runs_out_of_time()
    {
        var provider = new ScriptedByTitle()
            .Know("STAR TREK FIRST CONTACT", Release("Star Trek: First Contact", WorkKind.Film, "f1", 1996))
            .Know("nEMESIS", Release("Nemesis", WorkKind.Film, "n1", 1998), Release("Nemesis", WorkKind.Film, "n2", 2005))
            .Know("Star Trek nEMESIS", Release("Star Trek: Nemesis", WorkKind.Film, "f2", 2002));
        provider.Hangs = title => title == "Star Trek nEMESIS"; // the series round has the same fixed budget in both, and then stops
        ConfigureServices = services =>
        {
            services.RemoveAll<ICatalogProvider>();
            services.AddSingleton<ICatalogProvider>(provider);
        };
        var library = await CreateLibraryAsync();
        var client = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(client, library.Id);

        var eval = await GroundedByTheEvalAsync(StarTrekShelf);
        var live = await IdentifiedAsync(client, sessionId, StarTrekShelf);

        var shown = live.Where(c => Decision(c) != "rejected").ToList(); // /identify skips the unfound spines; the eval does not score them
        eval.Select(i => i.Verdict).ShouldBe(shown.Select(Resolution));
        eval.Select(i => i.Candidates.Select(c => c.Title).ToList()).ShouldBe(shown.Select(AlternativeTitles));
        AlternativeTitles(live[1]).ShouldBe(["Nemesis", "Nemesis"]);
        (await SeriesStepsOfAsync(live[1])).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_eval_and_identify_give_a_shelf_the_catalogue_holds_the_same_offer_of_the_next_film()
    {
        // Both films are in the local catalogue: the neighbour's best release is only an id until its title is looked up, and the
        // film found with the series is a catalogue release too.
        await CatalogSeed.AddAsync(Db, "Star Trek: First Contact", WorkKind.Film, 1996, format: ReleaseFormat.Dvd);
        await CatalogSeed.AddAsync(Db, "Star Trek: Nemesis", WorkKind.Film, 2002, format: ReleaseFormat.Dvd);
        await CatalogSeed.AddAsync(Db, "Nemesis", WorkKind.Film, 1998, format: ReleaseFormat.Dvd);
        await CatalogSeed.AddAsync(Db, "Nemesis", WorkKind.Film, 2005, format: ReleaseFormat.Dvd);
        var library = await CreateLibraryAsync();
        var client = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(client, library.Id);

        var eval = await GroundedByTheEvalAsync(StarTrekShelf); // a dry run: it leaves nothing in the catalogue for the live request to find
        var live = await IdentifiedAsync(client, sessionId, StarTrekShelf);

        var shown = live.Where(c => Decision(c) != "rejected").ToList(); // /identify skips the unfound spines; the eval does not score them
        eval.Select(i => i.Verdict).ShouldBe(shown.Select(Resolution));
        eval.Select(i => i.Candidates.Select(c => c.Title).ToList()).ShouldBe(shown.Select(AlternativeTitles));
        AlternativeTitles(live[1]).ShouldBe(["Star Trek: Nemesis", "Nemesis", "Nemesis"]);
        (await SeriesStepsOfAsync(live[1])).ShouldBe(["Star Trek nEMESIS (series: Star Trek)"]); // and it was the series ask that found it
    }

    private async Task<string[]> SeriesStepsOfAsync(JsonElement candidate)
    {
        await using var context = Db.CreateContext();
        var trace = await context.ScanCandidates.IgnoreQueryFilters().Where(c => c.Id == Id(candidate)).Select(c => c.TraceJson).SingleAsync();
        using var document = JsonDocument.Parse(trace ?? "{}");
        return [.. document.RootElement.GetProperty("steps").EnumerateArray().Where(s => s.GetProperty("provider").GetString() == "series").Select(s => s.GetProperty("outcome").GetString()!)];
    }
}
