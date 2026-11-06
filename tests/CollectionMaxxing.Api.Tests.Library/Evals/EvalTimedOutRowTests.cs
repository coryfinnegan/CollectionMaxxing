using System.Text.Json;
using CollectionMaxxing.Api.Tests.Scanning;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.TestSupport.Evals;

namespace CollectionMaxxing.Api.Tests.Evals;

/// <summary>
/// An <c>ok</c> model row that ran out of time but kept its items arrives with <c>timedOut: true</c>: the run counts it as a timeout and still
/// scores its items. The flag is refused on any other engine, and a row from an older build, without it, scores as before.
/// </summary>
public class EvalTimedOutRowTests(ApiFixture api) : EvalTest(api)
{
    private static readonly SyntheticGolden.SingleItem StarFoxTruth =
        new("Star Fox 64", "game_cartridge", "cart_only", "loose_cartridge", "Nintendo 64");

    private static object StarFoxRead() => ReadItem("Star Fox 64", "game", "game_cartridge", "Nintendo 64", "cart_only", resolvedBy: "model");

    /// <summary>The photo result as the phone uploads it, with the <c>timedOut</c> flag when it has one.</summary>
    private static Dictionary<string, object?> Row(string engine, bool? timedOut, string photoId = "c1")
    {
        var row = new Dictionary<string, object?>();
        foreach (var property in JsonSerializer.SerializeToElement(PhotoResult(photoId, engine, [StarFoxRead()], latencyMs: 9000)).EnumerateObject())
        {
            row[property.Name] = property.Value;
        }

        if (timedOut is { } flag)
        {
            row["timedOut"] = flag;
        }

        return row;
    }

    private async Task<JsonElement> SubmitAsync(Dictionary<string, object?> row)
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        using var admin = AdminClient();
        return await PostRunOkAsync(admin, RunBody(row));
    }

    private static JsonElement SingleOf(JsonElement response, string engine) =>
        response.GetProperty("summary").GetProperty("engines").GetProperty(engine).GetProperty("single");

    [Theory]
    [InlineData("apple_model")]
    [InlineData("fm_text")]
    public async Task An_ok_model_row_that_timed_out_counts_as_a_timeout_and_still_scores_its_items(string engine)
    {
        var response = await SubmitAsync(Row(engine, timedOut: true));

        var single = SingleOf(response, engine);
        single.GetProperty("timeouts").GetInt32().ShouldBe(1);
        single.GetProperty("timeoutPct").GetDouble().ShouldBe(100);
        single.GetProperty("top1Pct").GetDouble().ShouldBe(100);
        response.GetProperty("summary").GetProperty("scoredPhotos").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task A_model_row_from_an_older_build_without_the_flag_scores_as_before()
    {
        var response = await SubmitAsync(Row("apple_model", timedOut: null));

        var single = SingleOf(response, "apple_model");
        single.GetProperty("timeouts").GetInt32().ShouldBe(0);
        single.GetProperty("top1Pct").GetDouble().ShouldBe(100);
    }

    [Fact]
    public async Task A_Vision_row_that_claims_it_timed_out_is_refused_for_that_photo_and_the_rest_of_the_run_is_scored()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Golden.AddSingle("c2", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        using var admin = AdminClient();

        var response = await PostRunOkAsync(admin, RunBody(Row("apple_model", timedOut: null), Row("vision_pipeline", timedOut: true, photoId: "c2")));

        response.GetProperty("summary").GetProperty("scoredPhotos").GetInt32().ShouldBe(1);
        var skipped = response.GetProperty("summary").GetProperty("skipped").EnumerateArray().ShouldHaveSingleItem();
        skipped.GetProperty("photoId").GetString().ShouldBe("c2");
        skipped.GetProperty("reason").GetString().ShouldBe("evals.timed_out_invalid");
    }
}
