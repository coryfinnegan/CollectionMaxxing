using System.Net;
using System.Text;
using CollectionMaxxing.Api.Tests.Scanning;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.TestSupport.Evals;

namespace CollectionMaxxing.Api.Tests.Evals;

/// <summary>
/// The phone sends one photo at a time: its three engine results in one small request. The request is answered as soon as they are safely
/// stored, with no grounding in it; sending a photo again replaces it; a bad photo is refused alone and never takes the run with it.
/// </summary>
public class EvalOpenRunPhotoTests(ApiFixture api) : OpenRunTest(api)
{
    [Fact]
    public async Task A_stored_photo_is_answered_while_the_providers_are_still_busy_because_nothing_is_grounded_in_the_request()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Games.Behaviour = async cancellationToken =>
        {
            asked.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        };
        using var admin = AdminClient();
        await OpenOkAsync(admin);

        var stored = await PutPhotoOkAsync(admin, "c1", AllEngines("c1", StarFoxRead)).WaitAsync(Bounded);

        stored.GetProperty("photoId").GetString().ShouldBe("c1");
        stored.GetProperty("received").GetInt32().ShouldBe(1);
        stored.GetProperty("total").GetInt32().ShouldBe(1);
        await asked.Task.WaitAsync(Bounded); // the PC did go on to ground it, in the background, and is still waiting on the provider
        Received(await StatusAsync(admin)).ShouldBe(["c1"]);
        release.SetResult();
    }

    [Fact]
    public async Task Sending_the_same_photo_again_replaces_it_so_a_retry_after_a_lost_answer_is_harmless()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        Games.Results.Add(ScanData.Game("The Legend of Zelda", "Nintendo 64", 1998, "g2"));
        using var admin = AdminClient();
        await OpenOkAsync(admin);

        await PutPhotoOkAsync(admin, "c1", AllEngines("c1", StarFoxRead));
        await PutPhotoOkAsync(admin, "c1", AllEngines("c1", ZeldaRead));
        await PutPhotoOkAsync(admin, "c1", AllEngines("c1", ZeldaRead)); // the same answer twice
        Received(await StatusAsync(admin)).ShouldBe(["c1"]);
        await FinishOkAsync(admin);
        await StatusWhenDoneAsync(admin);

        var photos = (await LatestRunFileAsync()).GetProperty("photos");
        photos.GetArrayLength().ShouldBe(3, "one row per engine, not one per send");
        foreach (var photo in photos.EnumerateArray())
        {
            photo.GetProperty("items")[0].GetProperty("readTitle").GetString().ShouldBe("The Legend of Zelda");
        }
    }

    [Fact]
    public async Task A_photo_that_is_not_in_the_run_is_refused_alone_and_the_run_goes_on()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        using var admin = AdminClient();
        await OpenOkAsync(admin);

        var refused = await PutPhotoAsync(admin, "not-in-the-set", PhotoResult("not-in-the-set", "apple_model", [StarFoxRead()]));
        var accepted = await PutPhotoAsync(admin, "c1", AllEngines("c1", StarFoxRead));

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(refused)).GetProperty("code").GetString().ShouldBe("evals.photo_not_in_run");
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        Received(await StatusAsync(admin)).ShouldBe(["c1"]);
    }

    [Fact]
    public async Task A_photo_that_fails_its_own_checks_is_refused_alone_listed_with_its_code_and_left_out_of_the_score()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Golden.AddSingle("c2", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        using var admin = AdminClient();
        await OpenOkAsync(admin);
        var noThermal = new { photoId = "c1", engine = "apple_model", outcome = "ok", items = new[] { StarFoxRead() }, latencyMs = 100 };

        var refused = await PutPhotoAsync(admin, "c1", noThermal);
        await PutPhotoOkAsync(admin, "c2", AllEngines("c2", StarFoxRead));

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(refused)).GetProperty("code").GetString().ShouldBe("evals.thermal_required");
        var status = await StatusAsync(admin);
        Received(status).ShouldBe(["c2"]);
        status.GetProperty("refused").EnumerateArray().Select(r => $"{r.GetProperty("photoId").GetString()}:{r.GetProperty("reason").GetString()}")
            .ShouldBe(["c1:evals.thermal_required"], "the phone is told so it does not read the photo again");

        await FinishOkAsync(admin);
        var done = await StatusWhenDoneAsync(admin);
        var summary = done.GetProperty("summary");
        summary.GetProperty("scoredPhotos").GetInt32().ShouldBe(3, "one row for each of the three engines");
        Skipped(summary).ShouldContain("c1:evals.thermal_required");
        summary.GetProperty("partial").GetBoolean().ShouldBeFalse("a refused photo is accounted for: the run covered every photo");
    }

    [Fact]
    public async Task One_bad_engine_result_is_skipped_and_the_photos_other_results_are_still_scored()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        using var admin = AdminClient();
        await OpenOkAsync(admin);
        var noThermal = new { photoId = "c1", engine = "fm_text", outcome = "ok", items = new[] { StarFoxRead() }, latencyMs = 100 };

        await PutPhotoOkAsync(admin, "c1", PhotoResult("c1", "apple_model", [StarFoxRead()]), noThermal, PhotoResult("c1", "vision_pipeline", [StarFoxRead()]));
        await FinishOkAsync(admin);
        var summary = (await StatusWhenDoneAsync(admin)).GetProperty("summary");

        Skipped(summary).ShouldBe(["c1:evals.thermal_required"]);
        var engines = (await LatestRunFileAsync()).GetProperty("photos").EnumerateArray().Select(p => p.GetProperty("engine").GetString()).ToList();
        engines.ShouldBe(["apple_model", "vision_pipeline"]);
    }

    [Fact]
    public async Task A_photo_with_one_engine_twice_is_refused()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        using var admin = AdminClient();
        await OpenOkAsync(admin);

        var response = await PutPhotoAsync(admin, "c1", PhotoResult("c1", "apple_model", [StarFoxRead()]), PhotoResult("c1", "apple_model", [StarFoxRead()]));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("evals.duplicate_result");
        Received(await StatusAsync(admin)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_result_that_names_another_photo_than_the_route_is_refused()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Golden.AddSingle("c2", StarFoxTruth);
        using var admin = AdminClient();
        await OpenOkAsync(admin);

        var response = await PutPhotoAsync(admin, "c1", PhotoResult("c2", "apple_model", [StarFoxRead()]));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("evals.photo_id_mismatch");
    }

    [Fact]
    public async Task The_OCR_clamps_of_the_one_shot_route_apply_to_a_photo_sent_on_its_own()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        using var admin = AdminClient();
        await OpenOkAsync(admin);

        await PutPhotoOkAsync(admin, "c1", PhotoResult(
            "c1", "vision_pipeline", [StarFoxRead()], ocrLines: [OcrLine("Star Fox 64", x: -0.000001, y: 1.005), OcrLine("Nintendo")], ocrPieces: [[0, 1]]));
        await FinishOkAsync(admin);
        await StatusWhenDoneAsync(admin);

        var lines = (await LatestRunFileAsync()).GetProperty("photos")[0].GetProperty("ocrLines");
        lines[0].GetProperty("x").GetDouble().ShouldBe(0.0);
        lines[0].GetProperty("y").GetDouble().ShouldBe(1.0);
    }

    [Fact]
    public async Task A_photo_cannot_be_sent_once_the_run_is_being_finished()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Golden.AddSingle("c2", StarFoxTruth);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Games.Behaviour = async cancellationToken => await held.Task.WaitAsync(cancellationToken);
        using var admin = AdminClient();
        await OpenOkAsync(admin);
        await PutPhotoOkAsync(admin, "c1", AllEngines("c1", StarFoxRead));
        await FinishOkAsync(admin, partial: true);

        var response = await PutPhotoAsync(admin, "c2", AllEngines("c2", StarFoxRead));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("evals.run_not_open");
        held.SetResult();
    }

    [Fact]
    public async Task Sending_a_photo_when_no_run_is_open_is_a_404()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        using var admin = AdminClient();

        var response = await PutPhotoAsync(admin, "c1", AllEngines("c1", StarFoxRead));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("evals.no_open_run");
    }

    [Fact]
    public async Task A_photo_body_that_is_not_json_is_a_415_and_one_over_the_limit_is_a_413()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        using var admin = AdminClient();
        await OpenOkAsync(admin);

        var text = await admin.PutAsync($"{OpenUrl}/photos/c1", new StringContent("results", Encoding.UTF8, "text/plain"));
        var big = await admin.PutAsync($"{OpenUrl}/photos/c1", new StringContent(new string('x', (3 * 1024 * 1024)), Encoding.UTF8, "application/json"));

        text.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        big.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task A_photo_with_no_results_is_a_400()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        using var admin = AdminClient();
        await OpenOkAsync(admin);

        var response = await PutPhotoAsync(admin, "c1");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("evals.results_required");
    }
}
