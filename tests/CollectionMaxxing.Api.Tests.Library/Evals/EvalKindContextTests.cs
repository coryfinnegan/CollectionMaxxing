using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.TestSupport.Evals;

namespace CollectionMaxxing.Api.Tests.Evals;

/// <summary>
/// The eval run orders a photo's kindless reads by the kind the rest of the photo's reads agreed on, with the same rule
/// <c>/identify</c> uses: at least three reads naming a kind and at least 70% of them naming the same one. Ordering only; the
/// verdict and the scores are the scorer's. Neutral titles throughout.
/// </summary>
public class EvalKindContextTests(ApiFixture api) : EvalTest(api)
{
    private const string Title = "Quiet Harbor Atlas";
    private const int BookYear = 2001;
    private const int FilmYear = 2002;

    /// <summary>The title as a book and as a film, the book first as the providers answered them: only the order tells the two apart.</summary>
    private void ArrangeTiedBookAndFilm(params string[] shelfTitles)
    {
        Golden.AddShelf("s1", [.. shelfTitles.Select(t => new SyntheticGolden.ShelfItem(t, "film")), new SyntheticGolden.ShelfItem(Title, "film")]);
        Games.Results.Add(new ProviderRelease { Provider = "igdb", ExternalId = "b1", Title = Title, Kind = WorkKind.Book, Year = BookYear });
        Games.Results.Add(new ProviderRelease { Provider = "igdb", ExternalId = "f1", Title = Title, Kind = WorkKind.Film, Year = FilmYear });
    }

    private static object ReadOf(string title, string? kind) => new { title, text = title, kind };

    private static string[] Titles(int count, string stem) => [.. Enumerable.Range(1, count).Select(i => $"{stem} {i}")];

    private async Task<JsonElement> RunAsync(params object[] reads)
    {
        using var admin = AdminClient();
        await PostRunOkAsync(admin, RunBody(PhotoResult("s1", "apple_model", reads)));
        var text = await File.ReadAllTextAsync(Path.Combine(Golden.Results, "latest-run.json"));
        return JsonDocument.Parse(text).RootElement.GetProperty("photos")[0].GetProperty("items").Clone();
    }

    private static List<int> Years(JsonElement item) =>
        [.. item.GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("year").GetInt32())];

    [Fact]
    public async Task A_kindless_read_among_reads_of_films_is_grounded_with_the_film_first()
    {
        var shelf = Titles(4, "Lantern Row Part");
        ArrangeTiedBookAndFilm(shelf);

        var items = await RunAsync([.. shelf.Select(t => ReadOf(t, "film")), ReadOf(Title, null)]);

        var kindless = items[4];
        kindless.GetProperty("verdict").GetString().ShouldBe("ambiguous");
        Years(kindless).ShouldBe([FilmYear, BookYear]);
        kindless.GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("score").GetDouble()).ShouldBe([1.0, 1.0]);
    }

    [Fact]
    public async Task Without_a_clear_majority_the_providers_order_stands()
    {
        var shelf = Titles(2, "Lantern Row Part");
        ArrangeTiedBookAndFilm(shelf);

        var items = await RunAsync(
            [.. shelf.Select(t => ReadOf(t, "film")), ReadOf("Orchard Gate Volume 1", "book"), ReadOf("Orchard Gate Volume 2", "book"), ReadOf(Title, null)]);

        Years(items[4]).ShouldBe([BookYear, FilmYear]);
    }

    [Fact]
    public async Task A_read_with_its_own_kind_keeps_it_among_reads_of_films()
    {
        var shelf = Titles(4, "Lantern Row Part");
        ArrangeTiedBookAndFilm(shelf);

        var items = await RunAsync([.. shelf.Select(t => ReadOf(t, "film")), ReadOf(Title, "book")]);

        Years(items[4])[0].ShouldBe(BookYear);
    }
}
