using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Api.Tests.Scanning.Identify;

// A read that names no platform lists first, in a close call, the release on a platform the snap's shot prints: a line of any read of the
// snap that is, as a whole line, a console's name. Ordering only. Neutral titles throughout.
public class IdentifyPrintedPlatformTests(ApiFixture api) : IdentifyTest(api)
{
    /// <summary>A title no other test can have left in the shared catalogue.</summary>
    private string Title { get; } = $"Quiet Harbor Atlas {Guid.NewGuid():N}";

    private const int BookYear = 2001;
    private const int GameYear = 2002;

    /// <summary>The title as a book and as a PS2 game, the book first as the providers answered them. The read names neither kind nor platform.</summary>
    private void ArrangeTiedBookAndGame()
    {
        Games.Results.Add(new ProviderRelease { Provider = "igdb", ExternalId = "b1", Title = Title, Kind = WorkKind.Book, Year = BookYear });
        Games.Results.Add(new ProviderRelease { Provider = "igdb", ExternalId = "g1", Title = Title, Kind = WorkKind.Game, Platform = "PS2", Year = GameYear });
    }

    private static Dictionary<string, object?> Neighbour(string? line = null, string? platform = null)
    {
        var read = Read(title: "Lantern Row Part 1", platform: platform);
        if (line is not null)
        {
            read["lines"] = new[] { "LANTERN ROW", line };
        }

        return read;
    }

    private static List<int> AlternativeYears(JsonElement candidate) =>
        [.. candidate.GetProperty("alternatives").EnumerateArray().Select(a => a.GetProperty("year").GetInt32())];

    private async Task<List<int>> YearsOfTheKindlessReadBesideAsync(Dictionary<string, object?> neighbour, Dictionary<string, object?>? kindless = null)
    {
        ArrangeTiedBookAndGame();
        var library = await CreateLibraryAsync();
        var client = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(client, library.Id);

        var candidates = await IdentifyOkAsync(client, sessionId, neighbour, kindless ?? Read(title: Title));

        Resolution(candidates[1]).ShouldBe("ambiguous");
        return AlternativeYears(candidates[1]);
    }

    [Fact]
    public async Task A_neighbours_line_that_is_a_consoles_name_lists_the_game_on_that_console_first()
    {
        (await YearsOfTheKindlessReadBesideAsync(Neighbour("PlayStation.2"))).ShouldBe([GameYear, BookYear]);
    }

    [Fact]
    public async Task With_no_console_printed_the_order_stands()
    {
        (await YearsOfTheKindlessReadBesideAsync(Neighbour("LANTERN ROW"))).ShouldBe([BookYear, GameYear]);
    }

    [Fact]
    public async Task A_console_named_inside_a_longer_line_prints_nothing()
    {
        (await YearsOfTheKindlessReadBesideAsync(Neighbour("PlayStation 2 Secret Codes"))).ShouldBe([BookYear, GameYear]);
    }

    [Fact]
    public async Task A_reads_own_platform_guess_prints_nothing()
    {
        (await YearsOfTheKindlessReadBesideAsync(Neighbour(platform: "PlayStation 2"))).ShouldBe([BookYear, GameYear]);
    }

    [Fact]
    public async Task The_kindless_reads_own_line_counts_too()
    {
        var own = Read(title: Title);
        own["lines"] = new[] { Title, "PlayStation 2" };

        (await YearsOfTheKindlessReadBesideAsync(Neighbour(), own)).ShouldBe([GameYear, BookYear]);
    }
}
