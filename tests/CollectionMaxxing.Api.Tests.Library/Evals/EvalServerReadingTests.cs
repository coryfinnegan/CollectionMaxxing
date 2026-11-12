using System.Text.Json;
using CollectionMaxxing.Api.Tests.Scanning;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.ShelfReading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CollectionMaxxing.Api.Tests.Evals;

/// <summary>
/// The server reads the golden photos itself (ADR-033): a re-ground of the kept phone readings adds the rows <c>server_model</c> and <c>server_vision</c>
/// and moves nothing that existed, a second one reads nothing again, and the one-shot upload, which runs inside a request, never asks the reader.
/// A fake reader stands in for the model; real Postgres through the app's own database, pictures drawn for the test.
/// </summary>
public class EvalServerReadingTests(ApiFixture api) : RegroundTest(api)
{
    private const string ReaderEndpoint = "http://127.0.0.1:1"; // never called: the fake replaces the reader

    private sealed class CountingReader(Func<ShelfReadResult> answer) : IShelfReader
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<ShelfReadResult> ReadAsync(byte[] jpeg, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(answer());
        }
    }

    private static ShelfReadResult StarFox() => new([new ShelfReadItem("Star Fox 64", "", ShelfItemKind.Game)], ShelfReadStatus.Ok);

    private void StarFoxGolden(params string[] photoIds)
    {
        foreach (var id in photoIds)
        {
            Golden.AddSingle(id, StarFoxTruth);
        }

        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
    }

    private void UseReader(CountingReader reader) =>
        AddConfigureServices(services =>
        {
            services.RemoveAll<IShelfReader>();
            services.AddSingleton<IShelfReader>(reader);
        });

    private static void SetUpReader(Dictionary<string, string?> settings)
    {
        settings["ShelfReader:Endpoint"] = ReaderEndpoint;
        settings["ShelfReader:Model"] = "example-vl";
    }

    private string CacheFolder => Path.Combine(Golden.Results, "reader-cache");

    private async Task<string> FirstRunAsync(params string[] photoIds)
    {
        using var phone = AdminClient();
        return await PhoneRunAsync(phone, photoIds);
    }

    private static string RowsOf(JsonElement summary, Func<string, bool> keep) =>
        string.Join("|", summary.GetProperty("engines").EnumerateObject().Where(e => keep(e.Name)).Select(e => e.Name + "=" + e.Value.GetRawText()));

    private static bool IsOld(string engine) => engine is not ("server_model" or "server_vision");

    [Fact]
    public async Task A_re_ground_with_a_reader_adds_the_server_rows_and_every_old_row_the_gate_and_the_counts_stay_identical()
    {
        StarFoxGolden("c1");
        var firstRun = await FirstRunAsync("c1");
        var original = TryLatestSummary()!.Value;
        RowsOf(original, _ => true).ShouldNotContain("server_");
        var reader = new CountingReader(StarFox);
        UseReader(reader);

        await RedeployAsync(change: SetUpReader);
        var rescored = await NextLatestAfterAsync(firstRun);

        var engines = rescored.GetProperty("engines");
        engines.TryGetProperty("server_model", out var model).ShouldBeTrue();
        engines.TryGetProperty("server_vision", out _).ShouldBeTrue();
        model.GetProperty("single").GetProperty("photos").GetInt32().ShouldBe(1);
        RowsOf(rescored, IsOld).ShouldBe(RowsOf(original, IsOld));
        rescored.GetProperty("gate").GetRawText().ShouldBe(original.GetProperty("gate").GetRawText());
        rescored.GetProperty("scoredPhotos").GetInt32().ShouldBe(original.GetProperty("scoredPhotos").GetInt32());
        rescored.GetProperty("regression").GetProperty("status").GetString().ShouldBe(original.GetProperty("regression").GetProperty("status").GetString());
        reader.Calls.ShouldBe(1);
        (await File.ReadAllTextAsync(Path.Combine(Golden.Results, "report.md"))).ShouldContain("Outside the gate");
    }

    [Fact]
    public async Task A_second_re_ground_of_the_same_photos_makes_no_reader_call()
    {
        StarFoxGolden("c1");
        var firstRun = await FirstRunAsync("c1");
        var reader = new CountingReader(StarFox);
        UseReader(reader);
        var host = await RedeployAsync(change: SetUpReader);
        var second = (await NextLatestAfterAsync(firstRun)).GetProperty("runId").GetString()!;
        reader.Calls.ShouldBe(1);
        Directory.GetFiles(CacheFolder).Length.ShouldBe(1);

        await File.WriteAllTextAsync(Path.Combine(Golden.Results, "reground.request"), string.Empty);
        Wake(host);
        var third = await NextLatestAfterAsync(second);

        third.GetProperty("engines").TryGetProperty("server_model", out _).ShouldBeTrue();
        reader.Calls.ShouldBe(1, "the photo was read before, with the same settings");
    }

    [Fact]
    public async Task A_failed_read_is_an_error_row_that_is_not_kept_and_does_not_make_the_run_partial()
    {
        StarFoxGolden("c1");
        var firstRun = await FirstRunAsync("c1");
        var reader = new CountingReader(() => ShelfReadResult.Empty(ShelfReadStatus.Unavailable));
        UseReader(reader);

        await RedeployAsync(change: SetUpReader);
        var rescored = await NextLatestAfterAsync(firstRun);

        rescored.GetProperty("partial").GetBoolean().ShouldBeFalse();
        var row = (await LatestRunFileAsync()).GetProperty("photos").EnumerateArray().Single(p => p.GetProperty("engine").GetString() == "server_model");
        row.GetProperty("outcome").GetString().ShouldBe("error");
        row.GetProperty("items").GetArrayLength().ShouldBe(0);
        Directory.Exists(CacheFolder).ShouldBeFalse("a failed read is not cached");
    }

    [Fact]
    public async Task A_live_phone_run_never_waits_on_the_reader_and_shows_the_server_rows_only_from_a_warm_cache()
    {
        StarFoxGolden("c1");
        var reader = new CountingReader(StarFox);
        UseReader(reader);
        SetUpReader(Settings);

        var cold = await FirstRunAsync("c1");
        var coldSummary = TryLatestSummary()!.Value;
        RowsOf(coldSummary, _ => true).ShouldNotContain("server_");
        coldSummary.GetProperty("partial").GetBoolean().ShouldBeFalse();
        reader.Calls.ShouldBe(0, "a cold cache is no reason to call the reader during a phone run");

        await File.WriteAllTextAsync(Path.Combine(Golden.Results, "reground.request"), string.Empty);
        Wake(Factory);
        var reground = (await NextLatestAfterAsync(cold)).GetProperty("runId").GetString()!;
        reader.Calls.ShouldBe(1, "the re-ground reads on a miss");

        await FirstRunAsync("c1");
        (await BecomesTrueAsync(() => LatestRunId() is { } id && id != reground, Bounded)).ShouldBeTrue();
        RowsOf(TryLatestSummary()!.Value, _ => true).ShouldContain("server_model");
        reader.Calls.ShouldBe(1, "the second phone run used the cache");
    }

    [Fact]
    public async Task With_no_reader_set_up_a_re_ground_makes_no_server_row()
    {
        StarFoxGolden("c1");
        var firstRun = await FirstRunAsync("c1");
        var reader = new CountingReader(StarFox);
        UseReader(reader);

        await RedeployAsync(); // no ShelfReader:Endpoint
        var rescored = await NextLatestAfterAsync(firstRun);

        RowsOf(rescored, _ => true).ShouldNotContain("server_");
        reader.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task The_one_shot_upload_never_calls_the_reader()
    {
        StarFoxGolden("c1");
        var reader = new CountingReader(StarFox);
        UseReader(reader);
        SetUpReader(Settings);
        using var client = AdminClient();

        var summary = await PostRunOkAsync(client, RunBody(AllEngines("c1", StarFoxRead)));

        reader.Calls.ShouldBe(0);
        RowsOf(summary.GetProperty("summary").ValueKind == JsonValueKind.Object ? summary.GetProperty("summary") : summary, _ => true).ShouldNotContain("server_");
    }
}
