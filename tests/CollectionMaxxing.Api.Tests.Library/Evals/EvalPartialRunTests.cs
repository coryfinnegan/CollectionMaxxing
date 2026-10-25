using System.Net;
using CollectionMaxxing.Api.Tests.Scanning;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.TestSupport.Evals;

namespace CollectionMaxxing.Api.Tests.Evals;

/// <summary>A cancelled run is uploaded with <c>partial: true</c> (EVAL-1b): it is kept as partial and never counts as a pass or a baseline.</summary>
public class EvalPartialRunTests(ApiFixture api) : EvalTest(api)
{
    private static readonly SyntheticGolden.SingleItem StarFoxTruth =
        new("Star Fox 64", "game_cartridge", "cart_only", "loose_cartridge", "Nintendo 64");

    private void ArrangeThreePhotos()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Golden.AddSingle("c2", StarFoxTruth);
        Golden.AddSingle("c3", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
    }

    private static Dictionary<string, object?> PartialBody(bool? partial, params object[] results)
    {
        var body = RunBody(results);
        if (partial is { } flag)
        {
            body["partial"] = flag;
        }

        return body;
    }

    private static object StarFoxRead() => ReadItem("Star Fox 64", "game", "game_cartridge", "Nintendo 64", "cart_only", resolvedBy: "model");

    [Fact]
    public async Task A_cancelled_run_is_stored_as_partial_shown_as_N_of_M_photos_and_the_gate_says_incomplete()
    {
        ArrangeThreePhotos();
        using var admin = AdminClient();

        var response = await PostRunOkAsync(admin, PartialBody(true, PhotoResult("c1", "apple_model", [StarFoxRead()])));

        var summary = response.GetProperty("summary");
        summary.GetProperty("partial").GetBoolean().ShouldBeTrue();
        summary.GetProperty("photosRun").GetInt32().ShouldBe(1);
        summary.GetProperty("photosInSet").GetInt32().ShouldBe(3);
        summary.GetProperty("gate").GetProperty("verdict").GetString().ShouldBe("incomplete");
        (await File.ReadAllTextAsync(Path.Combine(Golden.Results, "report.md"))).ShouldContain("Partial run: 1 of 3 photos");
        System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Golden.Results, "latest-run.json")))
            .RootElement.GetProperty("partial").GetBoolean().ShouldBeTrue();
        var latest = await ReadJsonAsync(await admin.GetAsync($"{Base}/runs/latest"));
        latest.GetProperty("partial").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_partial_run_is_not_compared_with_the_baseline()
    {
        ArrangeThreePhotos();
        var baseline = Path.Combine(Path.GetTempPath(), "cm-baseline-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(baseline, """{ "metrics": { "apple_model.single.top1": 100 } }""");
        Settings["Evals:BaselinePath"] = baseline;
        using var admin = AdminClient();

        try
        {
            var partial = await PostRunOkAsync(admin, PartialBody(true, PhotoResult("c1", "apple_model", [ReadItem("Something Else Entirely")])));
            var whole = await PostRunOkAsync(admin, PartialBody(null, PhotoResult("c1", "apple_model", [ReadItem("Something Else Entirely")])));

            partial.GetProperty("summary").GetProperty("regression").GetProperty("status").GetString().ShouldBe("partial_run");
            whole.GetProperty("summary").GetProperty("regression").GetProperty("status").GetString().ShouldBe("regressed");
        }
        finally
        {
            File.Delete(baseline);
        }
    }

    [Fact]
    public async Task A_run_without_the_flag_is_not_partial_and_explicit_false_is_the_same()
    {
        ArrangeThreePhotos();
        using var admin = AdminClient();

        var plain = await PostRunOkAsync(admin, PartialBody(null, PhotoResult("c1", "apple_model", [StarFoxRead()])));
        var explicitFalse = await PostRunOkAsync(admin, PartialBody(false, PhotoResult("c1", "apple_model", [StarFoxRead()])));

        plain.GetProperty("summary").GetProperty("partial").GetBoolean().ShouldBeFalse();
        explicitFalse.GetProperty("summary").GetProperty("partial").GetBoolean().ShouldBeFalse();
        (await File.ReadAllTextAsync(Path.Combine(Golden.Results, "report.md"))).ShouldNotContain("Partial run");
    }

    [Fact]
    public async Task An_unknown_member_in_the_run_is_still_refused()
    {
        ArrangeThreePhotos();
        using var admin = AdminClient();
        var body = PartialBody(true, PhotoResult("c1", "apple_model", [StarFoxRead()]));
        body["parital"] = true; // a typo is not the flag

        var response = await PostRunAsync(admin, body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        SyntheticGolden.Files(Golden.Results).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_flag_must_be_a_boolean()
    {
        ArrangeThreePhotos();
        using var admin = AdminClient();
        var body = RunBody(PhotoResult("c1", "apple_model", [StarFoxRead()]));
        body["partial"] = "yes";

        (await PostRunAsync(admin, body)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
