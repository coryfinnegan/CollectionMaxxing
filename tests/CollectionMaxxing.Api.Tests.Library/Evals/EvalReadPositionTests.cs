using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.TestSupport.Evals;

namespace CollectionMaxxing.Api.Tests.Evals;

/// <summary>
/// An eval read carries the box it sat in, as an <c>/identify</c> read does. The dry run keeps the same release read in two
/// places as two items and the same release read twice in one place as one, and the grounded run keeps each item's box so the
/// combined engine can tell one spine both engines read from two spines that read alike. Neutral titles throughout.
/// </summary>
public class EvalReadPositionTests(ApiFixture api) : EvalTest(api)
{
    private const string Title = "Quiet Harbor Atlas";

    private static Dictionary<string, object?> BookAt(object? box)
    {
        var read = new Dictionary<string, object?>
        {
            ["title"] = Title,
            ["text"] = Title,
            ["creator"] = "Mara Odell",
            ["kind"] = "book",
            ["box"] = box,
        };
        return read;
    }

    private static object Box(double x) => new { x, y = 0.1, width = 0.05, height = 0.8 };

    private void ArrangeShelfOfTwoCopies()
    {
        Golden.AddShelf("s1", [new SyntheticGolden.ShelfItem(Title, "book"), new SyntheticGolden.ShelfItem(Title, "book")]);
        Books.Results.Add(new ProviderRelease
        {
            Provider = "openlibrary",
            ExternalId = "OL1W",
            WorkKey = "OL1W",
            Title = Title,
            Kind = WorkKind.Book,
            Year = 2015,
            Creators = ["Mara Odell"],
        });
    }

    private async Task<JsonElement> LatestRunPhotoAsync()
    {
        var text = await File.ReadAllTextAsync(Path.Combine(Golden.Results, "latest-run.json"));
        return JsonDocument.Parse(text).RootElement.GetProperty("photos")[0].Clone();
    }

    [Fact]
    public async Task The_same_book_read_in_two_places_is_two_items_and_the_run_keeps_where_each_sat()
    {
        ArrangeShelfOfTwoCopies();
        using var admin = AdminClient();

        var response = await PostRunOkAsync(admin, RunBody(PhotoResult("s1", "apple_model", [BookAt(Box(0.10)), BookAt(Box(0.60))])));

        var items = (await LatestRunPhotoAsync()).GetProperty("items");
        items.GetArrayLength().ShouldBe(2);
        items[0].GetProperty("box").GetProperty("x").GetDouble().ShouldBe(0.10);
        items[1].GetProperty("box").GetProperty("x").GetDouble().ShouldBe(0.60);
        items[1].GetProperty("box").GetProperty("height").GetDouble().ShouldBe(0.8);
        response.GetProperty("summary").GetProperty("engines").GetProperty("apple_model").GetProperty("shelf").GetProperty("top1Pct").GetDouble()
            .ShouldBe(100, "two spines of one title are two truth items, and each was read");
    }

    [Fact]
    public async Task The_same_book_read_twice_in_one_place_is_one_item()
    {
        ArrangeShelfOfTwoCopies();
        using var admin = AdminClient();

        await PostRunOkAsync(admin, RunBody(PhotoResult("s1", "apple_model", [BookAt(Box(0.30)), BookAt(Box(0.31))])));

        (await LatestRunPhotoAsync()).GetProperty("items").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task A_read_with_no_box_is_a_repeat_of_the_same_book_as_it_always_was()
    {
        ArrangeShelfOfTwoCopies();
        using var admin = AdminClient();

        await PostRunOkAsync(admin, RunBody(PhotoResult("s1", "apple_model", [BookAt(Box(0.10)), BookAt(null)])));

        (await LatestRunPhotoAsync()).GetProperty("items").GetArrayLength().ShouldBe(1);
    }

    [Theory]
    [InlineData(1.5, 0.1, 0.05, 0.8)]
    [InlineData(0.1, -0.1, 0.05, 0.8)]
    [InlineData(0.1, 0.1, 0.0, 0.8)]
    [InlineData(0.9, 0.1, 0.2, 0.8)]
    public async Task A_box_out_of_range_never_refuses_the_run_and_the_read_is_kept_with_no_box(double x, double y, double width, double height)
    {
        ArrangeShelfOfTwoCopies();
        using var admin = AdminClient();

        // With no box the read is compared as it always was, so it is a repeat of the same book read elsewhere.
        await PostRunOkAsync(admin, RunBody(PhotoResult("s1", "apple_model", [BookAt(Box(0.10)), BookAt(new { x, y, width, height })])));

        var items = (await LatestRunPhotoAsync()).GetProperty("items");
        items.GetArrayLength().ShouldBe(1);
        items[0].GetProperty("box").GetProperty("x").GetDouble().ShouldBe(0.10);
    }

    [Fact]
    public async Task A_lone_unusable_box_leaves_the_read_with_no_box()
    {
        ArrangeShelfOfTwoCopies();
        using var admin = AdminClient();

        await PostRunOkAsync(admin, RunBody(PhotoResult("s1", "apple_model", [BookAt(new { x = 0.1, y = 0.1, width = 0.05 })])));

        var item = (await LatestRunPhotoAsync()).GetProperty("items")[0];
        (item.TryGetProperty("box", out var box) && box.ValueKind != JsonValueKind.Null).ShouldBeFalse();
    }

    [Theory]
    [InlineData("left")]
    [InlineData(5)]
    public async Task A_box_that_is_not_an_object_or_has_text_for_a_number_never_refuses_the_run(object box)
    {
        ArrangeShelfOfTwoCopies();
        using var admin = AdminClient();

        await PostRunOkAsync(admin, RunBody(PhotoResult("s1", "apple_model", [BookAt(box), BookAt(new { x = "left", y = 0.1, width = 0.05, height = 0.8 })])));

        (await LatestRunPhotoAsync()).GetProperty("items").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task A_box_a_hair_outside_the_photo_is_moved_into_it_and_kept()
    {
        ArrangeShelfOfTwoCopies();
        using var admin = AdminClient();

        await PostRunOkAsync(admin, RunBody(PhotoResult("s1", "apple_model", [BookAt(new { x = 0.95, y = 0.1, width = 0.0505, height = 0.8 })])));

        var box = (await LatestRunPhotoAsync()).GetProperty("items")[0].GetProperty("box");
        box.GetProperty("x").GetDouble().ShouldBe(0.95);
        box.GetProperty("width").GetDouble().ShouldBe(0.05, 1e-9);
    }

    [Fact]
    public async Task A_run_whose_reads_carry_no_box_is_accepted_as_before()
    {
        ArrangeShelfOfTwoCopies();
        using var admin = AdminClient();

        await PostRunOkAsync(admin, RunBody(PhotoResult("s1", "apple_model", [BookAt(null)])));

        var item = (await LatestRunPhotoAsync()).GetProperty("items")[0];
        (item.TryGetProperty("box", out var box) && box.ValueKind != JsonValueKind.Null).ShouldBeFalse();
    }
}
