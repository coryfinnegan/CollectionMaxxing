using System.Net;
using CollectionMaxxing.Api.Tests.Scanning;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.TestSupport.Evals;

namespace CollectionMaxxing.Api.Tests.Evals;

/// <summary>
/// The PC grounds each photo in the background as it arrives, and scores once when the phone finishes: one run, one result file, the same files
/// the one-shot route writes. Finishing returns at once; finishing early marks the run partial, as cancelling always has.
/// </summary>
public class EvalOpenRunScoringTests(ApiFixture api) : OpenRunTest(api)
{
    private static readonly string[] ResultFileNames = ["latest-run.json", "latest.json", "report.md"];

    [Fact]
    public async Task Photos_are_grounded_in_the_background_as_they_arrive_before_the_phone_finishes()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Golden.AddSingle("c2", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        using var admin = AdminClient();
        await OpenOkAsync(admin);

        await PutPhotoOkAsync(admin, "c1", AllEngines("c1", StarFoxRead));
        var status = await StatusWhenAsync(admin, s => s.GetProperty("grounded").GetInt32() == 1);

        status.GetProperty("state").GetString().ShouldBe("open");
        ResultFiles().ShouldBeEmpty("nothing is scored until the phone finishes");
        await PutPhotoOkAsync(admin, "c2", AllEngines("c2", StarFoxRead));
        (await StatusWhenAsync(admin, s => s.GetProperty("grounded").GetInt32() == 2)).GetProperty("state").GetString().ShouldBe("open");
    }

    [Fact]
    public async Task Finishing_returns_at_once_and_the_run_is_scored_once_every_stored_photo_is_grounded_as_one_result_file()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Golden.AddSingle("c2", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Games.Behaviour = async cancellationToken => await release.Task.WaitAsync(cancellationToken);
        using var admin = AdminClient();
        var opened = await OpenOkAsync(admin);
        await PutPhotoOkAsync(admin, "c1", AllEngines("c1", StarFoxRead));
        await PutPhotoOkAsync(admin, "c2", AllEngines("c2", StarFoxRead));

        var finishing = await FinishOkAsync(admin).WaitAsync(Bounded);

        finishing.GetProperty("state").GetString().ShouldBe("finishing");
        ResultFiles().ShouldBeEmpty("the providers have not answered, so nothing can be scored yet");
        release.SetResult();
        var done = await StatusWhenDoneAsync(admin);

        done.GetProperty("runId").GetString().ShouldBe(opened.GetProperty("runId").GetString());
        ResultFiles().Count.ShouldBe(1, "one run, one result file");
        foreach (var name in ResultFileNames)
        {
            File.Exists(Path.Combine(Golden.Results, name)).ShouldBeTrue(name);
        }

        var summary = done.GetProperty("summary");
        summary.GetProperty("runId").GetString().ShouldBe(opened.GetProperty("runId").GetString());
        summary.GetProperty("scoredPhotos").GetInt32().ShouldBe(6);
        summary.GetProperty("partial").GetBoolean().ShouldBeFalse();
        summary.GetProperty("engines").GetProperty("apple_model").GetProperty("single").GetProperty("top1Pct").GetDouble().ShouldBe(100);
        (await LatestSummaryAsync()).GetProperty("runId").GetString().ShouldBe(opened.GetProperty("runId").GetString());
        (await admin.GetAsync($"{Base}/runs/latest")).StatusCode.ShouldBe(HttpStatusCode.OK, "the existing route reads the same summary");
        (await LatestRunFileAsync()).GetProperty("photos").GetArrayLength().ShouldBe(6);
    }

    [Fact]
    public async Task A_finished_run_gives_back_the_stored_photos_and_keeps_only_the_results()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        using var admin = AdminClient();
        await OpenOkAsync(admin);
        await PutPhotoOkAsync(admin, "c1", AllEngines("c1", StarFoxRead));
        await FinishOkAsync(admin);

        await StatusWhenDoneAsync(admin);

