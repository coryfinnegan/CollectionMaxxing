using System.Text.Json;
using System.Text.Json.Nodes;
using CollectionMaxxing.Api.Tests.Catalog;
using CollectionMaxxing.Api.Tests.Scanning;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Infrastructure.Evals;
using CollectionMaxxing.TestSupport;
using CollectionMaxxing.TestSupport.Evals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CollectionMaxxing.Api.Tests.Evals;

/// <summary>
/// Most recognition fixes change only the PC's half of a run (combination, kinds, matching), so a new server version should be measured on the last phone
/// run without another phone run. When the API starts as another version than the one that made the latest result, its worker grounds and scores the kept
/// phone readings again, by itself, as a new run marked with the run it came from. It never touches a run that is open, and it never loops.
/// </summary>
public class EvalRegroundTests(ApiFixture api) : RegroundTest(api)
{
    private const string Title = "Zorblax Quest";
    private const string LeakMarker = "leak-marker-7qz";

    private static readonly SyntheticGolden.SingleItem ZorblaxTruth = new(Title, "game_cartridge", "cart_only", "loose_cartridge");

    private static object ZorblaxRead() => ReadItem(Title, "game", "game_cartridge", completeness: "cart_only", resolvedBy: "model");

    private HttpClient AdminMember(TestLibrary library)
    {
        var client = AsUser(library.OwnerId);
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "admin");
        return client;
    }

    private void StarFoxGolden(params string[] photoIds)
    {
        foreach (var id in photoIds)
        {
            Golden.AddSingle(id, StarFoxTruth);
        }

        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
    }

    private async Task<string> FirstRunAsync(params string[] photoIds)
    {
        using var phone = AdminClient();
        return await PhoneRunAsync(phone, photoIds);
    }

    private async Task<IReadOnlyList<string?>> BestTitlesInLatestRunAsync() =>
        [.. (await LatestRunFileAsync()).GetProperty("photos").EnumerateArray()
            .Select(photo => photo.GetProperty("items")[0].GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("title").GetString()).FirstOrDefault())];

    private string RequestFile => Path.Combine(Golden.Results, "reground.request");

    // ---- the re-ground itself ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task After_a_server_change_the_worker_re_grounds_the_kept_readings_and_writes_a_new_result_under_the_new_sha_marked_with_the_run_it_came_from()
    {
        StarFoxGolden("c1");
        var firstRun = await FirstRunAsync("c1");
        var original = TryLatestSummary()!.Value;
        var kept = await KeptInputsAsync();

        await RedeployAsync();
        var rescored = await NextLatestAfterAsync(firstRun);

        rescored.GetProperty("buildSha").GetString().ShouldBe(NewSha);
        rescored.GetProperty("runId").GetString().ShouldNotBe(firstRun, "it is a new run");
        IsRegrounded(original).ShouldBeFalse();
        var from = rescored.GetProperty("regroundedFrom");
        from.GetProperty("runId").GetString().ShouldBe(firstRun);
        from.GetProperty("readAt").GetDateTimeOffset().ShouldBe(kept.GetProperty("readAt").GetDateTimeOffset());
        rescored.GetProperty("device").GetRawText().ShouldBe(original.GetProperty("device").GetRawText());
        rescored.GetProperty("configVersion").GetString().ShouldBe("cfg-test-1");
        rescored.GetProperty("partial").GetBoolean().ShouldBeFalse();
        rescored.GetProperty("scoredPhotos").GetInt32().ShouldBe(3);
        ResultFiles().Select(Path.GetFileName).ShouldBe([$"{rescored.GetProperty("date").GetString()}-{OldSha}.json", $"{rescored.GetProperty("date").GetString()}-{NewSha}.json"]);
        (await File.ReadAllTextAsync(Path.Combine(Golden.Results, "report.md"))).ShouldStartWith($"Re-scored the phone readings of run {firstRun}");
        (await LatestRunFileAsync()).GetProperty("runId").GetString().ShouldBe(rescored.GetProperty("runId").GetString());
        (await LatestRunFileAsync()).GetProperty("photos").GetArrayLength().ShouldBe(3);
        (await KeptInputsAsync()).GetProperty("runId").GetString().ShouldBe(firstRun, "the readings are still the phone's, so the next server version scores them too");
    }

    [Fact]
    public async Task The_re_ground_of_unchanged_code_gives_the_same_scores_as_the_original_run_metric_by_metric()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Golden.AddSingle("c2", ZeldaTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        var firstRun = await FirstRunAsync("c1", "c2"); // c2 reads Star Fox where the truth is Zelda: a miss
        var original = TryLatestSummary()!.Value;

        await RedeployAsync();
        var rescored = await NextLatestAfterAsync(firstRun);

        var before = MetricsOf(original);
        var after = MetricsOf(rescored);
        before.Count.ShouldBeGreaterThan(20, "the run has real numbers to compare");
        after.Keys.OrderBy(k => k, StringComparer.Ordinal).ShouldBe(before.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var (name, value) in before)
        {
            after[name].ShouldBe(value, name);
        }

        before["apple_model.single.top1"].ShouldBe(50, "the miss is in the numbers, so they are not all 100");
        foreach (var field in new[] { "scoredPhotos", "photosRun", "photosInSet", "thresholdHash", "skipped" })
        {
            rescored.GetProperty(field).GetRawText().ShouldBe(original.GetProperty(field).GetRawText(), field);
        }

        rescored.GetProperty("gate").GetProperty("verdict").GetString().ShouldBe(original.GetProperty("gate").GetProperty("verdict").GetString());
        rescored.GetProperty("regression").GetProperty("status").GetString().ShouldBe(original.GetProperty("regression").GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_run_the_phone_cancelled_is_re_scored_as_partial_with_the_same_device_and_config()
    {
        StarFoxGolden("c1");
        string firstRun;
        using (var phone = AdminClient())
        {
            firstRun = await PhoneRunAsync(phone, ["c1"], partial: true);
        }

        await RedeployAsync();
        var rescored = await NextLatestAfterAsync(firstRun);

        rescored.GetProperty("partial").GetBoolean().ShouldBeTrue();
        rescored.GetProperty("gate").GetProperty("verdict").GetString().ShouldBe("incomplete");
        rescored.GetProperty("regression").GetProperty("status").GetString().ShouldBe("partial_run");
    }

    [Fact]
    public async Task A_phone_run_with_a_photo_the_PC_gave_up_on_is_re_scored_with_every_photo_and_is_not_partial_so_it_is_compared_with_the_baseline()
    {
        StarFoxGolden("c1", "c2");
        var baseline = Path.Combine(Golden.Root, "baseline.json");
        await File.WriteAllTextAsync(baseline, JsonSerializer.Serialize(new { metrics = new Dictionary<string, double> { ["apple_model.single.top1"] = 100 } }));
        Settings["Evals:BaselinePath"] = baseline;
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
        UseClockAndFlakyStore(clock, (photoId, file) => photoId == "c1" && file.Photos.Count > 0); // the PC never manages to keep c1's grounding
        string firstRun;
        using (var phone = AdminClient())
        {
            firstRun = (await OpenOkAsync(phone)).GetProperty("runId").GetString()!;
            await PutPhotoOkAsync(phone, "c1", AllEngines("c1", StarFoxRead));
            await PutPhotoOkAsync(phone, "c2", AllEngines("c2", StarFoxRead));
            await FinishOkAsync(phone); // the phone read every photo and did not say partial
            (await BecomesTrueAsync(
                () =>
                {
                    clock.Advance(TimeSpan.FromMinutes(10));
                    return ResultFiles().Count == 1;
                },
                Bounded)).ShouldBeTrue("the PC gave up on c1 and scored the rest");
            await StatusWhenDoneAsync(phone);
        }

        var original = TryLatestSummary()!.Value;
        original.GetProperty("partial").GetBoolean().ShouldBeTrue("a photo given up on makes the original run partial");
        original.GetProperty("scoredPhotos").GetInt32().ShouldBe(3);
        (await KeptInputsAsync()).GetProperty("partial").GetBoolean().ShouldBeFalse("the kept partial flag is the phone's own: the give-up belongs to that run's grounding, not to the readings");
        (await KeptInputsAsync()).GetProperty("photos").GetArrayLength().ShouldBe(2, "the readings of the photo given up on are kept");
        ConfigureServices = null; // the redeployed server's disk works

        await RedeployAsync();
        var rescored = await NextLatestAfterAsync(firstRun);

        rescored.GetProperty("scoredPhotos").GetInt32().ShouldBe(6, "both photos ground now");
        rescored.GetProperty("partial").GetBoolean().ShouldBeFalse();
        rescored.GetProperty("skipped").GetArrayLength().ShouldBe(0, "no give-up is carried over");
        rescored.GetProperty("regression").GetProperty("status").GetString().ShouldBe("ok", "a complete re-score is compared with the baseline");
    }

    [Fact]
    public async Task A_photo_that_cannot_be_grounded_in_the_re_ground_is_given_up_alone_and_the_rest_are_scored_as_a_partial_run()
    {
        StarFoxGolden("c1", "c2");
        var firstRun = await FirstRunAsync("c1", "c2");
        await BreakKeptReadingsAsync("c2");

        await RedeployAsync();
        var rescored = await NextLatestAfterAsync(firstRun);

        rescored.GetProperty("scoredPhotos").GetInt32().ShouldBe(3, "c1's three engines");
        rescored.GetProperty("partial").GetBoolean().ShouldBeTrue("a photo given up on is a photo the run did not cover");
        rescored.GetProperty("skipped").EnumerateArray().Select(s => $"{s.GetProperty("photoId").GetString()}:{s.GetProperty("reason").GetString()}")
            .ShouldBe(["c2:grounding_failed"]);
        rescored.GetProperty("regression").GetProperty("status").GetString().ShouldBe("partial_run");
        rescored.GetProperty("regroundedFrom").GetProperty("runId").GetString().ShouldBe(firstRun);
        Logged("could not be grounded (InvalidOperationException)").ShouldBeTrue("logged by the type of the error only");
    }

    [Fact]
    public async Task When_no_photo_can_be_grounded_in_the_re_ground_nothing_is_written()
    {
        StarFoxGolden("c1", "c2");
        var firstRun = await FirstRunAsync("c1", "c2");
        await BreakKeptReadingsAsync("c1", "c2");

        await RedeployAsync();

        (await LogsAsync("evals.nothing_grounded")).ShouldBeTrue();
        LatestRunId().ShouldBe(firstRun, "a result with no photo in it never replaces a real one");
        ResultFiles().Count.ShouldBe(1);
    }

    /// <summary>Damages the kept readings of these photos so that grounding them throws: an engine the PC cannot read.</summary>
    private async Task BreakKeptReadingsAsync(params string[] photoIds)
    {
        var path = Path.Combine(Golden.Results, "latest-inputs.json");
        var kept = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        foreach (var photo in kept["photos"]!.AsArray().Where(p => photoIds.Contains(p!["photoId"]!.GetValue<string>())))
        {
            foreach (var result in photo!["results"]!.AsArray())
            {
                result!["engine"] = null;
            }
        }

        await File.WriteAllTextAsync(path, kept.ToJsonString());
    }

    [Fact]
    public async Task The_re_ground_writes_no_draft_truth_files_for_photos_that_have_no_truth()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Golden.AddSingle("c2", null);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        var firstRun = await FirstRunAsync("c1", "c2");
        var draft = Path.Combine(Golden.Golden, "single", "c2.truth.draft.json");
        File.Exists(draft).ShouldBeTrue("the phone run wrote a draft for the photo without truth");
        File.Delete(draft);

        await RedeployAsync();
        var rescored = await NextLatestAfterAsync(firstRun);

        File.Exists(draft).ShouldBeFalse("a re-score writes no drafts");
        Directory.Exists(Path.Combine(Golden.Results, "drafts")).ShouldBeFalse();
        rescored.GetProperty("skipped").EnumerateArray().Select(s => $"{s.GetProperty("photoId").GetString()}:{s.GetProperty("reason").GetString()}")
            .ShouldBe(["c2:no_truth"], "the photo is still listed as skipped");
    }

    [Fact]
    public async Task The_re_ground_acts_as_the_runs_opener_so_a_release_a_library_made_for_itself_is_found_as_in_the_phone_run()
    {
        var library = await CreateLibraryAsync();
        await CatalogSeed.AddAsync(Db, Title, WorkKind.Game, 1997, ownerLibraryId: library.Id, format: ReleaseFormat.GameCartridge);
        Golden.AddSingle("c1", ZorblaxTruth);
        string firstRun;
        using (var opener = AdminMember(library))
        {
            firstRun = await PhoneRunAsync(opener, ["c1"], read: ZorblaxRead);
        }

        var phoneRun = await BestTitlesInLatestRunAsync();
        await RedeployAsync();
        await NextLatestAfterAsync(firstRun);
        var rescored = await BestTitlesInLatestRunAsync();

        phoneRun.ShouldBe([Title, Title, Title], "the phone run saw the library's own release");
        rescored.ShouldBe(phoneRun);
    }

    // ---- when it does nothing -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task When_the_latest_result_already_has_the_current_sha_nothing_is_re_grounded()
    {
        StarFoxGolden("c1");
        var firstRun = await FirstRunAsync("c1");
        var lookups = 0;
        Games.Behaviour = _ =>
        {
            Interlocked.Increment(ref lookups);
            return Task.CompletedTask;
        };

        await RedeployAsync(OldSha);
        (await LogsAsync("was made by this server version")).ShouldBeTrue("the worker looked, and decided there is nothing to do");

        lookups.ShouldBe(0, "no read was grounded again");
        LatestRunId().ShouldBe(firstRun);
        ResultFiles().Count.ShouldBe(1);
    }

    [Fact]
    public async Task While_a_phone_run_is_open_nothing_is_re_grounded_and_the_phone_run_is_not_disturbed()
    {
        StarFoxGolden("c1");
        string firstRun;
        string secondRun;
        using (var phone = AdminClient())
        {
            firstRun = await PhoneRunAsync(phone, ["c1"]);
            secondRun = (await OpenOkAsync(phone)).GetProperty("runId").GetString()!;
            await PutPhotoOkAsync(phone, "c1", AllEngines("c1", StarFoxRead));
        }

        var host = await RedeployAsync();
        (await LogsAsync("waiting for the open phone run")).ShouldBeTrue();
        LatestRunId().ShouldBe(firstRun, "the open run is left alone");
        ResultFiles().Count.ShouldBe(1);
        using var after = AdminOn(host);
        await StatusWhenAsync(after, s => s.GetProperty("grounded").GetInt32() == 1);
        await FinishOkAsync(after);
        var done = await StatusWhenDoneAsync(after);

        var summary = done.GetProperty("summary");
        summary.GetProperty("runId").GetString().ShouldBe(secondRun);
        summary.GetProperty("buildSha").GetString().ShouldBe(NewSha);
        IsRegrounded(summary).ShouldBeFalse("it is the phone's run");
        (await LogsAsync("was made by this server version")).ShouldBeTrue("the finished phone run is by this version, so there is nothing to re-ground");
        ResultFiles().Count.ShouldBe(2);
        (await KeptInputsAsync()).GetProperty("runId").GetString().ShouldBe(secondRun);
    }

    [Fact]
    public async Task A_run_opened_while_a_re_ground_is_going_stops_it_and_the_phone_run_goes_ahead()
    {
        StarFoxGolden("c1");
        var firstRun = await FirstRunAsync("c1");
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Games.Behaviour = async cancellationToken =>
        {
            asked.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        };
        var host = await RedeployAsync();
        await asked.Task.WaitAsync(Bounded); // the worker is grounding the kept readings
        using var phone = AdminOn(host);

        var secondRun = (await OpenOkAsync(phone)).GetProperty("runId").GetString()!;
        (await LogsAsync("stopped because a phone run was opened")).ShouldBeTrue();
        Games.Behaviour = null;
        await PutPhotoOkAsync(phone, "c1", AllEngines("c1", StarFoxRead));
        await FinishOkAsync(phone);
        var done = await StatusWhenDoneAsync(phone);

        var summary = done.GetProperty("summary");
        summary.GetProperty("runId").GetString().ShouldBe(secondRun);
        IsRegrounded(summary).ShouldBeFalse();
        ResultFiles().Count.ShouldBe(2, "the stopped re-ground wrote nothing: one file for each phone run");
        (await KeptInputsAsync()).GetProperty("runId").GetString().ShouldBe(secondRun);
    }

    [Fact]
    public async Task Without_kept_readings_nothing_is_re_grounded_so_a_result_from_before_the_readings_were_kept_is_left_alone()
    {
        await File.WriteAllTextAsync(Path.Combine(Golden.Results, "latest.json"), JsonSerializer.Serialize(new { runId = "old", buildSha = "0ld5ha" }));
        Golden.AddSingle("c1", StarFoxTruth);

        await RedeployAsync();

        (await LogsAsync("holds no phone readings")).ShouldBeTrue();
        LatestRunId().ShouldBe("old");
        ResultFiles().ShouldBeEmpty();
    }

    [Fact]
    public async Task Readings_that_belong_to_another_run_than_the_latest_result_are_not_re_grounded_over_it()
    {
        StarFoxGolden("c1");
        await FirstRunAsync("c1");
        var latest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Golden.Results, "latest.json")))!.AsObject();
        latest["runId"] = "somebody-elses-run";
        await File.WriteAllTextAsync(Path.Combine(Golden.Results, "latest.json"), latest.ToJsonString());

        await RedeployAsync();

        (await LogsAsync("belong to another run")).ShouldBeTrue();
        LatestRunId().ShouldBe("somebody-elses-run");
        ResultFiles().Count.ShouldBe(1);
    }

    [Fact]
    public async Task With_the_setting_off_a_server_change_does_not_re_ground_anything()
    {
        StarFoxGolden("c1");
        var firstRun = await FirstRunAsync("c1");

        await RedeployAsync(change: settings => settings["Evals:RegroundOnStart"] = "false");

        (await LogsAsync("switched off")).ShouldBeTrue();
        LatestRunId().ShouldBe(firstRun);
        ResultFiles().Count.ShouldBe(1);
    }

    // ---- the manual trigger -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_reground_request_file_makes_the_worker_re_ground_now_even_when_the_sha_is_unchanged_and_the_file_is_removed()
    {
        StarFoxGolden("c1");
        var firstRun = await FirstRunAsync("c1");

        await File.WriteAllTextAsync(RequestFile, string.Empty);
        Wake(Factory);
        var rescored = await NextLatestAfterAsync(firstRun);

        rescored.GetProperty("buildSha").GetString().ShouldBe(OldSha, "the code did not change, and it still ran");
        rescored.GetProperty("regroundedFrom").GetProperty("runId").GetString().ShouldBe(firstRun);
        File.Exists(RequestFile).ShouldBeFalse("the request is spent");
        ResultFiles().Count.ShouldBe(2, "a second result for the same sha is numbered, not overwritten");
    }

    [Fact]
    public async Task A_request_made_while_a_phone_run_is_open_waits_for_that_run_to_finish_and_is_then_honoured()
    {
        StarFoxGolden("c1");
        var firstRun = await FirstRunAsync("c1");
        using var phone = AdminClient();
        var secondRun = (await OpenOkAsync(phone)).GetProperty("runId").GetString()!;
        await File.WriteAllTextAsync(RequestFile, string.Empty);
        Wake(Factory);
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        File.Exists(RequestFile).ShouldBeTrue("an open run is never touched, so the request is kept for later");
        LatestRunId().ShouldBe(firstRun);
        await PutPhotoOkAsync(phone, "c1", AllEngines("c1", StarFoxRead));
        await FinishOkAsync(phone);
        await StatusWhenDoneAsync(phone);
        var rescored = await NextLatestAfterAsync(secondRun);

        rescored.GetProperty("regroundedFrom").GetProperty("runId").GetString().ShouldBe(secondRun, "the readings of the newest run");
        File.Exists(RequestFile).ShouldBeFalse();
    }

    [Fact]
    public async Task A_failed_re_ground_is_logged_by_the_type_of_its_error_only_and_is_not_tried_again_in_a_loop()
    {
        StarFoxGolden("c1");
        var attempts = 0;
        ConfigureServices = services =>
        {
            services.RemoveAll<IEvalResults>();
            services.AddSingleton<IEvalResults>(provider => new FailingResults(
                new FileEvalResults(provider.GetRequiredService<IOptions<EvalsOptions>>()), () => Interlocked.Increment(ref attempts)));
        };
        var firstRun = await FirstRunAsync("c1");

        var host = await RedeployAsync();
        (await LogsAsync("failed (InvalidOperationException)")).ShouldBeTrue();
        for (var pass = 0; pass < 5; pass++)
        {
            Wake(host);
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        attempts.ShouldBe(1, "one try per start");
        Logs.Lines.Where(line => line.Contains(LeakMarker, StringComparison.Ordinal)).ShouldBeEmpty("the error's message is never logged: it may name a path or a title");
        LatestRunId().ShouldBe(firstRun);

        await File.WriteAllTextAsync(RequestFile, string.Empty);
        Wake(host);
        (await BecomesTrueAsync(() => Volatile.Read(ref attempts) == 2, Bounded)).ShouldBeTrue("a request is another try");
    }

    /// <summary>The real results, except that saving a re-scored run fails with an error whose message names a path and a title.</summary>
    private sealed class FailingResults(IEvalResults inner, Action attempt) : IEvalResults
    {
        public bool IsConfigured => inner.IsConfigured;

        public Task SaveAsync(EvalSummary summary, GroundedRun run, string reportMarkdown, CancellationToken cancellationToken)
        {
            if (summary.RegroundedFrom is null)
            {
                return inner.SaveAsync(summary, run, reportMarkdown, cancellationToken);
            }

            attempt();
            throw new InvalidOperationException($"{LeakMarker} C:\\evals\\results\\Star Fox 64");
        }

        public EvalSummary? LoadLatestSummary() => inner.LoadLatestSummary();

        public GroundedRun? LoadLatestRun() => inner.LoadLatestRun();

        public IReadOnlyDictionary<string, double>? LoadBaseline() => inner.LoadBaseline();

        public EvalBaselineDoc? LoadBaselineDoc() => inner.LoadBaselineDoc();

        public bool TryWriteDraft(string id, string json) => inner.TryWriteDraft(id, json);
    }
}
