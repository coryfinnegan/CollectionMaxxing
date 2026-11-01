using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Scanning;
using CollectionMaxxing.Api.Tests.Support;

namespace CollectionMaxxing.Api.Tests.Evals;

/// <summary>
/// When a run is scored, the PC keeps what the phone read, as <c>latest-inputs.json</c> beside the result files, so a later server version can ground and
/// score the same readings again without the phone. Both ways of taking a run keep them; a newer run replaces them.
/// </summary>
public class EvalKeptReadingsTests(ApiFixture api) : OpenRunTest(api)
{
    private const string KeptFile = "latest-inputs.json";

    private HttpClient AdminAs(Guid userId)
    {
        var client = AsUser(userId);
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "admin");
        return client;
    }

    private static IEnumerable<string> SkippedOf(JsonElement kept) =>
        kept.GetProperty("skipped").EnumerateArray().Select(s => $"{s.GetProperty("photoId").GetString()}:{s.GetProperty("reason").GetString()}");

    [Fact]
    public async Task A_scored_resumable_run_keeps_the_phones_readings_beside_the_result_files()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        var opener = Guid.CreateVersion7();
        using var admin = AdminAs(opener);
        var runId = (await OpenOkAsync(admin)).GetProperty("runId").GetString()!;
        await PutPhotoOkAsync(admin, "c1", AllEngines("c1", StarFoxRead));
        await FinishOkAsync(admin);
        await StatusWhenDoneAsync(admin);

        var kept = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Golden.Results, KeptFile))).RootElement;

        kept.GetProperty("runId").GetString().ShouldBe(runId);
        kept.GetProperty("openedBy").GetGuid().ShouldBe(opener);
        kept.GetProperty("device").GetProperty("model").GetString().ShouldBe("iPhone18,1");
        kept.GetProperty("configVersion").GetString().ShouldBe("cfg-test-1");
        kept.GetProperty("partial").GetBoolean().ShouldBeFalse();
        kept.GetProperty("readAt").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-10));
        var photo = kept.GetProperty("photos").EnumerateArray().ShouldHaveSingleItem();
        photo.GetProperty("photoId").GetString().ShouldBe("c1");
        var results = photo.GetProperty("results");
        results.GetArrayLength().ShouldBe(3, "one result for each engine, as the phone sent them");
        foreach (var result in results.EnumerateArray())
        {
            result.GetProperty("items")[0].GetProperty("read").GetProperty("title").GetString().ShouldBe("Star Fox 64");
        }

        SkippedOf(kept).ShouldBeEmpty();
        Directory.GetFiles(Golden.Results, "*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public async Task The_one_shot_route_keeps_the_phones_readings_too()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        var caller = Guid.CreateVersion7();
        using var admin = AdminAs(caller);

        var response = await PostRunOkAsync(admin, RunBody(AllEngines("c1", StarFoxRead)));

        var kept = await ReadKeptAsync();
        kept.GetProperty("runId").GetString().ShouldBe(response.GetProperty("runId").GetString());
        kept.GetProperty("openedBy").GetGuid().ShouldBe(caller, "the one-shot caller is the person the readings are grounded for");
        kept.GetProperty("photos").EnumerateArray().ShouldHaveSingleItem().GetProperty("results").GetArrayLength().ShouldBe(3);
    }

    [Fact]
    public async Task The_kept_readings_list_the_photos_the_checks_left_out_and_hold_only_the_ones_that_passed()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Golden.AddSingle("c2", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        using var admin = AdminClient();

        await PostRunOkAsync(admin, RunBody(
            PhotoResult("c1", "apple_model", [StarFoxRead()]),
            PhotoResult("c2", "apple_model", [StarFoxRead()], latencyMs: -5)));

        var kept = await ReadKeptAsync();
        kept.GetProperty("photos").EnumerateArray().Select(p => p.GetProperty("photoId").GetString()).ShouldBe(["c1"]);
        SkippedOf(kept).ShouldBe(["c2:evals.latency_invalid"]);
    }

    [Fact]
    public async Task A_run_the_phone_cancelled_is_kept_as_partial()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        using var admin = AdminClient();
        await OpenOkAsync(admin);
        await PutPhotoOkAsync(admin, "c1", AllEngines("c1", StarFoxRead));
        await FinishOkAsync(admin, partial: true);
        await StatusWhenDoneAsync(admin);

        (await ReadKeptAsync()).GetProperty("partial").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_newer_run_replaces_the_kept_readings()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        Games.Results.Add(ScanData.Game("The Legend of Zelda", "Nintendo 64", 1998, "g2"));
        using var admin = AdminClient();
        await PostRunOkAsync(admin, RunBody(AllEngines("c1", StarFoxRead)));
        var second = await PostRunOkAsync(admin, RunBody(AllEngines("c1", ZeldaRead)));

        var kept = await ReadKeptAsync();

        kept.GetProperty("runId").GetString().ShouldBe(second.GetProperty("runId").GetString());
        kept.GetProperty("photos")[0].GetProperty("results")[0].GetProperty("items")[0].GetProperty("read").GetProperty("title").GetString()
            .ShouldBe("The Legend of Zelda");
    }

    [Fact]
    public async Task A_run_that_was_refused_leaves_the_kept_readings_of_the_last_scored_run_alone()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        using var admin = AdminClient();
        var first = await PostRunOkAsync(admin, RunBody(AllEngines("c1", StarFoxRead)));

        var refused = await PostRunAsync(admin, RunBody());

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadKeptAsync()).GetProperty("runId").GetString().ShouldBe(first.GetProperty("runId").GetString());
    }

    [Fact]
    public async Task A_run_whose_results_were_written_just_before_the_API_stopped_still_has_its_readings_kept()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        using var admin = AdminClient();
        var runId = (await OpenOkAsync(admin)).GetProperty("runId").GetString()!;
        // The API stopped after writing this run's results and before keeping its readings or marking it done.
        await File.WriteAllTextAsync(Path.Combine(Golden.Results, "latest.json"), JsonSerializer.Serialize(new { runId, partial = false }));
        await PutPhotoOkAsync(admin, "c1", AllEngines("c1", StarFoxRead));
        await FinishOkAsync(admin);
        await StatusWhenDoneAsync(admin);

        (await ReadKeptAsync()).GetProperty("runId").GetString().ShouldBe(runId);
        ResultFiles().ShouldBeEmpty("the results were already there: nothing is scored again");
    }

    private async Task<JsonElement> ReadKeptAsync() =>
        JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Golden.Results, KeptFile))).RootElement.Clone();
}
