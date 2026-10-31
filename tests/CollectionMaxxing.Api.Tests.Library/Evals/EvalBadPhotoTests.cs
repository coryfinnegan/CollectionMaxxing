using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Scanning;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.TestSupport.Evals;

namespace CollectionMaxxing.Api.Tests.Evals;

/// <summary>
/// A run is the phone's one chance to have read the golden photos, so one bad photo never throws it away (a 46-book shelf made the first
/// real Vision run answer 400 every time, and the phone could not repeat it). A photo that fails its own checks is skipped and listed with
/// its problem code; what can be cleaned is cleaned.
/// </summary>
public class EvalBadPhotoTests(ApiFixture api) : EvalTest(api)
{
    private static readonly SyntheticGolden.SingleItem StarFoxTruth =
        new("Star Fox 64", "game_cartridge", "cart_only", "loose_cartridge", "Nintendo 64");

    private static object StarFoxRead() => ReadItem("Star Fox 64", "game", "game_cartridge", "Nintendo 64", "cart_only");

    private static object[] Shelf(int count) => [.. Enumerable.Range(0, count).Select(i => ReadItem($"Zorblax Quest {i}", "game", "game_cartridge", "Nintendo 64", "cart_only"))];

    private async Task<JsonElement> LatestRunFileAsync()
    {
        var text = await File.ReadAllTextAsync(Path.Combine(Golden.Results, "latest-run.json"));
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static IEnumerable<string> Skipped(JsonElement response) =>
        response.GetProperty("summary").GetProperty("skipped").EnumerateArray()
            .Select(s => $"{s.GetProperty("photoId").GetString()}:{s.GetProperty("reason").GetString()}");

    [Fact]
    public async Task A_Vision_shelf_of_46_books_is_accepted_and_scored()
    {
        Golden.AddShelf("s1", [new("Star Fox 64", "game_cartridge", "Nintendo 64")]);
        using var admin = AdminClient();

        var response = await PostRunOkAsync(admin, RunBody(PhotoResult("s1", "vision_pipeline", Shelf(46))));

        response.GetProperty("summary").GetProperty("scoredPhotos").GetInt32().ShouldBe(1);
        Skipped(response).ShouldBeEmpty();
        (await LatestRunFileAsync()).GetProperty("photos")[0].GetProperty("items").GetArrayLength().ShouldBe(46);
    }

    [Theory]
    [InlineData(-0.000001, 0.0)]
    [InlineData(1.005, 1.0)]
    public async Task An_OCR_box_a_hair_outside_0_to_1_is_clamped_and_the_run_is_accepted(double sent, double kept)
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        using var admin = AdminClient();

        var response = await PostRunOkAsync(admin, RunBody(PhotoResult(
            "c1", "vision_pipeline", [StarFoxRead()], ocrLines: [OcrLine("Star Fox 64", x: sent, y: sent, width: sent, height: sent), OcrLine("Nintendo")], ocrPieces: [[0, 1]])));

        response.GetProperty("summary").GetProperty("scoredPhotos").GetInt32().ShouldBe(1);
        Skipped(response).ShouldBeEmpty();
        var lines = (await LatestRunFileAsync()).GetProperty("photos")[0].GetProperty("ocrLines");
        lines.GetArrayLength().ShouldBe(2);
        foreach (var name in new[] { "x", "y", "width", "height" })
        {
            lines[0].GetProperty(name).GetDouble().ShouldBe(kept);
        }
    }

    [Fact]
    public async Task An_invalid_product_code_is_dropped_and_the_item_is_still_scored()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        using var admin = AdminClient();
        var read = new { title = "Star Fox 64", text = "Star Fox 64", kind = "game", format = "game_cartridge", platform = "Nintendo 64", completeness = "cart_only", productCode = "!!! not a product code !!!" };

        var response = await PostRunOkAsync(admin, RunBody(PhotoResult("c1", "apple_model", [read])));

