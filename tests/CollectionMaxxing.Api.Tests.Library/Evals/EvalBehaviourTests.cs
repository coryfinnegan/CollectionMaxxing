using System.Net;
using System.Text;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Scanning;
using CollectionMaxxing.Api.Tests.Scanning.Identify;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport.Evals;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Evals;

/// <summary>What the eval server does with a run: the dry-run grounding, the files it writes, and when it is off.</summary>
public class EvalBehaviourTests(ApiFixture api) : EvalTest(api)
{
    private static readonly SyntheticGolden.SingleItem StarFoxTruth =
        new("Star Fox 64", "game_cartridge", "cart_only", "loose_cartridge", "Nintendo 64");

    private static object StarFoxRead(string? resolvedBy = null) =>
        ReadItem("Star Fox 64", "game", "game_cartridge", "Nintendo 64", "cart_only", resolvedBy: resolvedBy);

    private async Task<int> CountAsync(string table)
    {
        await using var context = Db.CreateContext();
#pragma warning disable EF1002 // The table names are the constants of this file's own tests.
        return await context.Database.SqlQueryRaw<int>($"""select count(*)::int as "Value" from {table}""").SingleAsync();
#pragma warning restore EF1002
    }

    private async Task<JsonElement> LatestRunFileAsync()
    {
        var text = await File.ReadAllTextAsync(Path.Combine(Golden.Results, "latest-run.json"));
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    // ---- dry-run grounding -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Dry_run_grounding_persists_nothing_and_gives_the_same_candidates_as_identify_for_the_same_read()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        Games.Results.Add(ScanData.Game("Star Fox 64 Bonus Edition", "Nintendo 64", 1997, "g2"));
        var library = await CreateLibraryAsync();
        using var admin = AdminClient();

        await PostRunOkAsync(admin, RunBody(PhotoResult("c1", "apple_model", [StarFoxRead()])));

        (await CountAsync("scan_sessions")).ShouldBe(0);
        (await CountAsync("scan_snaps")).ShouldBe(0);
        (await CountAsync("scan_candidates")).ShouldBe(0);
        (await CountAsync("releases")).ShouldBe(0); // a provider's answer is not added to the catalogue by an eval
        (await CountAsync("works")).ShouldBe(0);
        (await CountAsync("jobs")).ShouldBe(0); // no safety-net or artwork job either

        var grounded = (await LatestRunFileAsync()).GetProperty("photos")[0].GetProperty("items")[0];
        var dryRun = grounded.GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("title").GetString()!).ToList();
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);
        var identified = await IdentifyOkAsync(
            viewer, sessionId, Read(title: "Star Fox 64", kind: "game", format: "game_cartridge", platform: "Nintendo 64", completeness: "cart_only"));

        dryRun.ShouldNotBeEmpty();
        grounded.GetProperty("verdict").GetString().ShouldBe(Resolution(identified[0]));
        dryRun.ShouldBe(AlternativeTitles(identified[0]));
    }

    // ---- one candidate per book per photo ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_repeat_read_of_one_book_in_a_photo_is_kept_once_with_the_stronger_read(bool repeatFirst)
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        using var admin = AdminClient();
        var repeat = ReadItem("Star Fox 64 FREDEROCKSOH", "game", "game_cartridge", "Nintendo 64", "cart_only");

        await PostRunOkAsync(admin, RunBody(PhotoResult("c1", "apple_model", repeatFirst ? [repeat, StarFoxRead()] : [StarFoxRead(), repeat])));

        var items = (await LatestRunFileAsync()).GetProperty("photos")[0].GetProperty("items");
        items.GetArrayLength().ShouldBe(1, "the same release read twice is one book");
        items[0].GetProperty("verdict").GetString().ShouldBe("matched");
        items[0].GetProperty("readTitle").GetString().ShouldBe("Star Fox 64");
    }

    [Fact]
    public async Task Reads_of_two_different_books_in_a_photo_are_both_kept()
    {
        Golden.AddShelf("s1", [new("Star Fox 64", "game_cartridge", "Nintendo 64"), new("Wave Race 64", "game_cartridge", "Nintendo 64")]);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        Games.Results.Add(ScanData.Game("Wave Race 64", "Nintendo 64", 1996, "g2"));
        using var admin = AdminClient();

        await PostRunOkAsync(admin, RunBody(PhotoResult("s1", "apple_model", [
            StarFoxRead(),
            ReadItem("Wave Race 64", "game", "game_cartridge", "Nintendo 64", "cart_only")])));

        var items = (await LatestRunFileAsync()).GetProperty("photos")[0].GetProperty("items");
        items.GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task A_run_scores_the_grounded_reading_against_the_truth_and_reports_the_gate_and_the_regression_check()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        using var admin = AdminClient();

        var response = await PostRunOkAsync(admin, RunBody(PhotoResult("c1", "apple_model", [StarFoxRead("model")], latencyMs: 1800)));

        var summary = response.GetProperty("summary");
        response.GetProperty("runId").GetString().ShouldBe(summary.GetProperty("runId").GetString());
        summary.GetProperty("scoredPhotos").GetInt32().ShouldBe(1);
        var single = summary.GetProperty("engines").GetProperty("apple_model").GetProperty("single");
        single.GetProperty("top1Pct").GetDouble().ShouldBe(100);
        single.GetProperty("precisionPct").GetDouble().ShouldBe(100);
        single.GetProperty("inventedTitles").GetInt32().ShouldBe(0);
        single.GetProperty("latencyP50Ms").GetInt64().ShouldBe(1800);
        single.GetProperty("completenessAccuracyPct").GetDouble().ShouldBe(100);
        single.GetProperty("resolvedBy").GetProperty("model").GetInt32().ShouldBe(1);
        summary.GetProperty("gate").GetProperty("verdict").GetString().ShouldBe("incomplete"); // one photo cannot cover every measure
        summary.GetProperty("gate").GetProperty("measures").GetArrayLength().ShouldBeGreaterThan(5);
        summary.GetProperty("regression").GetProperty("status").GetString().ShouldBe("no_baseline");
        ResultFiles().Count.ShouldBe(1);
        File.Exists(Path.Combine(Golden.Results, "report.md")).ShouldBeTrue();
        (await File.ReadAllTextAsync(Path.Combine(Golden.Results, "report.md"))).ShouldContain("M3 gate");
    }

    [Fact]
    public async Task A_timeout_is_accepted_counts_in_the_latency_percentiles_and_shows_as_its_own_rate_in_the_gate_table()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Golden.AddSingle("c2", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        using var admin = AdminClient();

        var response = await PostRunOkAsync(admin, RunBody(
            PhotoResult("c1", "apple_model", [StarFoxRead("model")], latencyMs: 1000),
            PhotoResult("c2", "apple_model", [], outcome: "timeout", latencyMs: 8200, firstItemMs: null, fellBackFrom: "timeout")));

        var summary = response.GetProperty("summary");
        var single = summary.GetProperty("engines").GetProperty("apple_model").GetProperty("single");
        single.GetProperty("timeouts").GetInt32().ShouldBe(1);
        single.GetProperty("timeoutPct").GetDouble().ShouldBe(50);
        single.GetProperty("latencyP95Ms").GetInt64().ShouldBe(8200);
        single.GetProperty("failures").GetInt32().ShouldBe(0);
        var gate = summary.GetProperty("gate").GetProperty("measures").EnumerateArray().Single(m => m.GetProperty("name").GetString() == "model_timeouts");
        gate.GetProperty("actual").GetString().ShouldBe("50%");
    }

    [Fact]
    public async Task An_invented_title_that_survives_grounding_is_counted()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Zorblax Quest 64", "Nintendo 64", 1997, "g9"));
        using var admin = AdminClient();

        var response = await PostRunOkAsync(admin, RunBody(PhotoResult("c1", "apple_model", [ReadItem("Zorblax Quest 64", "game", "game_cartridge", "Nintendo 64")])));

        var single = response.GetProperty("summary").GetProperty("engines").GetProperty("apple_model").GetProperty("single");
        single.GetProperty("inventedTitles").GetInt32().ShouldBe(1);
        single.GetProperty("top1Pct").GetDouble().ShouldBe(0);
    }

    // ---- path traversal (more spellings than the locked theory) ------------------------------------------------------------

    [Theory]
    [InlineData("..%5C..%5Csecret")] // ..\..\secret: from single/ that is <golden root>/secret.jpg
    [InlineData("..%5c..%5csecret")] // the same with lower-case escapes
    [InlineData("..%2f..%2fsecret")]
    [InlineData("%2e%2e%5c%2e%2e%5csecret")]
    public async Task A_backslash_traversal_id_never_reaches_a_file_outside_the_golden_folder(string id)
    {
        Golden.AddSingle("c1", StarFoxTruth);
        await File.WriteAllBytesAsync(Path.Combine(Golden.Root, "secret.jpg"), Encoding.ASCII.GetBytes("OUTSIDE-CANARY"));
        using var admin = AdminClient();

        using var response = await admin.GetAsync($"{Base}/golden/{id}/image");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).ShouldNotContain("OUTSIDE-CANARY");
    }

    [Theory]
    [InlineData("7")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("\"7\"")]
    [InlineData("\"AppleModel\"")]
    public async Task An_engine_that_is_not_a_known_engine_name_is_refused_and_nothing_is_written(string engineJson)
    {
        Golden.AddSingle("c1", StarFoxTruth);
        var body = $$"""{"device":{"model":"m","osVersion":"1","appVersion":"1"},"configVersion":"c","results":[{"photoId":"c1","engine":{{engineJson}},"outcome":"ok","items":[],"latencyMs":1,"thermalState":"nominal"}]}""";
        using var admin = AdminClient();

        using var response = await admin.PostAsync($"{Base}/runs", new StringContent(body, Encoding.UTF8, "application/json"));

        ((int)response.StatusCode).ShouldBe(400, await response.Content.ReadAsStringAsync());
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        SyntheticGolden.Files(Golden.Results).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_fm_text_result_is_accepted_and_scored_under_its_own_engine()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        using var admin = AdminClient();

        var response = await PostRunOkAsync(admin, RunBody(PhotoResult("c1", "fm_text", [StarFoxRead()])));

        var single = response.GetProperty("summary").GetProperty("engines").GetProperty("fm_text").GetProperty("single");
        single.GetProperty("top1Pct").GetDouble().ShouldBe(100);
        // An unspoken step defaults to the engine that read it: fm_text reads are settled by the model.
        single.GetProperty("resolvedBy").GetProperty("model").GetInt32().ShouldBe(1);
    }

    // ---- truth, drafts and what is skipped -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_photo_without_truth_is_skipped_and_listed_and_gets_a_draft_that_is_never_scored()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Golden.AddSingle("c2", null);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        using var admin = AdminClient();

        var response = await PostRunOkAsync(admin, RunBody(
            PhotoResult("c1", "apple_model", [StarFoxRead()]),
            PhotoResult("c2", "apple_model", [StarFoxRead()]),
            PhotoResult("ghost", "apple_model", [StarFoxRead()])));

        var summary = response.GetProperty("summary");
        summary.GetProperty("scoredPhotos").GetInt32().ShouldBe(1);
        summary.GetProperty("skipped").EnumerateArray().Select(s => $"{s.GetProperty("photoId").GetString()}:{s.GetProperty("reason").GetString()}")
            .Order().ShouldBe(["c2:no_truth", "ghost:unknown_photo"]);
        var draft = Path.Combine(Golden.Golden, "single", "c2.truth.draft.json");
        File.Exists(draft).ShouldBeTrue();
        var draftJson = JsonDocument.Parse(await File.ReadAllTextAsync(draft)).RootElement;
        draftJson.GetProperty("draft").GetBoolean().ShouldBeTrue();
        draftJson.GetProperty("title").GetString().ShouldBe("Star Fox 64");
        File.Exists(Path.Combine(Golden.Golden, "single", "c2.truth.json")).ShouldBeFalse();

        var list = JsonDocument.Parse(await admin.GetStringAsync($"{Base}/golden")).RootElement.EnumerateArray().ToList();
        list.Single(p => p.GetProperty("id").GetString() == "c2").GetProperty("hasTruth").GetBoolean().ShouldBeFalse();
        list.Single(p => p.GetProperty("id").GetString() == "c1").GetProperty("hasTruth").GetBoolean().ShouldBeTrue();
        list.Single(p => p.GetProperty("id").GetString() == "c1").GetProperty("set").GetString().ShouldBe("single");
        list.Single(p => p.GetProperty("id").GetString() == "c1").GetProperty("bytes").GetInt64().ShouldBe(SyntheticGolden.PictureBytes.Length);
    }

    [Fact]
    public async Task A_draft_the_owner_has_edited_is_not_overwritten_by_the_next_run()
    {
        Golden.AddSingle("c2", null);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        using var admin = AdminClient();
        await PostRunOkAsync(admin, RunBody(PhotoResult("c2", "apple_model", [StarFoxRead()])));
        var draft = Path.Combine(Golden.Golden, "single", "c2.truth.draft.json");
        await File.WriteAllTextAsync(draft, "{ \"title\": \"MY CORRECTION\" }");

        await PostRunOkAsync(admin, RunBody(PhotoResult("c2", "apple_model", [StarFoxRead()])));

        (await File.ReadAllTextAsync(draft)).ShouldContain("MY CORRECTION");
    }

    [Fact]
    public async Task When_a_draft_cannot_be_written_beside_the_picture_it_is_kept_in_the_results_folder_instead()
    {
        Golden.AddSingle("c2", null);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        // A folder by the draft's name cannot be replaced by a file: it stands in for a golden folder mounted read-only.
        Directory.CreateDirectory(Path.Combine(Golden.Golden, "single", "c2.truth.draft.json"));
        using var admin = AdminClient();

        await PostRunOkAsync(admin, RunBody(PhotoResult("c2", "apple_model", [StarFoxRead()])));

        File.Exists(Path.Combine(Golden.Results, "drafts", "c2.truth.draft.json")).ShouldBeTrue();
    }

    [Fact]
    public async Task A_truth_file_that_is_not_valid_truth_skips_the_photo_instead_of_scoring_half_of_it()
    {
        Golden.AddSingle("c1", null);
        Golden.AddFile("single", "c1.truth.json", "{ \"title\": ");
        using var admin = AdminClient();

        var response = await PostRunOkAsync(admin, RunBody(PhotoResult("c1", "apple_model", [StarFoxRead()])));

        var summary = response.GetProperty("summary");
        summary.GetProperty("scoredPhotos").GetInt32().ShouldBe(0);
        summary.GetProperty("skipped")[0].GetProperty("reason").GetString().ShouldBe("invalid_truth");
    }

    [Fact]
    public async Task Sending_the_same_photo_twice_for_one_engine_is_refused()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        using var admin = AdminClient();

        var response = await PostRunAsync(admin, RunBody(PhotoResult("c1", "apple_model", []), PhotoResult("c1", "apple_model", [])));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        SyntheticGolden.Files(Golden.Results).ShouldBeEmpty();
    }

    // ---- latest run and the baseline ---------------------------------------------------------------------------------------

    [Fact]
    public async Task The_latest_run_is_not_found_until_one_is_uploaded_and_then_returns_its_summary()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        using var admin = AdminClient();

        (await admin.GetAsync($"{Base}/runs/latest")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var posted = await PostRunOkAsync(admin, RunBody(PhotoResult("c1", "apple_model", [StarFoxRead()])));
        var latest = await ReadJsonAsync(await admin.GetAsync($"{Base}/runs/latest"));

        latest.GetProperty("runId").GetString().ShouldBe(posted.GetProperty("runId").GetString());
        latest.GetProperty("gate").GetProperty("engine").GetString().ShouldBe("apple_vision"); // the row the gate measures since ADR-032: what the app shows
    }

    [Fact]
    public async Task A_run_more_than_2_points_worse_than_the_baseline_file_is_flagged()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        var baseline = Path.Combine(Golden.Root, "baseline.json");
        await File.WriteAllTextAsync(baseline, """{ "metrics": { "apple_model.single.top1": 100, "apple_model.single.precision": 100 } }""");
        Settings["Evals:BaselinePath"] = baseline;
        using var admin = AdminClient();

        var response = await PostRunOkAsync(admin, RunBody(PhotoResult("c1", "apple_model", [ReadItem("Nothing Like It")])));

        var regression = response.GetProperty("summary").GetProperty("regression");
        regression.GetProperty("status").GetString().ShouldBe("regressed");
        regression.GetProperty("regressions").EnumerateArray().Select(r => r.GetProperty("metric").GetString()).ShouldContain("apple_model.single.top1");
    }

    // ---- when it is off --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("false", "golden")]
    [InlineData("true", "missing-folder")]
    [InlineData("true", "")]
    public async Task When_the_evals_are_off_or_there_is_no_golden_folder_every_route_is_not_found(string enabled, string golden)
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Settings["Evals:Enabled"] = enabled;
        Settings["Evals:GoldenPath"] = golden switch
        {
            "golden" => Golden.Golden,
            "missing-folder" => Path.Combine(Golden.Root, "no-such-folder"),
            _ => string.Empty,
        };
        using var admin = AdminClient();

        (await admin.GetAsync($"{Base}/golden")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.GetAsync($"{Base}/golden/c1/image")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.GetAsync($"{Base}/runs/latest")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await PostRunAsync(admin, RunBody(PhotoResult("c1", "apple_model", [StarFoxRead()])))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.PostAsync($"{Base}/runs", new StringContent("not json", Encoding.UTF8, "application/json"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        SyntheticGolden.Files(Golden.Results).ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_a_results_folder_a_run_is_refused_with_a_problem_and_nothing_is_scored()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Settings["Evals:ResultsPath"] = string.Empty;
        using var admin = AdminClient();

        var response = await PostRunAsync(admin, RunBody(PhotoResult("c1", "apple_model", [StarFoxRead()])));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    // ---- the honest fields (docs/16 §17) ------------------------------------------------------------------------------------

    private static object TieredStarFoxRead() => new
    {
        title = "Star Fox 64",
        text = "Star Fox 64",
        kind = "game",
        format = "game_cartridge",
        platform = "Nintendo 64",
        completeness = "cart_only",
        resolvedBy = "model",
        tier = "T1",
        segmentId = "seg-1",
    };

    [Fact]
    public async Task The_honest_fields_ride_through_grounding_into_the_summary_and_the_kept_run()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        using var admin = AdminClient();
        var body = new Dictionary<string, object?>
        {
            ["device"] = new { model = "iPhone18,1", osVersion = "26.0", appVersion = "0.3.0" },
            ["configVersion"] = "cfg-test-1",
            ["baselineNote"] = "acceptance re-run under the new T1 bar",
            ["results"] = new[]
            {
                new
                {
                    photoId = "c1",
                    engine = "apple_model",
                    outcome = "ok",
                    items = new[] { TieredStarFoxRead() },
                    unreadableCount = 0,
                    latencyMs = 1800L,
                    firstItemMs = 800L,
                    thermalState = "nominal",
                    capturedCount = 1,
                    capturedAt = "2026-10-24T09:00:00Z",
                    duplicateRescan = new { ownedHits = 2, answeredOnCard = 2, unsolicitedAdds = 0 },
                },
            },
        };

        var response = await PostRunOkAsync(admin, body);

        var summary = response.GetProperty("summary");
        summary.GetProperty("baselineNote").GetString().ShouldBe("acceptance re-run under the new T1 bar");
        summary.GetProperty("thresholdHash").GetString()!.Length.ShouldBe(16);
        var single = summary.GetProperty("engines").GetProperty("apple_model").GetProperty("single");
        single.GetProperty("t1Auto").GetInt32().ShouldBe(1);
        single.GetProperty("autoAdds").GetInt32().ShouldBe(1);
        single.GetProperty("correctAutoAdds").GetInt32().ShouldBe(1);
        single.GetProperty("autoAddPrecisionPct").GetDouble().ShouldBe(100);
        single.GetProperty("reviewRatePct").GetDouble().ShouldBe(0);
        single.GetProperty("duplicateClean").GetBoolean().ShouldBeTrue();
        single.GetProperty("timeToFirstResultsMs").GetInt64().ShouldBe(1800);
        single.GetProperty("totalTimeToTrustworthyMs").GetInt64().ShouldBe(1800);
        single.GetProperty("unreconciledPhotos").GetInt32().ShouldBe(0);
        summary.GetProperty("metrics").GetProperty("apple_model.single.auto_add_precision").GetDouble().ShouldBe(100);

        var grounded = (await LatestRunFileAsync()).GetProperty("photos")[0];
        grounded.GetProperty("capturedCount").GetInt32().ShouldBe(1);
        grounded.GetProperty("items")[0].GetProperty("tier").GetString().ShouldBe("T1");
        grounded.GetProperty("items")[0].GetProperty("segmentId").GetString().ShouldBe("seg-1");
    }

    [Fact]
    public async Task The_kept_run_records_what_was_read_and_the_terms_behind_each_release_score()
    {
        // Third eval run: right releases scored about 0.8 and the kept run could not say which term pulled them down.
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        using var admin = AdminClient();

        await PostRunOkAsync(
            admin, RunBody(PhotoResult("c1", "apple_model", [ReadItem("Star Fox 64", platform: "Nintendo 64", year: 1997, creator: "STAR FOX")])));

        var item = (await LatestRunFileAsync()).GetProperty("photos")[0].GetProperty("items")[0];
        var read = item.GetProperty("read");
        read.GetProperty("kind").GetString().ShouldBe("game");
        read.GetProperty("format").GetString().ShouldBe("game_cartridge");
        read.GetProperty("platform").GetString().ShouldBe("Nintendo 64");
        read.GetProperty("year").GetInt32().ShouldBe(1997);
        read.GetProperty("profile").GetString().ShouldNotBeNullOrWhiteSpace();
        read.GetProperty("creator").ValueKind.ShouldBe(JsonValueKind.Null); // cleaned: it only repeats the title
        read.GetProperty("creatorAsRead").GetString().ShouldBe("STAR FOX");
        var terms = item.GetProperty("candidates")[0].GetProperty("terms").EnumerateArray()
            .ToDictionary(t => t.GetProperty("name").GetString()!);
        terms.Keys.ShouldBe(["title", "type", "platform", "year"], ignoreOrder: true);
        terms["title"].GetProperty("value").GetDouble().ShouldBe(1.0);
        terms["title"].GetProperty("compared").GetBoolean().ShouldBeTrue();
        terms["title"].GetProperty("weight").GetDouble().ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData("\"tier\":7", "evals.tier_invalid")]
    [InlineData("\"tier\":\"T9\"", "evals.body_invalid")]
    public async Task A_tier_that_is_not_T0_T1_or_T2_is_refused(string tierJson, string code)
    {
        Golden.AddSingle("c1", StarFoxTruth);
        var body = $$"""{"device":{"model":"m","osVersion":"1","appVersion":"1"},"configVersion":"c","results":[{"photoId":"c1","engine":"apple_model","outcome":"ok","items":[{"title":"Star Fox 64","text":"Star Fox 64","kind":"game","format":"game_cartridge","platform":"Nintendo 64","completeness":"cart_only",{{tierJson}}}],"latencyMs":1,"thermalState":"nominal"}]}""";
        using var admin = AdminClient();

        using var response = await admin.PostAsync($"{Base}/runs", new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain(code);
        SyntheticGolden.Files(Golden.Results).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("capturedCount", "-1", "evals.captured_invalid")]
    [InlineData("capturedCount", "1001", "evals.captured_invalid")]
    public async Task A_captured_count_outside_0_to_1000_is_refused(string field, string value, string code)
    {
        Golden.AddSingle("c1", StarFoxTruth);
        var body = $$"""{"device":{"model":"m","osVersion":"1","appVersion":"1"},"configVersion":"c","results":[{"photoId":"c1","engine":"apple_model","outcome":"ok","items":[],"{{field}}":{{value}},"latencyMs":1,"thermalState":"nominal"}]}""";
        using var admin = AdminClient();

        using var response = await admin.PostAsync($"{Base}/runs", new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain(code);
        SyntheticGolden.Files(Golden.Results).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(2, 3, 0)] // more answered than hit
    [InlineData(-1, 0, 0)] // negative hits
    [InlineData(2, 2, -1)] // negative adds
    public async Task Duplicate_re_scan_evidence_that_cannot_be_true_is_refused(int owned, int answered, int unsolicited)
    {
        Golden.AddSingle("c1", StarFoxTruth);
        var body = """{"device":{"model":"m","osVersion":"1","appVersion":"1"},"configVersion":"c","results":[{"photoId":"c1","engine":"apple_model","outcome":"ok","items":[],"latencyMs":1,"thermalState":"nominal","duplicateRescan":{"ownedHits":"""
            + owned + ""","answeredOnCard":""" + answered + ""","unsolicitedAdds":""" + unsolicited + """}}]}""";
        using var admin = AdminClient();

        using var response = await admin.PostAsync($"{Base}/runs", new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("evals.duplicate_invalid");
        SyntheticGolden.Files(Golden.Results).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_baseline_note_over_500_characters_is_refused()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        using var admin = AdminClient();
        var body = RunBody(PhotoResult("c1", "apple_model", []));
        body["baselineNote"] = new string('n', 501);

        using var response = await PostRunAsync(admin, body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("evals.note_too_long");
        SyntheticGolden.Files(Golden.Results).ShouldBeEmpty();
    }

    // ---- the OCR lines the Vision engine split ---------------------------------------------------------------------------------

    private static bool Absent(JsonElement photo, string name) => !photo.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null;

    [Fact]
    public async Task The_Vision_engines_OCR_lines_and_the_pieces_it_made_are_kept_in_the_grounded_run()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        using var admin = AdminClient();

        await PostRunOkAsync(admin, RunBody(PhotoResult(
            "c1", "vision_pipeline", [StarFoxRead()],
            ocrLines: [OcrLine("Star Fox 64", 0.2, 0.1, 0.05, 0.6), OcrLine("Nintendo", 0.3, 0.7, 0.4, 0.05)],
            ocrPieces: [[0, 1]])));

        var photo = (await LatestRunFileAsync()).GetProperty("photos")[0];
        var lines = photo.GetProperty("ocrLines");
        lines.GetArrayLength().ShouldBe(2);
        lines[0].GetProperty("text").GetString().ShouldBe("Star Fox 64");
        lines[0].GetProperty("x").GetDouble().ShouldBe(0.2);
        lines[0].GetProperty("y").GetDouble().ShouldBe(0.1);
        lines[0].GetProperty("width").GetDouble().ShouldBe(0.05);
        lines[0].GetProperty("height").GetDouble().ShouldBe(0.6);
        lines[1].GetProperty("text").GetString().ShouldBe("Nintendo");
        photo.GetProperty("ocrPieces").EnumerateArray().Select(p => p.EnumerateArray().Select(i => i.GetInt32()).ToArray()).ShouldBe([[0, 1]]);
    }

    [Fact]
    public async Task OCR_lines_stay_out_of_the_dated_result_file_that_is_safe_to_commit()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        using var admin = AdminClient();

        await PostRunOkAsync(admin, RunBody(PhotoResult("c1", "vision_pipeline", [StarFoxRead()], ocrLines: [OcrLine("Zebra Quartz Lantern")], ocrPieces: [[0]])));

        (await File.ReadAllTextAsync(Path.Combine(Golden.Results, "latest-run.json"))).ShouldContain("Zebra Quartz Lantern");
        foreach (var file in ResultFiles().Append(Path.Combine(Golden.Results, "latest.json")))
        {
            (await File.ReadAllTextAsync(file)).ShouldNotContain("Zebra Quartz Lantern");
        }
    }

    [Theory]
    [InlineData("apple_model")]
    [InlineData("fm_text")]
    public async Task OCR_lines_sent_with_another_engines_photo_are_ignored(string engine)
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        using var admin = AdminClient();
        object[] tooMany = [.. Enumerable.Range(0, 1001).Select(i => OcrLine($"Line {i}"))];

        await PostRunOkAsync(admin, RunBody(PhotoResult("c1", engine, [StarFoxRead()], ocrLines: tooMany, ocrPieces: [[0]])));

        var photo = (await LatestRunFileAsync()).GetProperty("photos")[0];
        Absent(photo, "ocrLines").ShouldBeTrue();
        Absent(photo, "ocrPieces").ShouldBeTrue();
    }

    [Fact]
    public async Task A_Vision_photo_without_OCR_lines_is_accepted_as_before()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        using var admin = AdminClient();

        await PostRunOkAsync(admin, RunBody(PhotoResult("c1", "vision_pipeline", [StarFoxRead()])));

        Absent((await LatestRunFileAsync()).GetProperty("photos")[0], "ocrLines").ShouldBeTrue();
    }

    [Fact]
    public async Task A_thousand_OCR_lines_are_kept()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        using var admin = AdminClient();
        object[] lines = [.. Enumerable.Range(0, 1000).Select(i => OcrLine($"Line {i}"))];

        await PostRunOkAsync(admin, RunBody(PhotoResult("c1", "vision_pipeline", [], ocrLines: lines, ocrPieces: [[0, 999]])));

        (await LatestRunFileAsync()).GetProperty("photos")[0].GetProperty("ocrLines").GetArrayLength().ShouldBe(1000);
    }

    [Theory]
    [InlineData("evals.ocr_too_many_lines", "count")]
    [InlineData("evals.ocr_line_invalid", "long_text")]
    [InlineData("evals.ocr_line_invalid", "x_over_one")] // 1.5: values within 0.01 of 0 to 1 are clamped instead (EvalBadPhotoTests)
    [InlineData("evals.ocr_line_invalid", "y_below_zero")]
    [InlineData("evals.ocr_line_invalid", "width_over_one")]
    [InlineData("evals.ocr_line_invalid", "no_height")]
    [InlineData("evals.ocr_line_invalid", "no_text")]
    [InlineData("evals.ocr_piece_invalid", "piece_past_the_lines")]
    [InlineData("evals.ocr_piece_invalid", "piece_negative")]
    [InlineData("evals.ocr_piece_invalid", "piece_without_lines")]
    public async Task OCR_lines_over_the_limits_are_refused_and_nothing_is_written(string code, string problem)
    {
        Golden.AddSingle("c1", StarFoxTruth);
        using var admin = AdminClient();
        object[] lines = problem switch
        {
            "count" => [.. Enumerable.Range(0, 1001).Select(i => OcrLine($"Line {i}"))],
            "long_text" => [OcrLine(new string('t', 201))],
            "x_over_one" => [OcrLine("Dune", x: 1.5)],
            "y_below_zero" => [OcrLine("Dune", y: -0.5)],
            "width_over_one" => [OcrLine("Dune", width: 2)],
            "no_height" => [new { text = "Dune", x = 0.1, y = 0.1, width = 0.1 }],
            "no_text" => [new { x = 0.1, y = 0.1, width = 0.1, height = 0.1 }],
            "piece_without_lines" => [],
            _ => [OcrLine("Dune")],
        };
        int[][] pieces = problem switch
        {
            "piece_past_the_lines" => [[0, 1]],
            "piece_negative" => [[-1]],
            "piece_without_lines" => [[0]],
            _ => [],
        };

        using var response = await PostRunAsync(admin, RunBody(PhotoResult("c1", "vision_pipeline", [], ocrLines: lines, ocrPieces: pieces)));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain(code);
        SyntheticGolden.Files(Golden.Results).ShouldBeEmpty();
    }
}
