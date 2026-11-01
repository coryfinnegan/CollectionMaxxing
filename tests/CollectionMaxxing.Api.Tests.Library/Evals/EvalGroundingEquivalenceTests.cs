using CollectionMaxxing.Api.Tests.Catalog;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CollectionMaxxing.Api.Tests.Evals;

/// <summary>
/// The eval grounder asks several reads at once, but the answer for a photo is the one it gave when it asked them one at a time:
/// same verdicts, same candidates in the same order with the same scores, the same repeats dropped. The expected lines below are
/// the sequential grounder's output for this photo, kept as they were; a provider that answers the later reads first shows that
/// the order is the photo's, not the order the answers came back in. Real Postgres for the local catalogue, a scripted provider.
/// </summary>
public class EvalGroundingEquivalenceTests(ApiFixture api) : EvalTest(api)
{
    private const string Nintendo = "Nintendo 64";

    private static readonly Dictionary<string, ProviderRelease[]> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["lantern keep legends"] = [Game("Lantern Keep Legends", 1997, "g1")],
        ["ember tide"] = [Game("Ember Tide", 1997, "g2"), Game("Ember Tide II", 1999, "g3")],
        ["harbor lights"] = [Book("Harbor Lights", 2004, "b1")],
        ["dusk rider"] = [Book("Dusk Rider", 1988, "b2", "Mara Vell")],
        ["iron fox quest part"] = [Game("Iron Fox Quest Part", 2001, "g4")],
    };

    private static ProviderRelease Game(string title, int year, string id) => new()
    {
        Provider = "scripted",
        ExternalId = id,
        Title = title,
        Kind = WorkKind.Game,
        Format = ReleaseFormat.GameCartridge,
        Platform = Nintendo,
        Year = year,
    };

    private static ProviderRelease Book(string title, int year, string id, string? creator = null) => new()
    {
        Provider = "scripted",
        ExternalId = id,
        Title = title,
        Kind = WorkKind.Book,
        Year = year,
        Creators = creator is null ? [] : [creator],
    };

    /// <summary>Answers from <see cref="Known"/> by title, later titles first (the shorter the title, the longer the wait), and fails for one title.</summary>
    private sealed class ScriptedProvider : ICatalogProvider, ISearchableCatalogProvider
    {
        public string Name => "scripted";

        public bool Supports(WorkKind kind) => true;

        public Task<ProviderRelease?> LookupAsync(Identifier id, CancellationToken ct) => Task.FromResult<ProviderRelease?>(null);

        public async Task<IReadOnlyList<ProviderRelease>> SearchAsync(CatalogQuery query, CancellationToken ct)
        {
            await Task.Delay(5 + (40 - Math.Min(query.Title.Length, 35)), ct);
            if (query.Title.StartsWith("Pending", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("the provider is down");
            }

            return Known.TryGetValue(query.Title, out var found) ? found : [];
        }
    }

    private static EvalReadItemInput Read(
        string? title, string? kind = "game", string? platform = Nintendo, string? creator = null) => new(
        new IdentifyItemInput(
            null, null, title, title, creator, null, null, kind == "book" ? WorkKind.Book : WorkKind.Game,
            kind == "book" ? ReleaseFormat.Paperback : ReleaseFormat.GameCartridge, null, platform, null),
        null);

    private static readonly EvalReadItemInput[] Photo =
    [
        Read("Lantern Keep Legends"),
        Read("Moonlit Cartridge"),
        Read("Ember Tide"),
        Read("Zzxq Plmk"),
        Read("Lantern Keep Legends"),
        Read(null),
        Read("Pending Title Here"),
        Read("Harbor Lights", "book", null),
        Read("Ember Tide", "book", null),
        Read("Dusk Rider", "book", null, "Mara Vell"),
        Read("Moonlit Cartridge"),
        Read("Iron Fox Quest Part Zxqv"),
        Read("Harbor Lights", "book", null),
    ];

    private async Task<string[]> GroundAsync(int? parallelism)
    {
        ConfigureServices = services =>
        {
            services.RemoveAll<ICatalogProvider>();
            services.AddSingleton<ICatalogProvider>(new ScriptedProvider());
        };
        if (parallelism is { } bound)
        {
            Settings["Evals:GroundingParallelism"] = bound.ToString();
        }

        await CatalogSeed.AddAsync(Db, "Moonlit Cartridge", WorkKind.Game, 1998, format: ReleaseFormat.GameCartridge);
        await CatalogSeed.AddAsync(Db, "Harbor Lights", WorkKind.Book, 2004, format: ReleaseFormat.Paperback);

        using var scope = Factory.Services.CreateScope();
        var grounder = scope.ServiceProvider.GetRequiredService<EvalGrounder>();
        var items = await grounder.GroundAsync(ScanEngine.VisionPipeline, Photo, new EvalGroundingMemo(), printKind: null, CancellationToken.None);
        return [.. items.Select(Line)];
    }

    private static string Line(GroundedItem item) =>
        $"{item.ReadTitle} | {item.Verdict} | {item.Read!.Profile} | "
        + string.Join(" ; ", item.Candidates.Select(c =>
            $"{c.Title}/{c.Kind}/{c.Format}/{c.Platform}/{c.Year}/{c.Score:F6}/{string.Join('+', c.Creators ?? [])}/{string.Join(',', (c.Terms ?? []).Select(t => $"{t.Name}={t.Value:F4}{(t.Compared ? "" : "?")}"))}"));

    private static readonly string[] Sequential =
    [
        "Lantern Keep Legends | matched | loose_cartridge | Lantern Keep Legends/game/game_cartridge/Nintendo 64/1997/1.000000//title=1.0000,type=1.0000,platform=1.0000",
        "Moonlit Cartridge | matched | loose_cartridge | Moonlit Cartridge/game/game_cartridge//1998/1.000000//title=1.0000,type=1.0000",
        "Ember Tide | matched | loose_cartridge | Ember Tide/game/game_cartridge/Nintendo 64/1997/1.000000//title=1.0000,type=1.0000,platform=1.0000 ; Ember Tide II/game/game_cartridge/Nintendo 64/1999/0.800000//title=0.6250,type=1.0000,platform=1.0000",
        "Zzxq Plmk | not_found | loose_cartridge | ",
        " | not_found | loose_cartridge | ",
        "Pending Title Here | pending | loose_cartridge | ",
        "Harbor Lights | matched | shelf_spine | Harbor Lights/book/paperback//2004/1.000000//title=1.0000,type=1.0000",
        "Dusk Rider | matched | shelf_spine | Dusk Rider/book/paperback//1988/1.000000/Mara Vell/title=1.0000,type=1.0000,creator=1.0000",
        "Iron Fox Quest Part Zxqv | ambiguous | loose_cartridge | Iron Fox Quest Part/game/game_cartridge/Nintendo 64/2001/0.893333//title=0.8000,type=1.0000,platform=1.0000",
    ];

    [Fact]
    public async Task The_photo_is_grounded_to_what_the_sequential_grounder_gave()
    {
        var lines = await GroundAsync(parallelism: null);

        lines.ShouldBe(Sequential);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(32)]
    public async Task The_answer_does_not_depend_on_how_many_reads_are_asked_at_once(int parallelism)
    {
        var lines = await GroundAsync(parallelism);

        lines.ShouldBe(Sequential);
    }
}
