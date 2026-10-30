using System.Diagnostics;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Scanning;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.TestSupport.Evals;

namespace CollectionMaxxing.Api.Tests.Evals;

/// <summary>
/// A big run takes minutes to ground, and the phone locks or loses its connection meanwhile (a 19-photo run made 636 catalogue
/// calls and took five and a half minutes). The run must still be saved, and a read that several photos and engines share is
/// looked up once.
/// </summary>
public class EvalSubmitRunTests(ApiFixture api) : EvalTest(api)
{
    /// <summary>How long a test waits for something the server does on its own.</summary>
    private static readonly TimeSpan Bounded = TimeSpan.FromSeconds(15);

    private static readonly SyntheticGolden.SingleItem StarFoxTruth =
        new("Star Fox 64", "game_cartridge", "cart_only", "loose_cartridge", "Nintendo 64");

    private static object StarFoxRead() =>
        ReadItem("Star Fox 64", "game", "game_cartridge", "Nintendo 64", "cart_only", resolvedBy: "model");

    private async Task<JsonElement> LatestSummaryAsync()
    {
        var text = await File.ReadAllTextAsync(Path.Combine(Golden.Results, "latest.json"));
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<bool> BecomesTrueAsync(Func<bool> condition, TimeSpan limit)
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

    // ---- a dropped connection does not throw the run away ------------------------------------------------------------------

    [Fact]
    public async Task A_run_is_saved_even_when_the_phone_drops_the_connection_while_the_reads_are_being_grounded()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The provider holds its answer until the test lets it go, and gives up only if the token it was handed is cancelled: that
        // is what the dropped connection did to it.
        Games.Behaviour = async cancellationToken =>
        {
            asked.TrySetResult();
            await answer.Task.WaitAsync(cancellationToken);
        };
        using var admin = AdminClient();
        using var dropped = new CancellationTokenSource();

        var posting = admin.PostAsync($"{Base}/runs", Json(RunBody(PhotoResult("c1", "apple_model", [StarFoxRead()]))), dropped.Token);
        await asked.Task.WaitAsync(Bounded);
        await dropped.CancelAsync(); // the phone locks: the request is aborted while the PC is still grounding
        // The test server's client call returns only when the server has finished the request, so it is not awaited here: the
        // abort gets a moment to reach the server, then the provider answers.
        await Task.WhenAny(posting, Task.Delay(TimeSpan.FromMilliseconds(250)));
        answer.SetResult();

        (await BecomesTrueAsync(() => File.Exists(Path.Combine(Golden.Results, "latest.json")), Bounded))
            .ShouldBeTrue("the run is saved although nobody is waiting for the answer");
        ResultFiles().Count.ShouldBe(1);
        var summary = await LatestSummaryAsync();
        summary.GetProperty("scoredPhotos").GetInt32().ShouldBe(1);
        summary.GetProperty("engines").GetProperty("apple_model").GetProperty("single").GetProperty("top1Pct").GetDouble().ShouldBe(100);
        await Record.ExceptionAsync(() => posting.WaitAsync(Bounded)); // the phone is gone; however its call ends, it is not the test's concern
    }

    // ---- each distinct read is looked up once per run ----------------------------------------------------------------------

    [Fact]
    public async Task The_same_read_from_several_engines_and_photos_is_looked_up_once_and_every_photo_still_scores_the_match()
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

        var response = await PostRunOkAsync(admin, RunBody(
            PhotoResult("c1", "apple_model", [StarFoxRead()]),
            PhotoResult("c1", "fm_text", [StarFoxRead()]),
            PhotoResult("c2", "apple_model", [StarFoxRead()]),
            PhotoResult("c2", "fm_text", [StarFoxRead()])));

        lookups.ShouldBe(1, "four photo-and-engine results carry one read");
        var engines = response.GetProperty("summary").GetProperty("engines");
        foreach (var engine in new[] { "apple_model", "fm_text" })
        {
            var single = engines.GetProperty(engine).GetProperty("single");
            single.GetProperty("top1Pct").GetDouble().ShouldBe(100, engine);
            single.GetProperty("precisionPct").GetDouble().ShouldBe(100, engine);
        }

        var photos = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Golden.Results, "latest-run.json"))).RootElement.GetProperty("photos");
        photos.GetArrayLength().ShouldBe(4);
        foreach (var photo in photos.EnumerateArray())
        {
            photo.GetProperty("items")[0].GetProperty("verdict").GetString().ShouldBe("matched");
        }
    }

    [Fact]
    public async Task Reads_that_differ_in_any_field_are_looked_up_separately()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Golden.AddSingle("c2", StarFoxTruth);
        Golden.AddSingle("c3", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        var lookups = 0;
        Games.Behaviour = _ =>
        {
            Interlocked.Increment(ref lookups);
            return Task.CompletedTask;
        };
        using var admin = AdminClient();

        await PostRunOkAsync(admin, RunBody(
            PhotoResult("c1", "apple_model", [StarFoxRead()]),
            PhotoResult("c2", "apple_model", [ReadItem("Star Fox 64", "game", "game_cartridge", "Nintendo 64", "cart_only", year: 1997, resolvedBy: "model")]),
            PhotoResult("c3", "apple_model", [ReadItem("Star Fox 64", "game", "game_cartridge", "Super Nintendo", "cart_only", resolvedBy: "model")])));

        lookups.ShouldBe(3, "a different year, or a different platform, is a different question for the catalogue");
    }

    [Fact]
    public async Task A_second_run_looks_its_reads_up_again()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        var lookups = 0;
        Games.Behaviour = _ =>
        {
            Interlocked.Increment(ref lookups);
            return Task.CompletedTask;
        };
        using var admin = AdminClient();

        await PostRunOkAsync(admin, RunBody(PhotoResult("c1", "apple_model", [StarFoxRead()])));
        await PostRunOkAsync(admin, RunBody(PhotoResult("c1", "apple_model", [StarFoxRead()])));

        lookups.ShouldBe(2, "what one run learned is not kept for the next: the catalogue may have changed");
    }
}
