using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.TestSupport.Evals;

namespace CollectionMaxxing.Api.Tests.Evals;

/// <summary>
/// The combined row (<c>apple_vision</c>) grounds a model-only place the way the app does: under one snap with Vision, so the
/// shelf's printed kind (named on the <c>vision_pipeline</c> row) can take a kind it denies off a kindless read before the row
/// searches. The <c>apple_model</c> row the phone uploaded is grounded on its own, unaffected by the print.
/// </summary>
public class EvalCombinationPrintLendingTests(ApiFixture api) : EvalTest(api)
{
    private const string Title = "Quiet Harbor Atlas";
    private const int BookYear = 2001;
    private const int FilmYear = 2002;

    /// <summary>A shelf of one title with two tied provider releases, a book and a film, the book answered first: only order and the print tell them apart.</summary>
    private void ArrangeShelf()
    {
        Golden.AddShelf("s1", [new SyntheticGolden.ShelfItem(Title, "film")]);
        Games.Results.Add(new ProviderRelease { Provider = "igdb", ExternalId = "b1", Title = Title, Kind = WorkKind.Book, Year = BookYear });
        Games.Results.Add(new ProviderRelease { Provider = "igdb", ExternalId = "f1", Title = Title, Kind = WorkKind.Film, Year = FilmYear });
    }

    private static object KindlessRead(string title) => new { title, text = title, kind = (string?)null };

    [Fact]
    public async Task A_model_only_place_is_grounded_with_the_print_in_the_combined_row_but_not_in_the_models_own_row()
    {
        ArrangeShelf();
        using var admin = AdminClient();

        var response = await PostRunOkAsync(admin, RunBody(
            PhotoResult("s1", "apple_model", [KindlessRead(Title)]),
            PhotoResult("s1", "vision_pipeline", [], printKind: "film")));

        var engines = response.GetProperty("summary").GetProperty("engines");
        engines.GetProperty("apple_model").GetProperty("shelf").GetProperty("top1Hits").GetInt32()
            .ShouldBe(0, "grounded without the print, the kindless read is left in the providers' order: a book, which does not match the shelf's film");
        engines.GetProperty("apple_vision").GetProperty("shelf").GetProperty("top1Hits").GetInt32()
            .ShouldBe(1, "the combined row grounds the model-only place with the print lent, as the app would, and finds the film");
    }
}