        response.GetProperty("summary").GetProperty("scoredPhotos").GetInt32().ShouldBe(1);
        Skipped(response).ShouldBeEmpty();
        (await LatestRunFileAsync()).GetProperty("photos")[0].GetProperty("items")[0].GetProperty("verdict").GetString().ShouldBe("matched");
    }

    [Fact]
    public async Task A_photo_that_fails_a_hard_check_is_skipped_with_its_code_logged_and_the_rest_of_the_run_is_scored()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Golden.AddSingle("c2", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        using var admin = AdminClient();
        var badTier = new { title = "Star Fox 64", text = "Star Fox 64", kind = "game", format = "game_cartridge", platform = "Nintendo 64", completeness = "cart_only", tier = 7 };

        var response = await PostRunOkAsync(admin, RunBody(
            PhotoResult("c2", "apple_model", [badTier]),
            PhotoResult("c1", "apple_model", [StarFoxRead()])));

        response.GetProperty("summary").GetProperty("scoredPhotos").GetInt32().ShouldBe(1);
        Skipped(response).ShouldBe(["c2:evals.tier_invalid"]);
        (await LatestRunFileAsync()).GetProperty("photos").EnumerateArray().Select(p => p.GetProperty("photoId").GetString()).ShouldBe(["c1"]);
        Logs.Lines.ShouldContain(line => line.Contains("c2", StringComparison.Ordinal)
            && line.Contains("apple_model", StringComparison.Ordinal)
            && line.Contains("evals.tier_invalid", StringComparison.Ordinal));
        (await File.ReadAllTextAsync(Path.Combine(Golden.Results, "report.md"))).ShouldContain("`c2`: evals.tier_invalid");
    }

    [Fact]
    public async Task Items_past_the_limit_are_cut_to_the_first_120_and_the_report_says_how_many_were_cut()
    {
        Golden.AddShelf("s1", [new("Star Fox 64", "game_cartridge", "Nintendo 64")]);
        using var admin = AdminClient();

        var response = await PostRunOkAsync(admin, RunBody(PhotoResult("s1", "vision_pipeline", Shelf(130))));

        response.GetProperty("summary").GetProperty("scoredPhotos").GetInt32().ShouldBe(1);
        Skipped(response).ShouldBe(["s1:trimmed_items_120_of_130"]);
        var items = (await LatestRunFileAsync()).GetProperty("photos")[0].GetProperty("items");
        items.GetArrayLength().ShouldBe(120);
        items[0].GetProperty("readTitle").GetString().ShouldBe("Zorblax Quest 0");
        items[119].GetProperty("readTitle").GetString().ShouldBe("Zorblax Quest 119");
        (await File.ReadAllTextAsync(Path.Combine(Golden.Results, "report.md"))).ShouldContain("`s1`: trimmed_items_120_of_130");
    }

    [Fact]
    public async Task A_run_in_which_every_photo_fails_is_refused_with_the_first_problem_logged_and_nothing_written()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        using var admin = AdminClient();
        var badTier = new { title = "Star Fox 64", text = "Star Fox 64", kind = "game", tier = 7 };

        using var response = await PostRunAsync(admin, RunBody(PhotoResult("c1", "apple_model", [badTier])));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("evals.tier_invalid");
        SyntheticGolden.Files(Golden.Results).ShouldBeEmpty();
        Logs.Lines.ShouldContain(line => line.Contains("Eval run refused", StringComparison.Ordinal) && line.Contains("evals.tier_invalid", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_photo_sent_twice_for_one_engine_still_refuses_the_run()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        using var admin = AdminClient();

        using var response = await PostRunAsync(admin, RunBody(PhotoResult("c1", "apple_model", []), PhotoResult("c1", "apple_model", [])));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("evals.duplicate_result");
        Logs.Lines.ShouldContain(line => line.Contains("Eval run refused", StringComparison.Ordinal) && line.Contains("evals.duplicate_result", StringComparison.Ordinal));
    }
}
