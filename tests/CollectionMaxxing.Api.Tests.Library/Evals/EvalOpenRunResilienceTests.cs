using System.Text.Json;
using CollectionMaxxing.Api.Tests.Scanning;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Infrastructure.Evals;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CollectionMaxxing.Api.Tests.Evals;

/// <summary>
/// The PC redeploys often and a run takes a while, so a run must survive it: what is stored is on disk, and a photo stored but not yet grounded is
/// picked up again after a restart. One photo that cannot be grounded never holds the rest of the run up.
/// </summary>
public class EvalOpenRunResilienceTests(ApiFixture api) : OpenRunTest(api)
{
    /// <summary>A second host over the same results folder and the same fakes: the API after a redeploy.</summary>
    private WebApplicationFactory<Program> RestartedHost() => Api.Factory.WithWebHostBuilder(builder =>
    {
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(Settings));
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(Options);
            services.RemoveAll<ICatalogProvider>();
            services.AddSingleton<ICatalogProvider>(Games);
            services.AddSingleton<ICatalogProvider>(Books);
            services.RemoveAll<IProductLookup>();
            services.AddSingleton<IProductLookup>(Products);
        });
    });

    private static HttpClient AdminOn(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "admin");
        return client;
    }

    [Fact]
    public async Task Photos_stored_before_a_restart_are_grounded_after_it_and_the_run_is_finished_as_if_nothing_happened()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Golden.AddSingle("c2", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The provider never answers: the first API is stopped while it is still grounding. Only the shutdown ends the wait.
        Games.Behaviour = async cancellationToken =>
        {
            asked.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        };
        string runId;
        using (var before = AdminClient())
        {
            runId = (await OpenOkAsync(before)).GetProperty("runId").GetString()!;
            await PutPhotoOkAsync(before, "c1", AllEngines("c1", StarFoxRead));
            await PutPhotoOkAsync(before, "c2", AllEngines("c2", StarFoxRead));
            await asked.Task.WaitAsync(Bounded);
        }

        await Factory.DisposeAsync(); // the API is stopped
        Directory.Exists(Path.Combine(OpenRunFolder, "grounded")).ShouldBeFalse("the stopped API wrote no half-done grounding");
        Games.Behaviour = null;
        await using var restarted = RestartedHost();
        using var after = AdminOn(restarted);

        var status = await StatusAsync(after);
        status.GetProperty("runId").GetString().ShouldBe(runId);
        Received(status).ShouldBe(["c1", "c2"]);
        await StatusWhenAsync(after, s => s.GetProperty("grounded").GetInt32() == 2);
        await FinishOkAsync(after);
        var done = await StatusWhenDoneAsync(after);

        done.GetProperty("summary").GetProperty("scoredPhotos").GetInt32().ShouldBe(6, "a row for each engine of the two photos");
        done.GetProperty("summary").GetProperty("engines").GetProperty("apple_model").GetProperty("single").GetProperty("top1Pct").GetDouble().ShouldBe(100);
        ResultFiles().Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_run_finished_just_before_a_restart_is_scored_after_it()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Games.Behaviour = async cancellationToken =>
        {
            asked.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        };
        using (var before = AdminClient())
        {
            await OpenOkAsync(before);
            await PutPhotoOkAsync(before, "c1", AllEngines("c1", StarFoxRead));
            await FinishOkAsync(before);
            await asked.Task.WaitAsync(Bounded);
        }

        await Factory.DisposeAsync();
        ResultFiles().ShouldBeEmpty();
        Games.Behaviour = null;
        await using var restarted = RestartedHost();
        using var after = AdminOn(restarted);

        var done = await StatusWhenDoneAsync(after);

        done.GetProperty("summary").GetProperty("scoredPhotos").GetInt32().ShouldBe(3);
        ResultFiles().Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_photo_that_fails_to_ground_is_tried_again_later_while_the_others_carry_on()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Golden.AddSingle("c2", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
        var failures = 0;
        UseClockAndFlakyStore(clock, (photoId, file) => photoId == "c1" && file.Photos.Count > 0 && Interlocked.Increment(ref failures) <= 2);
        using var admin = AdminClient();
        await OpenOkAsync(admin);
        await PutPhotoOkAsync(admin, "c1", AllEngines("c1", StarFoxRead));
        await PutPhotoOkAsync(admin, "c2", AllEngines("c2", StarFoxRead));

        (await WaitUntilAsync(async () => Volatile.Read(ref failures) >= 1 && await GroundedCountAsync(admin) == 1, Bounded))
            .ShouldBeTrue("c2 is grounded although c1 failed");
        (await WaitUntilAsync(async () =>
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            return await GroundedCountAsync(admin) == 2;
        }, Bounded)).ShouldBeTrue("c1 is tried again after a wait");
        await FinishOkAsync(admin);
        var summary = (await StatusWhenDoneAsync(admin)).GetProperty("summary");

        failures.ShouldBe(3, "it failed twice, then worked");
        summary.GetProperty("scoredPhotos").GetInt32().ShouldBe(6);
        Skipped(summary).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_photo_that_never_grounds_is_given_up_and_listed_so_the_run_still_finishes()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Golden.AddSingle("c2", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
        UseClockAndFlakyStore(clock, (photoId, file) => photoId == "c1" && file.Photos.Count > 0);
        using var admin = AdminClient();
        await OpenOkAsync(admin);
        await PutPhotoOkAsync(admin, "c1", AllEngines("c1", StarFoxRead));
        await PutPhotoOkAsync(admin, "c2", AllEngines("c2", StarFoxRead));
        await FinishOkAsync(admin);

        (await BecomesTrueAsync(() =>
        {
            clock.Advance(TimeSpan.FromMinutes(10));
            return ResultFiles().Count == 1;
        }, Bounded)).ShouldBeTrue("the run is scored although one photo never grounded");
        var summary = (await StatusWhenDoneAsync(admin)).GetProperty("summary");

        Skipped(summary).ShouldBe(["c1:grounding_failed"]);
        summary.GetProperty("scoredPhotos").GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task A_run_with_a_photo_given_up_on_is_partial_so_it_never_gets_a_full_verdict_or_becomes_a_baseline()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Golden.AddSingle("c2", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
        UseClockAndFlakyStore(clock, (photoId, file) => photoId == "c1" && file.Photos.Count > 0);
        using var admin = AdminClient();
        await OpenOkAsync(admin);
        await PutPhotoOkAsync(admin, "c1", AllEngines("c1", StarFoxRead));
        await PutPhotoOkAsync(admin, "c2", AllEngines("c2", StarFoxRead));
        await FinishOkAsync(admin); // the phone sent every photo and did not say partial

        (await BecomesTrueAsync(() =>
        {
            clock.Advance(TimeSpan.FromMinutes(10));
            return ResultFiles().Count == 1;
        }, Bounded)).ShouldBeTrue();
        var done = await StatusWhenDoneAsync(admin);

        done.GetProperty("partial").GetBoolean().ShouldBeTrue();
        var summary = done.GetProperty("summary");
        summary.GetProperty("partial").GetBoolean().ShouldBeTrue();
        summary.GetProperty("gate").GetProperty("verdict").GetString().ShouldBe("incomplete");
        summary.GetProperty("regression").GetProperty("status").GetString().ShouldBe("partial_run");
    }

    [Fact]
    public async Task A_run_whose_results_are_already_on_disk_is_only_marked_done_and_not_scored_into_a_second_file()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        using var admin = AdminClient();
        var runId = (await OpenOkAsync(admin)).GetProperty("runId").GetString()!;
        // The API stopped after writing this run's results and before marking it done.
        await File.WriteAllTextAsync(Path.Combine(Golden.Results, "latest.json"), JsonSerializer.Serialize(new { runId, partial = false }));
        await PutPhotoOkAsync(admin, "c1", AllEngines("c1", StarFoxRead));
        await FinishOkAsync(admin);

        var done = await StatusWhenDoneAsync(admin);

        done.GetProperty("runId").GetString().ShouldBe(runId);
        ResultFiles().ShouldBeEmpty("the results of this run were already written: nothing is written again");
        Directory.Exists(Path.Combine(OpenRunFolder, "photos")).ShouldBeFalse("the stored photos are cleared all the same");
    }

    [Fact]
    public async Task A_run_whose_done_mark_could_not_be_written_is_marked_on_the_next_try_without_a_second_result_file()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
        var refused = 0;
        UseClockAndFlakyStore(clock, (_, _) => false, header => header.State == OpenRunState.Done && Interlocked.Increment(ref refused) == 1);
        using var admin = AdminClient();
        await OpenOkAsync(admin);
        await PutPhotoOkAsync(admin, "c1", AllEngines("c1", StarFoxRead));
        await FinishOkAsync(admin);

        (await BecomesTrueAsync(() =>
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            return Volatile.Read(ref refused) >= 2;
        }, Bounded)).ShouldBeTrue("it tried again after the failed write");
        await StatusWhenDoneAsync(admin);

        ResultFiles().Count.ShouldBe(1, "one run, one result file, however many tries it took to mark it done");
    }

    private void UseClockAndFlakyStore(FakeTimeProvider clock, Func<string, GroundedPhotoFile, bool> fails, Func<OpenRunHeader, bool>? failsSave = null) =>
        ConfigureServices = services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
            services.RemoveAll<IEvalRunStore>();
            services.AddSingleton<IEvalRunStore>(provider => new FlakyRunStore(new FileEvalRunStore(provider.GetRequiredService<IOptions<EvalsOptions>>()), fails, failsSave));
        };

    /// <summary>The real store, except that it refuses to keep some groundings: a disk that fails under the PC.</summary>
    private sealed class FlakyRunStore(IEvalRunStore inner, Func<string, GroundedPhotoFile, bool> fails, Func<OpenRunHeader, bool>? failsSave) : IEvalRunStore
    {
        public bool IsConfigured => inner.IsConfigured;

        public OpenRunHeader? Load() => inner.Load();

        public void Create(OpenRunHeader header) => inner.Create(header);

        public void Save(OpenRunHeader header)
        {
            if (failsSave?.Invoke(header) == true)
            {
                throw new IOException("the disk is full");
            }

            inner.Save(header);
        }

        public void Discard() => inner.Discard();

        public IReadOnlySet<string> StoredIds() => inner.StoredIds();

        public IReadOnlySet<string> GroundedIds() => inner.GroundedIds();

        public StoredEvalPhoto? LoadPhoto(string photoId) => inner.LoadPhoto(photoId);

        public void SavePhoto(string photoId, StoredEvalPhoto photo) => inner.SavePhoto(photoId, photo);

        public void DeletePhoto(string photoId) => inner.DeletePhoto(photoId);

        public GroundedPhotoFile? LoadGrounded(string photoId) => inner.LoadGrounded(photoId);

        public void SaveGrounded(string photoId, GroundedPhotoFile grounded)
        {
            if (fails(photoId, grounded))
            {
                throw new IOException("the disk is full");
            }

            inner.SaveGrounded(photoId, grounded);
        }

        public void DeleteGrounded(string photoId) => inner.DeleteGrounded(photoId);

        public void ClearPhotos() => inner.ClearPhotos();
    }
}
