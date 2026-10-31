using System.Diagnostics;
using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.TestSupport.Evals;

namespace CollectionMaxxing.Api.Tests.Evals;

/// <summary>
/// Base of the tests of the resumable eval run: the phone opens a run on the PC, sends one photo at a time, and finishes it; the PC grounds in
/// the background and scores once. Helpers speak the routes under <c>/runs/open</c>.
/// </summary>
public abstract class OpenRunTest(ApiFixture api) : EvalTest(api)
{
    /// <summary>How long a test waits for something the server does on its own.</summary>
    protected static readonly TimeSpan Bounded = TimeSpan.FromSeconds(20);

    protected static readonly SyntheticGolden.SingleItem StarFoxTruth =
        new("Star Fox 64", "game_cartridge", "cart_only", "loose_cartridge", "Nintendo 64");

    protected static readonly SyntheticGolden.SingleItem ZeldaTruth =
        new("The Legend of Zelda", "game_cartridge", "cart_only", "loose_cartridge", "Nintendo 64");

    protected static string OpenUrl => $"{Base}/runs/open";

    protected static object StarFoxRead() =>
        ReadItem("Star Fox 64", "game", "game_cartridge", "Nintendo 64", "cart_only", resolvedBy: "model");

    protected static object ZeldaRead() =>
        ReadItem("The Legend of Zelda", "game", "game_cartridge", "Nintendo 64", "cart_only", resolvedBy: "model");

    /// <summary>The three engines' results for one photo, as the phone sends them in one request.</summary>
    protected static object[] AllEngines(string photoId, Func<object> read) =>
    [
        PhotoResult(photoId, "apple_model", [read()]),
        PhotoResult(photoId, "fm_text", [read()]),
        PhotoResult(photoId, "vision_pipeline", [read()]),
    ];

    protected static object OpenBody(string? baselineNote = null) => new
    {
        device = new { model = "iPhone18,1", osVersion = "26.0", appVersion = "0.3.0" },
        configVersion = "cfg-test-1",
        baselineNote,
    };

    protected static Task<HttpResponseMessage> OpenAsync(HttpClient client, object? body = null) =>
        client.PostAsync(OpenUrl, Json(body ?? OpenBody()));

    protected static async Task<JsonElement> OpenOkAsync(HttpClient client, object? body = null)
    {
        var response = await OpenAsync(client, body);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await ReadJsonAsync(response);
    }

    protected static Task<HttpResponseMessage> PutPhotoAsync(HttpClient client, string photoId, params object[] results) =>
        client.PutAsync($"{OpenUrl}/photos/{photoId}", Json(new { results }));

    protected static async Task<JsonElement> PutPhotoOkAsync(HttpClient client, string photoId, params object[] results)
    {
        var response = await PutPhotoAsync(client, photoId, results);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await ReadJsonAsync(response);
    }

    protected static Task<HttpResponseMessage> FinishAsync(HttpClient client, bool? partial = null) =>
        client.PostAsync($"{OpenUrl}/finish", Json(partial is null ? new { } : new { partial }));

    protected static async Task<JsonElement> FinishOkAsync(HttpClient client, bool? partial = null)
    {
        var response = await FinishAsync(client, partial);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        return await ReadJsonAsync(response);
    }

    protected static async Task<JsonElement> StatusAsync(HttpClient client)
    {
        var response = await client.GetAsync(OpenUrl);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await ReadJsonAsync(response);
    }

    protected static IReadOnlyList<string> Received(JsonElement status) =>
        [.. status.GetProperty("received").EnumerateArray().Select(e => e.GetString()!)];

    protected static IReadOnlyList<string> Skipped(JsonElement summary) =>
        [.. summary.GetProperty("skipped").EnumerateArray().Select(s => $"{s.GetProperty("photoId").GetString()}:{s.GetProperty("reason").GetString()}")];

    protected static async Task<bool> BecomesTrueAsync(Func<bool> condition, TimeSpan limit)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < limit)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        return condition();
    }

    protected static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan limit)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < limit)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        return await condition();
    }

    protected static async Task<int> GroundedCountAsync(HttpClient client) =>
        (await StatusAsync(client)).GetProperty("grounded").GetInt32();

    /// <summary>Polls the open run's status until <paramref name="condition"/> holds, and returns the status that satisfied it.</summary>
    protected static async Task<JsonElement> StatusWhenAsync(HttpClient client, Func<JsonElement, bool> condition, TimeSpan? limit = null)
    {
        var clock = Stopwatch.StartNew();
        JsonElement status;
        do
        {
            status = await StatusAsync(client);
            if (condition(status))
            {
                return status;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }
        while (clock.Elapsed < (limit ?? Bounded));

        Assert.Fail($"The open run never reached the state the test waits for. Last status: {status}");
        return status;
    }

    protected static Task<JsonElement> StatusWhenDoneAsync(HttpClient client) =>
        StatusWhenAsync(client, s => s.GetProperty("state").GetString() == "done");

    protected string OpenRunFolder => Path.Combine(Golden.Results, "open-run");

    protected async Task<JsonElement> LatestRunFileAsync()
    {
        var text = await File.ReadAllTextAsync(Path.Combine(Golden.Results, "latest-run.json"));
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    protected async Task<JsonElement> LatestSummaryAsync()
    {
        var text = await File.ReadAllTextAsync(Path.Combine(Golden.Results, "latest.json"));
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
