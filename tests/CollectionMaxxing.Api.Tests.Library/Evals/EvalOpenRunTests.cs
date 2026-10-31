using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Scanning;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.TestSupport.Evals;

namespace CollectionMaxxing.Api.Tests.Evals;

/// <summary>
/// The phone opens a run on the PC and the run lives there until it is finished: opening it names the golden photos in reading order, asking for
/// it tells which photos the PC already has, and discarding it throws its stored files away. There is at most one open run.
/// </summary>
public class EvalOpenRunTests(ApiFixture api) : OpenRunTest(api)
{
    private void ThreePhotos()
    {
        Golden.AddSingle("c2", StarFoxTruth);
        Golden.AddShelf("s2", [new SyntheticGolden.ShelfItem("Star Fox 64", "game", "Nintendo 64")]);
        Golden.AddShelf("s1", [new SyntheticGolden.ShelfItem("Star Fox 64", "game", "Nintendo 64")]);
    }

    [Fact]
    public async Task Opening_a_run_names_the_golden_photos_in_the_order_the_phone_reads_them_and_the_PC_has_none_yet()
    {
        ThreePhotos();
        using var admin = AdminClient();

        var run = await OpenOkAsync(admin);

        run.GetProperty("runId").GetString().ShouldNotBeNullOrWhiteSpace();
        run.GetProperty("state").GetString().ShouldBe("open");
        run.GetProperty("partial").GetBoolean().ShouldBeFalse();
        run.GetProperty("configVersion").GetString().ShouldBe("cfg-test-1");
        run.GetProperty("device").GetProperty("model").GetString().ShouldBe("iPhone18,1");
        run.GetProperty("photos").EnumerateArray().Select(p => $"{p.GetProperty("set").GetString()}/{p.GetProperty("id").GetString()}")
            .ShouldBe(["shelf/s1", "shelf/s2", "single/c2"]);
        Received(run).ShouldBeEmpty();
        run.GetProperty("grounded").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Asking_for_the_open_run_returns_the_same_run_and_the_photos_the_PC_already_has()
    {
        ThreePhotos();
        using var admin = AdminClient();
        var opened = await OpenOkAsync(admin, OpenBody("T1 bar moved for the re-run."));
        await PutPhotoOkAsync(admin, "s2", AllEngines("s2", StarFoxRead));
        await PutPhotoOkAsync(admin, "c2", AllEngines("c2", StarFoxRead));

        var status = await StatusAsync(admin);

        status.GetProperty("runId").GetString().ShouldBe(opened.GetProperty("runId").GetString());
        status.GetProperty("baselineNote").GetString().ShouldBe("T1 bar moved for the re-run.");
        Received(status).ShouldBe(["s2", "c2"], "in the run's order, whatever order they were sent in");
        status.GetProperty("photos").GetArrayLength().ShouldBe(3);
    }

    [Fact]
    public async Task The_open_run_is_kept_in_the_results_folder_where_the_owner_keeps_the_rest_of_the_evals()
    {
        ThreePhotos();
        using var admin = AdminClient();
        await OpenOkAsync(admin);
        await PutPhotoOkAsync(admin, "s1", AllEngines("s1", StarFoxRead));

        Directory.Exists(OpenRunFolder).ShouldBeTrue();
        SyntheticGolden.Files(Golden.Results).ShouldAllBe(file => file.StartsWith("open-run" + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        ResultFiles().ShouldBeEmpty("a run that is not finished is not a result");
    }

    [Fact]
    public async Task Asking_for_the_open_run_when_there_is_none_is_a_404_that_says_so()
    {
        ThreePhotos();
        using var admin = AdminClient();

        var response = await admin.GetAsync(OpenUrl);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("evals.no_open_run");
    }

    [Fact]
    public async Task Opening_a_run_while_one_is_open_is_a_409_and_leaves_the_open_run_as_it_was()
    {
        ThreePhotos();
        using var admin = AdminClient();
        var first = await OpenOkAsync(admin);
        await PutPhotoOkAsync(admin, "s1", AllEngines("s1", StarFoxRead));

        var second = await OpenAsync(admin);

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadJsonAsync(second)).GetProperty("code").GetString().ShouldBe("evals.run_open");
        var status = await StatusAsync(admin);
        status.GetProperty("runId").GetString().ShouldBe(first.GetProperty("runId").GetString());
        Received(status).ShouldBe(["s1"]);
    }

    [Theory]
    [InlineData("""{ "configVersion": "c" }""", "evals.device_required")]
    [InlineData("""{ "device": { "model": "m", "osVersion": "o" }, "configVersion": "c" }""", "evals.device_required")]
    [InlineData("""{ "device": { "model": "m", "osVersion": "o", "appVersion": "a" } }""", "evals.config_version_required")]
    public async Task Opening_a_run_needs_the_device_and_the_config_version(string body, string code)
    {
        ThreePhotos();
        using var admin = AdminClient();

        var response = await admin.PostAsync(OpenUrl, new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe(code);
        (await admin.GetAsync(OpenUrl)).StatusCode.ShouldBe(HttpStatusCode.NotFound, "a refused run is not left open");
    }

    [Fact]
    public async Task Opening_a_run_with_no_golden_photos_is_a_400()
    {
        using var admin = AdminClient();

        var response = await OpenAsync(admin);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("evals.no_photos");
    }

    [Fact]
    public async Task Discarding_the_open_run_throws_its_stored_files_away_and_the_phone_can_open_another()
    {
        ThreePhotos();
        using var admin = AdminClient();
        var first = await OpenOkAsync(admin);
        await PutPhotoOkAsync(admin, "s1", AllEngines("s1", StarFoxRead));

        var discarded = await admin.DeleteAsync(OpenUrl);

        discarded.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await admin.GetAsync(OpenUrl)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        Directory.Exists(OpenRunFolder).ShouldBeFalse();
        var second = await OpenOkAsync(admin);
        second.GetProperty("runId").GetString().ShouldNotBe(first.GetProperty("runId").GetString());
        Received(second).ShouldBeEmpty("the new run starts with nothing");
    }

    [Fact]
    public async Task Discarding_when_nothing_is_open_is_a_404()
    {
        ThreePhotos();
        using var admin = AdminClient();

        var response = await admin.DeleteAsync(OpenUrl);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("evals.no_open_run");
    }

    [Fact]
    public async Task A_finished_run_does_not_stop_the_phone_opening_the_next_one()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        using var admin = AdminClient();
        var first = await OpenOkAsync(admin);
        await PutPhotoOkAsync(admin, "c1", AllEngines("c1", StarFoxRead));
        await FinishOkAsync(admin);
        var done = await StatusWhenDoneAsync(admin);
        done.GetProperty("runId").GetString().ShouldBe(first.GetProperty("runId").GetString());

        var next = await OpenOkAsync(admin);

        next.GetProperty("runId").GetString().ShouldNotBe(first.GetProperty("runId").GetString());
        next.GetProperty("state").GetString().ShouldBe("open");
        ResultFiles().Count.ShouldBe(1, "the finished run's result stays");
    }
}
