using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.TestSupport.Evals;

namespace CollectionMaxxing.Api.Tests.Evals;

/// <summary>
/// A wrong format on a single cover could not be explained, because nothing recorded what the phone's cover cue saw. The phone sends a short
/// line about it with each photo's result (<c>coverCue</c>, optional); the PC keeps it with the grounded run and shows it beside the picture's
/// miss in the report. It is for the eval record only, so a bad one is cleaned and cut, never a reason to refuse the run.
/// </summary>
public class EvalCoverCueTests(ApiFixture api) : EvalTest(api)
{
    private const string Cue = "cue=ready fmt=shape:dvd r=0.71 cov=0.61 rects=2 (1 frame-hugging) kind=film code=none";

    private static readonly SyntheticGolden.SingleItem StarFoxTruth =
        new("Star Fox 64", "game_cartridge", "cart_only", "loose_cartridge", "Nintendo 64");

    /// <summary>A photo's result as the phone uploads it, with the cover cue it noted (or none).</summary>
    private static Dictionary<string, object?> Photo(string photoId, string engine, object[] items, string? coverCue, bool withCue = true)
    {
        var result = new Dictionary<string, object?>
        {
            ["photoId"] = photoId,
            ["engine"] = engine,
            ["outcome"] = "ok",
            ["items"] = items,
            ["unreadableCount"] = 0,
            ["latencyMs"] = 1500,
            ["firstItemMs"] = 800,
            ["thermalState"] = "nominal",
        };
        if (withCue)
        {
            result["coverCue"] = coverCue;
        }

        return result;
    }

    private static object StarFoxRead() => ReadItem("Star Fox 64", "game", "game_cartridge", "Nintendo 64", "cart_only");

    private static object WrongRead() => ReadItem("Mario Kart 64", "game", "game_cartridge", "Nintendo 64", "cart_only");

    private async Task<JsonElement> LatestPhotoAsync()
    {
        var text = await File.ReadAllTextAsync(Path.Combine(Golden.Results, "latest-run.json"));
        return JsonDocument.Parse(text).RootElement.GetProperty("photos")[0].Clone();
    }

    private static bool Absent(JsonElement photo, string name) => !photo.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null;

    [Theory]
    [InlineData("apple_model")]
    [InlineData("fm_text")]
    [InlineData("vision_pipeline")]
    public async Task A_cover_cue_sent_with_a_photo_is_kept_with_the_grounded_run(string engine)
    {
        Golden.AddSingle("c1", StarFoxTruth);
        using var admin = AdminClient();

        await PostRunOkAsync(admin, RunBody(Photo("c1", engine, [StarFoxRead()], Cue)));

        (await LatestPhotoAsync()).GetProperty("coverCue").GetString().ShouldBe(Cue);
    }

    [Fact]
    public async Task A_cover_cue_over_the_limit_is_cut_to_it_and_the_run_is_accepted()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        using var admin = AdminClient();

        var response = await PostRunAsync(admin, RunBody(Photo("c1", "apple_model", [StarFoxRead()], new string('x', 500))));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await LatestPhotoAsync()).GetProperty("coverCue").GetString().ShouldBe(new string('x', 300));
    }

    [Fact]
    public async Task A_photo_without_a_cover_cue_is_accepted_as_before_and_keeps_none()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        using var admin = AdminClient();

        await PostRunOkAsync(admin, RunBody(Photo("c1", "apple_model", [StarFoxRead()], null, withCue: false)));

        Absent(await LatestPhotoAsync(), "coverCue").ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_cover_cue_is_not_kept(string blank)
    {
        Golden.AddSingle("c1", StarFoxTruth);
        using var admin = AdminClient();

        await PostRunOkAsync(admin, RunBody(Photo("c1", "apple_model", [StarFoxRead()], blank)));

        Absent(await LatestPhotoAsync(), "coverCue").ShouldBeTrue();
    }

    [Fact]
    public async Task Line_breaks_in_a_cover_cue_become_spaces_so_the_report_keeps_one_line()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        using var admin = AdminClient();

        await PostRunOkAsync(admin, RunBody(Photo("c1", "apple_model", [StarFoxRead()], "cue=ready\nfmt=none")));

        (await LatestPhotoAsync()).GetProperty("coverCue").GetString().ShouldBe("cue=ready fmt=none");
    }

    [Fact]
    public async Task The_report_shows_the_cover_cue_beside_the_pictures_miss_line()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        using var admin = AdminClient();

        await PostRunOkAsync(admin, RunBody(Photo("c1", "apple_model", [WrongRead()], Cue)));

        var report = await File.ReadAllTextAsync(Path.Combine(Golden.Results, "report.md"));
        var miss = report.Split('\n').First(line => line.StartsWith("- `c1` (single, miss)", StringComparison.Ordinal)).TrimEnd('\r');
        miss.ShouldEndWith($", cover cue `{Cue}`");
    }

    [Fact]
    public async Task The_cover_cue_stays_out_of_the_dated_result_file_that_is_safe_to_commit()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        using var admin = AdminClient();

        await PostRunOkAsync(admin, RunBody(Photo("c1", "apple_model", [StarFoxRead()], Cue)));

        (await File.ReadAllTextAsync(Path.Combine(Golden.Results, "latest-run.json"))).ShouldContain("shape:dvd");
        foreach (var file in ResultFiles().Append(Path.Combine(Golden.Results, "latest.json")))
        {
            (await File.ReadAllTextAsync(file)).ShouldNotContain("shape:dvd");
        }
    }
}
