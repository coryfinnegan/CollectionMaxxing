using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Scanning;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.TestSupport.Evals;

namespace CollectionMaxxing.Api.Tests.Evals;

/// <summary>
/// The OCR probes: extra reads of a shelf picture (another recognition level, language correction, tiles) that the phone sends with the Vision photo
/// so a later replay can try an OCR setting without the phone. The PC keeps them with the grounded run and never scores them.
/// </summary>
public class EvalOcrProbeTests(ApiFixture api) : OpenRunTest(api)
{
    private static JsonElement PhotoOf(JsonElement run) => run.GetProperty("photos")[0];

    private static object[] TwoProbes() =>
    [
        OcrProbe("production", 412, ProbeLine("Star Fox 64", pass: 0, confidence: 0.98, x: 0.2, y: 0.1, width: 0.05, height: 0.6), ProbeLine("xod xoF", pass: 180, confidence: 0.2)),
        OcrProbe("fast0", 61, ProbeLine("STAR FOX", pass: 0, confidence: 0.5)),
    ];

    [Fact]
    public async Task The_probes_sent_with_the_Vision_photo_are_kept_in_the_grounded_run_line_for_line()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        using var admin = AdminClient();

        await PostRunOkAsync(admin, RunBody(PhotoResult("c1", "vision_pipeline", [StarFoxRead()], ocrProbes: TwoProbes())));