        Directory.Exists(Path.Combine(OpenRunFolder, "photos")).ShouldBeFalse();
        Directory.Exists(Path.Combine(OpenRunFolder, "grounded")).ShouldBeFalse();
    }

    [Fact]
    public async Task Finishing_early_scores_what_the_PC_has_and_marks_the_run_partial()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Golden.AddSingle("c2", StarFoxTruth);
        Golden.AddSingle("c3", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        using var admin = AdminClient();
        await OpenOkAsync(admin);
        await PutPhotoOkAsync(admin, "c1", AllEngines("c1", StarFoxRead));

        await FinishOkAsync(admin, partial: true);
        var summary = (await StatusWhenDoneAsync(admin)).GetProperty("summary");

        summary.GetProperty("partial").GetBoolean().ShouldBeTrue();
        summary.GetProperty("photosRun").GetInt32().ShouldBe(1);
        summary.GetProperty("photosInSet").GetInt32().ShouldBe(3);
        summary.GetProperty("gate").GetProperty("verdict").GetString().ShouldBe("incomplete");
        summary.GetProperty("regression").GetProperty("status").GetString().ShouldBe("partial_run");
        ResultFiles().Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_run_finished_with_photos_missing_is_partial_even_when_the_phone_did_not_say_so()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Golden.AddSingle("c2", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        using var admin = AdminClient();
        await OpenOkAsync(admin);
        await PutPhotoOkAsync(admin, "c2", AllEngines("c2", StarFoxRead));

        var finishing = await FinishOkAsync(admin);
        var done = await StatusWhenDoneAsync(admin);

        finishing.GetProperty("partial").GetBoolean().ShouldBeTrue();
        done.GetProperty("summary").GetProperty("partial").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_run_is_scored_with_the_truth_files_exactly_as_the_one_shot_route_scores_it()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Golden.AddShelf("s1", [new SyntheticGolden.ShelfItem("Star Fox 64", "game", "Nintendo 64"), new SyntheticGolden.ShelfItem("The Legend of Zelda", "game", "Nintendo 64")]);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        Games.Results.Add(ScanData.Game("The Legend of Zelda", "Nintendo 64", 1998, "g2"));
        using var admin = AdminClient();
        var shelf = new[]
        {
            PhotoResult("s1", "apple_model", [StarFoxRead(), ZeldaRead()]),
            PhotoResult("s1", "vision_pipeline", [StarFoxRead()]),
        };

        var oneShot = await PostRunOkAsync(admin, RunBody([.. AllEngines("c1", StarFoxRead), .. shelf]));
        await OpenOkAsync(admin);
        await PutPhotoOkAsync(admin, "c1", AllEngines("c1", StarFoxRead));
        await PutPhotoOkAsync(admin, "s1", shelf);
        await FinishOkAsync(admin);
        var opened = (await StatusWhenDoneAsync(admin)).GetProperty("summary");

        var expected = oneShot.GetProperty("summary");
        opened.GetProperty("metrics").GetRawText().ShouldBe(expected.GetProperty("metrics").GetRawText());
        opened.GetProperty("gate").GetRawText().ShouldBe(expected.GetProperty("gate").GetRawText());
        opened.GetProperty("engines").GetRawText().ShouldBe(expected.GetProperty("engines").GetRawText());
        opened.GetProperty("scoredPhotos").GetInt32().ShouldBe(expected.GetProperty("scoredPhotos").GetInt32());
        ResultFiles().Count.ShouldBe(2, "the one-shot run's file and this one's");
    }

    [Fact]
    public async Task A_photo_without_a_truth_file_is_skipped_and_gets_a_draft_like_in_the_one_shot_route()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Golden.AddSingle("c2", null);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        using var admin = AdminClient();
        await OpenOkAsync(admin);
        await PutPhotoOkAsync(admin, "c1", AllEngines("c1", StarFoxRead));
        await PutPhotoOkAsync(admin, "c2", AllEngines("c2", StarFoxRead));
        await FinishOkAsync(admin);

        var summary = (await StatusWhenDoneAsync(admin)).GetProperty("summary");

        Skipped(summary).ShouldBe(["c2:no_truth"]);
        summary.GetProperty("scoredPhotos").GetInt32().ShouldBe(3);
        (File.Exists(Path.Combine(Golden.Golden, "single", "c2.truth.draft.json")) || File.Exists(Path.Combine(Golden.Results, "drafts", "c2.truth.draft.json")))
            .ShouldBeTrue("a draft truth file is written for the owner to correct");
    }

    [Fact]
    public async Task Finishing_twice_is_harmless_and_scores_once()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        using var admin = AdminClient();
        await OpenOkAsync(admin);
        await PutPhotoOkAsync(admin, "c1", AllEngines("c1", StarFoxRead));

        await FinishOkAsync(admin);
        var again = await FinishAsync(admin);
        await StatusWhenDoneAsync(admin);
        var afterDone = await FinishAsync(admin);

        again.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        afterDone.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        ResultFiles().Count.ShouldBe(1);
    }

    [Fact]
    public async Task Finishing_a_run_with_nothing_stored_is_a_400_and_the_run_stays_open()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        using var admin = AdminClient();
        await OpenOkAsync(admin);

        var response = await FinishAsync(admin);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("evals.results_required");
        (await StatusAsync(admin)).GetProperty("state").GetString().ShouldBe("open");
    }

    [Fact]
    public async Task Finishing_when_no_run_is_open_is_a_404()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        using var admin = AdminClient();

        var response = await FinishAsync(admin);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("evals.no_open_run");
    }

    [Fact]
    public async Task Discarding_a_run_that_is_waiting_to_be_scored_scores_nothing()
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
        await PutPhotoOkAsync(admin, "c1", AllEngines("c1", StarFoxRead));
        await FinishOkAsync(admin);
        await asked.Task.WaitAsync(Bounded);

        (await admin.DeleteAsync(OpenUrl)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        release.SetResult();
        await Task.Delay(TimeSpan.FromMilliseconds(500)); // long enough for a wrongly resumed grounding to write something

        ResultFiles().ShouldBeEmpty();
        File.Exists(Path.Combine(Golden.Results, "latest.json")).ShouldBeFalse();
        Directory.Exists(OpenRunFolder).ShouldBeFalse("the late grounding did not bring the discarded run back");
    }

    [Fact]
    public async Task A_photo_replaced_while_it_is_being_grounded_is_grounded_again_and_the_new_reads_are_scored()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        Games.Results.Add(ScanData.Game("The Legend of Zelda", "Nintendo 64", 1998, "g2"));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = true;
        Games.Behaviour = async cancellationToken =>
        {
            if (first)
            {
                first = false;
                asked.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
        };
        using var admin = AdminClient();
        await OpenOkAsync(admin);
        await PutPhotoOkAsync(admin, "c1", AllEngines("c1", StarFoxRead));
        await asked.Task.WaitAsync(Bounded); // the first send is being grounded

        await PutPhotoOkAsync(admin, "c1", AllEngines("c1", ZeldaRead)); // the phone sent the photo again meanwhile
        release.SetResult();
        await FinishOkAsync(admin);
        await StatusWhenDoneAsync(admin);

        foreach (var photo in (await LatestRunFileAsync()).GetProperty("photos").EnumerateArray())
        {
            photo.GetProperty("items")[0].GetProperty("readTitle").GetString().ShouldBe("The Legend of Zelda");
        }
    }

    [Fact]
    public async Task A_read_that_several_photos_share_is_looked_up_once_in_a_run_and_again_in_the_next()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Golden.AddSingle("c2", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        var lookups = 0;
        Games.Behaviour = _ =>
        {
            Interlocked.Increment(ref lookups);
            return Task.CompletedTask;
        };
        using var admin = AdminClient();
        await OpenOkAsync(admin);
        await PutPhotoOkAsync(admin, "c1", AllEngines("c1", StarFoxRead));
        await PutPhotoOkAsync(admin, "c2", AllEngines("c2", StarFoxRead));
        await FinishOkAsync(admin);
        await StatusWhenDoneAsync(admin);
        lookups.ShouldBe(1, "six results carry one read, and the run has one memo");

        await OpenOkAsync(admin);
        await PutPhotoOkAsync(admin, "c1", AllEngines("c1", StarFoxRead));
        await FinishOkAsync(admin);
        await StatusWhenAsync(admin, s => s.GetProperty("state").GetString() == "done" && ResultFiles().Count == 2);

        lookups.ShouldBe(2, "what one run learned is not kept for the next: the catalogue may have changed");
    }
}
