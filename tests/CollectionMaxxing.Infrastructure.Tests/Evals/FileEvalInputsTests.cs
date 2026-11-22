using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Scanning;
using CollectionMaxxing.Infrastructure.Evals;
using CollectionMaxxing.TestSupport.Evals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Infrastructure.Tests.Evals;

/// <summary>
/// The phone's readings of the last run as <c>latest-inputs.json</c> beside the result files, and the empty <c>reground.request</c> file that asks for
/// them to be grounded again. The readings must come back exactly as written, be replaced whole by a newer run, and never leave a half-written file.
/// </summary>
public sealed class FileEvalInputsTests : IDisposable
{
    private readonly SyntheticGolden _golden = new();

    private static readonly DateTimeOffset At = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    public void Dispose() => _golden.Dispose();

    private FileEvalInputs Inputs(bool enabled = true, string? resultsPath = null, ILogger<FileEvalInputs>? logger = null, Action<string>? delete = null) =>
        new(Options.Create(new EvalsOptions { Enabled = enabled, ResultsPath = resultsPath ?? _golden.Results }), logger ?? NullLogger<FileEvalInputs>.Instance, delete);

    /// <summary>Keeps the text of every line logged, with the exception's text if any.</summary>
    private sealed class ListLogger : ILogger<FileEvalInputs>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add($"{logLevel}: {formatter(state, exception)} {exception}");
    }

    private static KeptRunInputs Kept(string runId = "run1", string title = "Star Fox 64") => new(
        runId,
        At,
        new EvalDevice("iPhone18,1", "26.0", "0.3.0"),
        "cfg-1",
        "a note",
        true,
        Guid.Parse("11111111-2222-3333-4444-555555555555"),
        [
            new KeptPhoto(
                "c1",
                [
                    new EvalPhotoInput(
                        "c1",
                        ScanEngine.AppleModel,
                        EvalOutcome.Ok,
                        FallbackReason.Timeout,
                        [new EvalReadItemInput(
                            new IdentifyItemInput("l1", "seg1", title, title, null, null, ItemCompleteness.CartOnly, WorkKind.Game, ReleaseFormat.GameCartridge, 1997, "Nintendo 64", null),
                            ResolvedStep.Model,
                            ScanTier.T1)],
                        2,
                        1500,
                        800,
                        ThermalState.Fair,
                        1,
                        At,
                        new DuplicateRescanInput(3, 2, 0),
                        [new EvalOcrLineInput(title, 0.1, 0.2, 0.3, 0.4)],
                        [[0]],
                        "cover cue"),
                ]),
        ],
        [new SkippedPhoto("c2", "evals.thermal_required")]);

    private static EvalSummary Summary(string runId, string date, string sha) => new(
        runId, date, sha, "cfg-1", new EvalDevice("iPhone18,1", "26.0", "0.3.0"), 1, [], new Dictionary<string, EngineSummary>(),
        new GateTable("apple", "pass", [], null!), new RegressionCheck("ok", 0, []), new Dictionary<string, double>());

    private async Task FinishRunAsync(string runId, string date = "2026-11-22", string title = "Star Fox 64")
    {
        var results = new FileEvalResults(Options.Create(new EvalsOptions { Enabled = true, ResultsPath = _golden.Results }));
        await results.SaveAsync(Summary(runId, date, "abc1234"), new GroundedRun(runId, At, new EvalDevice("iPhone18,1", "26.0", "0.3.0"), "cfg-1", []), "# r", CancellationToken.None);
        await Inputs().SaveAsync(Kept(runId, title), CancellationToken.None);
    }

    private List<string> Names(string pattern) => [.. Directory.GetFiles(_golden.Results, pattern).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task A_finished_run_keeps_a_dated_copy_of_its_readings_next_to_its_dated_result()
    {
        await FinishRunAsync("run1");

        Names("*-inputs.json").ShouldBe(["2026-11-22-abc1234-inputs.json", "latest-inputs.json"]);
        File.Exists(Path.Combine(_golden.Results, "2026-11-22-abc1234.json")).ShouldBeTrue();
    }

    [Fact]
    public async Task The_latest_readings_file_is_still_written_byte_for_byte_the_same()
    {
        await FinishRunAsync("run1");

        File.ReadAllBytes(Path.Combine(_golden.Results, "latest-inputs.json")).ShouldBe(File.ReadAllBytes(Path.Combine(_golden.Results, "2026-11-22-abc1234-inputs.json")));
        Inputs().Load()!.RunId.ShouldBe("run1");
    }

    [Fact]
    public async Task A_second_run_does_not_overwrite_the_first_runs_dated_copy()
    {
        await FinishRunAsync("run1", title: "Star Fox 64");
        await FinishRunAsync("run2", title: "Zelda");

        Names("2026-*-inputs.json").ShouldBe(["2026-11-22-abc1234-2-inputs.json", "2026-11-22-abc1234-inputs.json"]);
        File.ReadAllText(Path.Combine(_golden.Results, "2026-11-22-abc1234-inputs.json")).ShouldContain("Star Fox 64");
        File.ReadAllText(Path.Combine(_golden.Results, "2026-11-22-abc1234-2-inputs.json")).ShouldContain("Zelda");
    }

    [Fact]
    public async Task Only_the_newest_twenty_dated_copies_are_kept_and_a_result_summary_is_never_deleted()
    {
        for (var day = 1; day <= 22; day++)
        {
            var date = $"2026-10-{day:00}";
            await FinishRunAsync("run" + day, date);
            File.SetLastWriteTimeUtc(Path.Combine(_golden.Results, date + "-abc1234-inputs.json"), At.UtcDateTime.AddDays(day));
        }

        var kept = Names("2026-*-inputs.json");
        kept.Count.ShouldBe(20);
        kept.ShouldNotContain("2026-10-01-abc1234-inputs.json");
        kept.ShouldNotContain("2026-10-02-abc1234-inputs.json");
        kept.ShouldContain("2026-10-22-abc1234-inputs.json");
        Names("2026-*-abc1234.json").Count.ShouldBe(22);
        File.Exists(Path.Combine(_golden.Results, "latest-inputs.json")).ShouldBeTrue();
    }

    [Fact]
    public async Task The_number_of_dated_copies_kept_is_a_setting()
    {
        var inputs = new FileEvalInputs(Options.Create(new EvalsOptions { Enabled = true, ResultsPath = _golden.Results, KeepDatedInputs = 1 }), NullLogger<FileEvalInputs>.Instance);
        var old = Path.Combine(_golden.Results, "2026-01-01-old-inputs.json");
        await File.WriteAllTextAsync(old, "{}");
        File.SetLastWriteTimeUtc(old, At.UtcDateTime.AddDays(-9));

        await inputs.SaveAsync(Kept(), CancellationToken.None);

        Names("2026-*-inputs.json").Count.ShouldBe(1);
        File.Exists(old).ShouldBeFalse();
    }

    private FileEvalInputs InputsWith(int keep = 20, Action<FileInfo>? deleteOld = null) =>
        new(Options.Create(new EvalsOptions { Enabled = true, ResultsPath = _golden.Results, KeepDatedInputs = keep }), NullLogger<FileEvalInputs>.Instance, deleteOld: deleteOld);

    [Fact]
    public async Task The_copy_just_written_is_never_pruned_even_when_older_copies_carry_later_write_times()
    {
        var old = Path.Combine(_golden.Results, "2026-01-01-old-inputs.json");
        await File.WriteAllTextAsync(old, "{}");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddYears(5));

        await InputsWith(keep: 1).SaveAsync(Kept(), CancellationToken.None);

        Names("2026-10-31-run1-inputs.json").Count.ShouldBe(1);
        File.Exists(old).ShouldBeFalse();
    }

    [Fact]
    public async Task A_failure_writing_the_dated_copy_does_not_fail_the_save()
    {
        Directory.CreateDirectory(Path.Combine(_golden.Results, "2026-10-31-run1-inputs.json")); // the dated copy cannot be written over a folder

        await Inputs().SaveAsync(Kept(), CancellationToken.None);

        Inputs().Load()!.RunId.ShouldBe("run1");
    }

    [Fact]
    public async Task A_failure_deleting_an_old_copy_does_not_fail_the_save()
    {
        var old = Path.Combine(_golden.Results, "2026-01-01-old-inputs.json");
        await File.WriteAllTextAsync(old, "{}");
        File.SetLastWriteTimeUtc(old, At.UtcDateTime.AddDays(-9));

        await InputsWith(keep: 1, deleteOld: _ => throw new InvalidOperationException("boom")).SaveAsync(Kept(), CancellationToken.None);

        Inputs().Load()!.RunId.ShouldBe("run1");
        File.Exists(Path.Combine(_golden.Results, "2026-10-31-run1-inputs.json")).ShouldBeTrue();
    }

    [Fact]
    public async Task The_latest_readings_are_written_before_the_dated_copy()
    {
        Directory.CreateDirectory(Path.Combine(_golden.Results, "2026-10-31-run1-inputs.json"));

        await Inputs().SaveAsync(Kept(), CancellationToken.None);

        // the dated copy failed, yet latest-inputs.json is whole: it was written first
        Inputs().Load().ShouldNotBeNull().Photos.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Readings_come_back_exactly_as_they_were_kept()
    {
        var kept = Kept();

        await Inputs().SaveAsync(kept, CancellationToken.None);
        var loaded = Inputs().Load();

        loaded.ShouldNotBeNull();
        loaded.RunId.ShouldBe(kept.RunId);
        loaded.ReadAt.ShouldBe(kept.ReadAt);
        loaded.Device.ShouldBe(kept.Device);
        loaded.ConfigVersion.ShouldBe("cfg-1");
        loaded.BaselineNote.ShouldBe("a note");
        loaded.Partial.ShouldBeTrue();
        loaded.OpenedBy.ShouldBe(kept.OpenedBy);
        loaded.Skipped.ShouldBe(kept.Skipped);
        var photo = loaded.Photos.ShouldHaveSingleItem();
        photo.PhotoId.ShouldBe("c1");
        var result = photo.Results.ShouldHaveSingleItem();
        result.Engine.ShouldBe(ScanEngine.AppleModel);
        result.FellBackFrom.ShouldBe(FallbackReason.Timeout);
        result.Thermal.ShouldBe(ThermalState.Fair);
        result.Items!.ShouldHaveSingleItem().Read.Title.ShouldBe("Star Fox 64");
        result.Items![0].Tier.ShouldBe(ScanTier.T1);
        result.OcrLines!.ShouldHaveSingleItem().Text.ShouldBe("Star Fox 64");
        result.OcrPieces!.ShouldHaveSingleItem().ShouldBe([0]);
        result.DuplicateRescan.ShouldBe(new DuplicateRescanInput(3, 2, 0));
        result.CoverCue.ShouldBe("cover cue");
    }

    [Fact]
    public async Task The_readings_are_one_file_next_to_the_result_files_and_no_temp_file_is_left_behind()
    {
        await Inputs().SaveAsync(Kept(), CancellationToken.None);

        File.Exists(Path.Combine(_golden.Results, "latest-inputs.json")).ShouldBeTrue();
        Directory.GetFiles(_golden.Results).Select(Path.GetFileName).Order(StringComparer.Ordinal).ShouldBe(["2026-10-31-run1-inputs.json", "latest-inputs.json"]);
    }

    [Fact]
    public async Task A_newer_run_replaces_the_readings_whole()
    {
        await Inputs().SaveAsync(Kept("run1", "Star Fox 64"), CancellationToken.None);

        await Inputs().SaveAsync(Kept("run2", "Zelda"), CancellationToken.None);

        var loaded = Inputs().Load();
        loaded!.RunId.ShouldBe("run2");
        loaded.Photos.ShouldHaveSingleItem().Results.ShouldHaveSingleItem().Items!.ShouldHaveSingleItem().Read.Title.ShouldBe("Zelda");
    }

    [Fact]
    public async Task A_save_that_cannot_finish_leaves_no_temp_file_behind()
    {
        // A directory in the place of the file cannot be moved onto, so the save fails after the temp file was written.
        Directory.CreateDirectory(Path.Combine(_golden.Results, "latest-inputs.json"));

        var failed = await Record.ExceptionAsync(() => Inputs().SaveAsync(Kept("run2"), CancellationToken.None));

        failed.ShouldNotBeNull();
        Directory.GetFiles(_golden.Results, "*.tmp").ShouldBeEmpty("a failed save cleans up after itself");
    }

    [Fact]
    public void With_nothing_kept_there_are_no_readings()
    {
        Inputs().Load().ShouldBeNull();
    }

    [Fact]
    public async Task A_file_that_is_not_readings_is_none()
    {
        await File.WriteAllTextAsync(Path.Combine(_golden.Results, "latest-inputs.json"), "{ not json");

        Inputs().Load().ShouldBeNull();
    }

    [Fact]
    public async Task With_the_evals_off_nothing_is_kept_or_found()
    {
        var off = Inputs(enabled: false);

        off.IsConfigured.ShouldBeFalse();
        off.Load().ShouldBeNull();
        await Should.ThrowAsync<InvalidOperationException>(() => off.SaveAsync(Kept(), CancellationToken.None));
    }

    [Fact]
    public void A_regrounding_request_is_taken_once_and_its_file_is_deleted()
    {
        var path = Path.Combine(_golden.Results, "reground.request");
        File.WriteAllText(path, string.Empty);
        var inputs = Inputs();

        inputs.TakeRegroundRequest().ShouldBeTrue();

        File.Exists(path).ShouldBeFalse();
        inputs.TakeRegroundRequest().ShouldBeFalse("the file is gone, so the request is spent");
    }

    [Fact]
    public void Looking_for_a_request_leaves_the_file_where_it_is()
    {
        var path = Path.Combine(_golden.Results, "reground.request");
        var inputs = Inputs();
        inputs.HasRegroundRequest().ShouldBeFalse();
        File.WriteAllText(path, string.Empty);

        inputs.HasRegroundRequest().ShouldBeTrue();

        File.Exists(path).ShouldBeTrue("only taking a request spends it");
    }

    [Fact]
    public void A_request_file_that_cannot_be_deleted_is_not_taken_so_it_never_starts_a_loop_and_is_logged_once_by_the_type_of_the_error()
    {
        var path = Path.Combine(_golden.Results, "reground.request");
        File.WriteAllText(path, string.Empty);
        var log = new ListLogger();
        var inputs = Inputs(logger: log, delete: _ => throw new IOException("in use by C:\\somewhere\\private"));

        inputs.TakeRegroundRequest().ShouldBeFalse();
        inputs.TakeRegroundRequest().ShouldBeFalse();
        inputs.TakeRegroundRequest().ShouldBeFalse();

        File.Exists(path).ShouldBeTrue();
        inputs.HasRegroundRequest().ShouldBeTrue();
        var line = log.Lines.ShouldHaveSingleItem("said once, not at every look");
        line.ShouldStartWith("Warning");
        line.ShouldContain("IOException");
        line.ShouldNotContain("somewhere", Case.Insensitive, "the error's message may name a path");
    }

    [Fact]
    public void Without_a_request_file_nothing_is_requested()
    {
        Inputs().TakeRegroundRequest().ShouldBeFalse();
    }
}