        var probes = PhotoOf(await LatestRunFileAsync()).GetProperty("ocrProbes");
        probes.GetArrayLength().ShouldBe(2);
        probes[0].GetProperty("name").GetString().ShouldBe("production");
        probes[0].GetProperty("ms").GetInt64().ShouldBe(412);
        var lines = probes[0].GetProperty("lines");
        lines.GetArrayLength().ShouldBe(2);
        lines[0].GetProperty("text").GetString().ShouldBe("Star Fox 64");
        lines[0].GetProperty("confidence").GetDouble().ShouldBe(0.98);
        lines[0].GetProperty("pass").GetInt32().ShouldBe(0);
        lines[0].GetProperty("x").GetDouble().ShouldBe(0.2);
        lines[0].GetProperty("y").GetDouble().ShouldBe(0.1);
        lines[0].GetProperty("width").GetDouble().ShouldBe(0.05);
        lines[0].GetProperty("height").GetDouble().ShouldBe(0.6);
        lines[1].GetProperty("pass").GetInt32().ShouldBe(180);
        probes[1].GetProperty("name").GetString().ShouldBe("fast0");
        probes[1].GetProperty("lines")[0].GetProperty("text").GetString().ShouldBe("STAR FOX");
    }

    [Fact]
    public async Task The_probes_of_a_photo_sent_on_its_own_to_the_open_run_are_kept_too()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        using var admin = AdminClient();
        await OpenOkAsync(admin);

        await PutPhotoOkAsync(admin, "c1", PhotoResult("c1", "vision_pipeline", [StarFoxRead()], ocrProbes: TwoProbes()));
        await FinishOkAsync(admin);
        await StatusWhenDoneAsync(admin);

        var probes = PhotoOf(await LatestRunFileAsync()).GetProperty("ocrProbes");
        probes.EnumerateArray().Select(p => p.GetProperty("name").GetString()).ShouldBe(["production", "fast0"]);
        probes[0].GetProperty("lines")[1].GetProperty("pass").GetInt32().ShouldBe(180);
    }

    [Fact]
    public async Task A_probe_box_a_hair_outside_the_picture_is_pulled_in_like_an_OCR_line_is()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        using var admin = AdminClient();

        await PostRunOkAsync(admin, RunBody(PhotoResult(
            "c1", "vision_pipeline", [StarFoxRead()], ocrProbes: [OcrProbe("production", 1, ProbeLine("Star Fox 64", pass: 180, x: -0.000001, y: 1.005))])));

        var line = PhotoOf(await LatestRunFileAsync()).GetProperty("ocrProbes")[0].GetProperty("lines")[0];
        line.GetProperty("x").GetDouble().ShouldBe(0.0);
        line.GetProperty("y").GetDouble().ShouldBe(1.0);
    }

    [Fact]
    public async Task Probe_text_stays_out_of_the_dated_result_file_that_is_safe_to_commit()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        using var admin = AdminClient();

        await PostRunOkAsync(admin, RunBody(PhotoResult("c1", "vision_pipeline", [StarFoxRead()], ocrProbes: [OcrProbe("production", 1, ProbeLine("Zebra Quartz Lantern"))])));

        (await File.ReadAllTextAsync(Path.Combine(Golden.Results, "latest-run.json"))).ShouldContain("Zebra Quartz Lantern");
        foreach (var file in ResultFiles().Append(Path.Combine(Golden.Results, "latest.json")))
        {
            (await File.ReadAllTextAsync(file)).ShouldNotContain("Zebra Quartz Lantern");
        }
    }

    [Fact]
    public async Task A_run_scores_the_same_with_probes_as_without()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        using var admin = AdminClient();
        object[] Engines(object[]? probes) =>
        [
            PhotoResult("c1", "apple_model", [StarFoxRead()]),
            PhotoResult("c1", "fm_text", [StarFoxRead()]),
            PhotoResult("c1", "vision_pipeline", [StarFoxRead()], ocrLines: [OcrLine("Star Fox 64", 0.2, 0.1, 0.05, 0.6)], ocrPieces: [[0]], ocrProbes: probes),
        ];

        await PostRunOkAsync(admin, RunBody(Engines(null)));
        var without = (summary: await LatestSummaryAsync(), photos: (await LatestRunFileAsync()).GetProperty("photos"));
        await PostRunOkAsync(admin, RunBody(Engines(TwoProbes())));
        var with = (summary: await LatestSummaryAsync(), photos: (await LatestRunFileAsync()).GetProperty("photos"));

        with.summary.GetProperty("engines").GetRawText().ShouldBe(without.summary.GetProperty("engines").GetRawText());
        with.summary.GetProperty("scoredPhotos").GetInt32().ShouldBe(without.summary.GetProperty("scoredPhotos").GetInt32());
        for (var i = 0; i < without.photos.GetArrayLength(); i++)
        {
            with.photos[i].GetProperty("items").GetRawText().ShouldBe(without.photos[i].GetProperty("items").GetRawText());
        }
    }

    [Theory]
    [InlineData("apple_model")]
    [InlineData("fm_text")]
    public async Task Probes_sent_with_another_engines_photo_are_ignored(string engine)
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        using var admin = AdminClient();
        object[] tooMany = [.. Enumerable.Range(0, 9).Select(i => OcrProbe($"p{i}", -5, ProbeLine("Dune", pass: 45)))];

        await PostRunOkAsync(admin, RunBody(PhotoResult("c1", engine, [StarFoxRead()], ocrProbes: tooMany)));

        var photo = PhotoOf(await LatestRunFileAsync());
        (!photo.TryGetProperty("ocrProbes", out var kept) || kept.ValueKind == JsonValueKind.Null).ShouldBeTrue();
    }

    [Fact]
    public async Task A_Vision_photo_without_probes_is_accepted_as_before()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        using var admin = AdminClient();

        await PostRunOkAsync(admin, RunBody(PhotoResult("c1", "vision_pipeline", [StarFoxRead()])));

        var photo = PhotoOf(await LatestRunFileAsync());
        (!photo.TryGetProperty("ocrProbes", out var kept) || kept.ValueKind == JsonValueKind.Null).ShouldBeTrue();
    }

    [Theory]
    [InlineData("count")]
    [InlineData("lines")]
    [InlineData("long_text")]
    [InlineData("x_over_one")]
    [InlineData("confidence_over_one")]
    [InlineData("unknown_pass")]
    [InlineData("negative_time")]
    [InlineData("time_over_an_hour")]
    [InlineData("no_name")]
    public async Task A_probe_over_a_limit_is_refused_with_its_own_code_and_nothing_is_written(string problem)
    {
        Golden.AddSingle("c1", StarFoxTruth);
        using var admin = AdminClient();
        object[] probes = problem switch
        {
            "count" => [.. Enumerable.Range(0, 5).Select(i => OcrProbe($"p{i}", 1, ProbeLine("Dune")))],
            "lines" => [OcrProbe("production", 1, [.. Enumerable.Range(0, 1501).Select(i => ProbeLine($"Line {i}"))])],
            "long_text" => [OcrProbe("production", 1, ProbeLine(new string('t', 201)))],
            "x_over_one" => [OcrProbe("production", 1, ProbeLine("Dune", x: 1.5))],
            "confidence_over_one" => [OcrProbe("production", 1, ProbeLine("Dune", confidence: 1.5))],
            "unknown_pass" => [OcrProbe("production", 1, ProbeLine("Dune", pass: 45))],
            "negative_time" => [OcrProbe("production", -1, ProbeLine("Dune"))],
            "time_over_an_hour" => [OcrProbe("production", 3_600_001, ProbeLine("Dune"))],
            _ => [new { ms = 1, lines = new[] { ProbeLine("Dune") } }],
        };

        using var response = await PostRunAsync(admin, RunBody(PhotoResult("c1", "vision_pipeline", [], ocrProbes: probes)));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("evals.ocr_probe_invalid");
        SyntheticGolden.Files(Golden.Results).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_bad_probe_sent_to_the_open_run_is_refused_alone_and_the_run_stays_open()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        using var admin = AdminClient();
        await OpenOkAsync(admin);

        var response = await PutPhotoAsync(admin, "c1", PhotoResult("c1", "vision_pipeline", [StarFoxRead()], ocrProbes: [OcrProbe("production", 1, ProbeLine("Dune", pass: 45))]));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("evals.ocr_probe_invalid");
        Received(await StatusAsync(admin)).ShouldBeEmpty();
    }
}
