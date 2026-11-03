using System.Text.Json;
using CollectionMaxxing.Api.Tests.Catalog;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Scanning.Identify;

// A cover that found nothing is asked again without its sequel number: the model read the PlayStation game "Wing Commander IV: The
// Price of Freedom" as "WING COMMANDER II THE PRICE OF FREEDOM", a title that does not exist, and the wrong number blocked the whole
// search. The game that turns up that way is offered for the person to pick, never matched, so it cannot be added by itself. The
// provider is the one that knows the game: its search finds nothing for the misread title and the game for the same title without
// the number, as a provider's word search does.
public class IdentifySequelNumberTests(ApiFixture api) : IdentifyTest(api)
{
    private const string Misread = "WING COMMANDER II THE PRICE OF FREEDOM";

    private const string Real = "Wing Commander IV: The Price of Freedom";

    private static Dictionary<string, object?> Cover(string title = Misread) => Read(title: title, kind: "game", platform: "PlayStation");

    /// <summary>The provider knows the game, but its search finds it for only one title: without the number (or the one given), not the misread one.</summary>
    private GameFoundOnlyAsTitle UseTheProvider(string title = "Wing Commander The Price of Freedom")
    {
        var provider = new GameFoundOnlyAsTitle(ScanData.Game(Real, platform: "PlayStation", year: 1997), title);
        Games.Enabled = false;
        ConfigureServices = services => services.AddSingleton<ICatalogProvider>(provider);
        return provider;
    }

    private async Task<(HttpClient Client, Guid SessionId)> StartAsync(bool autoAdd = true)
    {
        var library = await CreateLibraryAsync();
        var client = As(library, LibraryRole.Editor);
        return (client, autoAdd ? await StartSessionWithAutoCommitAsync(client, library.Id) : await StartSessionWithoutAutoCommitAsync(client, library.Id));
    }

    /// <summary>The outcome of each <c>numeral</c> step of the stored trace: the title it was asked with and the number it dropped.</summary>
    private async Task<string[]> NumeralStepsOfAsync(JsonElement candidate)
    {
        await using var context = Db.CreateContext();
        var trace = (await context.ScanCandidates.IgnoreQueryFilters().SingleAsync(c => c.Id == Id(candidate))).TraceJson;
        using var document = JsonDocument.Parse(trace ?? "{}");
        return document.RootElement.TryGetProperty("steps", out var steps)
            ? [.. steps.EnumerateArray().Where(s => s.GetProperty("provider").GetString() == "numeral").Select(s => s.GetProperty("outcome").GetString()!)]
            : [];
    }

    [Fact]
    public async Task A_cover_read_with_the_wrong_sequel_number_is_offered_the_game_found_without_it_and_not_added()
    {
        UseTheProvider();
        var (client, sessionId) = await StartAsync(autoAdd: true);

        var cover = (await IdentifyOkAsync(client, sessionId, Cover())).Single();

        Resolution(cover).ShouldBe("ambiguous");
        Decision(cover).ShouldBe("undecided");
        AlternativeTitles(cover).ShouldBe([Real]);
        cover.GetProperty("release").ValueKind.ShouldBe(JsonValueKind.Null);
        cover.GetProperty("visibleText").GetString().ShouldBe(Misread);
        (await NumeralStepsOfAsync(cover)).ShouldBe(["WING COMMANDER THE PRICE OF FREEDOM (dropped: II)"]);
    }

    [Fact]
    public async Task The_same_read_with_the_right_number_needs_no_retry()
    {
        UseTheProvider("Wing Commander IV The Price of Freedom");
        var (client, sessionId) = await StartAsync();

        var cover = (await IdentifyOkAsync(client, sessionId, Cover("WING COMMANDER IV THE PRICE OF FREEDOM"))).Single();

        AlternativeTitles(cover).ShouldBe([Real]);
        (await NumeralStepsOfAsync(cover)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_cover_read_with_no_sequel_number_in_it_stays_not_found()
    {
        Games.Enabled = false;
        var (client, sessionId) = await StartAsync();

        var cover = (await IdentifyOkAsync(client, sessionId, Cover("XQZTRWVKLMN PQZTRWVKLMN WVKLMNXQZTR"))).Single();

        Resolution(cover).ShouldBe("not_found");
        (await NumeralStepsOfAsync(cover)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_game_the_pending_job_finds_without_the_sequel_number_is_offered_the_same_way()
    {
        Options.ResolveBudget = TimeSpan.FromMilliseconds(300);
        var provider = UseTheProvider();
        provider.Slow = true;
        var (client, sessionId) = await StartAsync(autoAdd: false);
        Resolution((await IdentifyOkAsync(client, sessionId, Cover())).Single()).ShouldBe("pending");

        provider.Slow = false;
        await RunNextJobAsync();

        var stored = (await GetSessionAsync(client, sessionId)).GetProperty("candidates");
        var cover = stored.EnumerateArray().Single(c => c.GetProperty("visibleText").GetString() == Misread);
        Resolution(cover).ShouldBe("ambiguous");
        AlternativeTitles(cover).ShouldBe([Real]);
        (await NumeralStepsOfAsync(cover)).ShouldBe(["WING COMMANDER THE PRICE OF FREEDOM (dropped: II)"]);
    }
}
