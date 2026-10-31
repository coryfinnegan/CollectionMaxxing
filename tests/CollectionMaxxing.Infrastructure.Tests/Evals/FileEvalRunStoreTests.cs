using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Scanning;
using CollectionMaxxing.Infrastructure.Evals;
using CollectionMaxxing.TestSupport.Evals;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Infrastructure.Tests.Evals;

/// <summary>
/// The open eval run as files under the results folder: it is what the PC keeps between requests, between the phone's photos and across a restart, so it
/// must come back exactly as it was written, count what is stored and what is grounded from file names alone, and never leave a half-written file.
/// </summary>
public sealed class FileEvalRunStoreTests : IDisposable
{
    private readonly SyntheticGolden _golden = new();

    private static readonly DateTimeOffset At = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    public void Dispose() => _golden.Dispose();

    private FileEvalRunStore Store(bool enabled = true, string? resultsPath = null) =>
        new(Options.Create(new EvalsOptions { Enabled = enabled, ResultsPath = resultsPath ?? _golden.Results }));

    private static OpenRunHeader Header(string runId = "run1") => new(
        runId,
        At,
        new EvalDevice("iPhone18,1", "26.0", "0.3.0"),
        "cfg-1",
        "a note",
        [new EvalRunPhoto("s1", "shelf"), new EvalRunPhoto("c1", "single")]);

    private static StoredEvalPhoto Photo(string title = "Star Fox 64") => new(
        [
            new EvalPhotoInput(
                "c1",
                ScanEngine.AppleModel,
                EvalOutcome.Ok,
                null,
                [new EvalReadItemInput(
                    new IdentifyItemInput("l1", "seg1", title, title, null, null, ItemCompleteness.CartOnly, WorkKind.Game, ReleaseFormat.GameCartridge, 1997, "Nintendo 64", null),
                    ResolvedStep.Model,
                    ScanTier.T1)],
                0,
                1500,
                800,
                ThermalState.Fair,
                1,
                At),
        ],
        [new SkippedPhoto("c1", "evals.thermal_required")],
        At);

    [Fact]
    public void A_run_comes_back_as_it_was_written_with_nothing_stored_yet()
    {
        var store = Store();

        store.Create(Header());

        var loaded = Store().Load()!; // another instance: what a restarted API reads
        loaded.RunId.ShouldBe("run1");
        loaded.State.ShouldBe(OpenRunState.Open);
        loaded.Device.ShouldBe(new EvalDevice("iPhone18,1", "26.0", "0.3.0"));
        loaded.Photos.Select(p => $"{p.Set}/{p.Id}").ShouldBe(["shelf/s1", "single/c1"]);
        loaded.BaselineNote.ShouldBe("a note");
        store.StoredIds().ShouldBeEmpty();
        store.GroundedIds().ShouldBeEmpty();
    }

    [Fact]
    public void The_person_who_opened_the_run_comes_back_with_it_and_a_run_written_without_one_reads_as_nobody()
    {
        var opener = Guid.CreateVersion7();
        var store = Store();

        store.Create(Header() with { OpenedBy = opener });
        Store().Load()!.OpenedBy.ShouldBe(opener);

        File.WriteAllText(
            Path.Combine(_golden.Results, "open-run", "run.json"),
            """{ "runId": "old", "openedAt": "2026-10-31T12:00:00+00:00", "device": { "model": "m", "osVersion": "o", "appVersion": "a" }, "configVersion": "c", "photos": [], "state": "Open" }""");
        var older = Store().Load()!;
        older.RunId.ShouldBe("old");
        older.OpenedBy.ShouldBeNull();
    }

    [Fact]
    public void A_stored_photo_comes_back_with_its_enums_items_and_notes()
    {
        var store = Store();
        store.Create(Header());

        store.SavePhoto("c1", Photo());
        var loaded = Store().LoadPhoto("c1")!;

        loaded.StoredAt.ShouldBe(At);
        loaded.Skipped.ShouldBe([new SkippedPhoto("c1", "evals.thermal_required")]);
        var result = loaded.Results.Single();
        result.Engine.ShouldBe(ScanEngine.AppleModel);
        result.Outcome.ShouldBe(EvalOutcome.Ok);
        result.Thermal.ShouldBe(ThermalState.Fair);
        result.Items!.Single().Read.Title.ShouldBe("Star Fox 64");
        result.Items![0].Read.Completeness.ShouldBe(ItemCompleteness.CartOnly);
        result.Items![0].Read.Kind.ShouldBe(WorkKind.Game);
        result.Items![0].Read.Format.ShouldBe(ReleaseFormat.GameCartridge);
        result.Items![0].Tier.ShouldBe(ScanTier.T1);
        result.CapturedAt.ShouldBe(At);
    }

    [Fact]
    public void Storing_a_photo_again_replaces_it_and_the_ids_are_listed_by_file_name()
    {
        var store = Store();
        store.Create(Header());

        store.SavePhoto("c1", Photo("Star Fox 64"));
        store.SavePhoto("c1", Photo("Zelda"));
        store.SavePhoto("s1", Photo("Doom"));

        store.StoredIds().ShouldBe(["c1", "s1"], ignoreOrder: true);
        store.LoadPhoto("c1")!.Results.Single().Items!.Single().Read.Title.ShouldBe("Zelda");
    }

    [Fact]
    public void A_grounded_file_marks_a_photo_done_and_deleting_it_puts_the_photo_back_in_the_queue()
    {
        var store = Store();
        store.Create(Header());
        store.SavePhoto("c1", Photo());

        store.SaveGrounded("c1", new GroundedPhotoFile([], [new SkippedPhoto("c1", "grounding_failed")]));

        store.GroundedIds().ShouldBe(["c1"]);
        store.LoadGrounded("c1")!.Skipped.ShouldBe([new SkippedPhoto("c1", "grounding_failed")]);
        store.DeleteGrounded("c1");
        store.GroundedIds().ShouldBeEmpty();
        store.LoadGrounded("c1").ShouldBeNull();
        store.StoredIds().ToList().ShouldBe(["c1"], "the photo itself is still stored");
    }

    [Fact]
    public void A_file_that_is_still_being_written_is_not_counted_and_a_file_that_cannot_be_read_is_none()
    {
        var store = Store();
        store.Create(Header());
        store.SavePhoto("c1", Photo());
        var folder = Path.Combine(_golden.Results, "open-run", "photos");
        File.WriteAllText(Path.Combine(folder, "s1.json.1a2b3c4d.tmp"), "{ half");
        File.WriteAllText(Path.Combine(folder, "bad.json"), "{ not json");
        File.WriteAllText(Path.Combine(folder, "not a valid id.json"), "{}");

        store.StoredIds().ShouldBe(["bad", "c1"], ignoreOrder: true);
        store.LoadPhoto("bad").ShouldBeNull("the worker drops it and the phone sends it again");
        store.LoadPhoto("c1").ShouldNotBeNull();
        store.LoadPhoto("s1").ShouldBeNull();
    }

    [Fact]
    public void A_write_leaves_no_temp_file_behind()
    {
        var store = Store();
        store.Create(Header());

        store.SavePhoto("c1", Photo());
        store.SaveGrounded("c1", new GroundedPhotoFile([], []));
        store.Save(Header() with { State = OpenRunState.Finishing });

        SyntheticGolden.Files(_golden.Results).ShouldAllBe(file => file.EndsWith(".json", StringComparison.Ordinal));
    }

    [Fact]
    public void Clearing_the_photos_keeps_the_header_and_discarding_removes_the_whole_run()
    {
        var store = Store();
        store.Create(Header());
        store.SavePhoto("c1", Photo());
        store.SaveGrounded("c1", new GroundedPhotoFile([], []));
        store.Save(Header() with { State = OpenRunState.Done });

        store.ClearPhotos();

        store.Load()!.State.ShouldBe(OpenRunState.Done);
        store.StoredIds().ShouldBeEmpty();
        store.GroundedIds().ShouldBeEmpty();
        store.Discard();
        store.Load().ShouldBeNull();
        Directory.Exists(Path.Combine(_golden.Results, "open-run")).ShouldBeFalse();
        Directory.Exists(_golden.Results).ShouldBeTrue("only the run goes, not the results folder");
    }

    [Fact]
    public void Creating_a_run_throws_away_the_one_before_it_with_all_its_photos()
    {
        var store = Store();
        store.Create(Header("old"));
        store.SavePhoto("c1", Photo());
        store.SaveGrounded("c1", new GroundedPhotoFile([], []));

        store.Create(Header("new"));

        store.Load()!.RunId.ShouldBe("new");
        store.StoredIds().ShouldBeEmpty();
        store.GroundedIds().ShouldBeEmpty();
    }

    [Fact]
    public void The_dated_result_files_beside_the_run_are_never_touched_by_discarding_it()
    {
        var store = Store();
        File.WriteAllText(Path.Combine(_golden.Results, "2026-10-31-abc1234.json"), "{}");
        File.WriteAllText(Path.Combine(_golden.Results, "latest-run.json"), "{}");
        store.Create(Header());

        store.Discard();

        File.Exists(Path.Combine(_golden.Results, "2026-10-31-abc1234.json")).ShouldBeTrue();
        File.Exists(Path.Combine(_golden.Results, "latest-run.json")).ShouldBeTrue();
    }

    [Fact]
    public void A_photo_id_that_is_not_plain_never_becomes_a_path()
    {
        var store = Store();
        store.Create(Header());

        Should.Throw<ArgumentException>(() => store.SavePhoto("..", Photo()));
        Should.Throw<ArgumentException>(() => store.SaveGrounded("a/b", new GroundedPhotoFile([], [])));
        Should.Throw<ArgumentException>(() => store.LoadPhoto("..\\x"));
    }

    [Fact]
    public void Without_a_results_folder_or_with_the_evals_off_the_store_is_not_configured_and_reads_nothing()
    {
        Store(resultsPath: " ").IsConfigured.ShouldBeFalse();
        var off = Store(enabled: false);

        off.IsConfigured.ShouldBeFalse();
        off.Load().ShouldBeNull();
        off.StoredIds().ShouldBeEmpty();
        off.GroundedIds().ShouldBeEmpty();
        off.LoadPhoto("c1").ShouldBeNull();
        off.Discard();
        Should.Throw<InvalidOperationException>(() => off.Create(Header()));
    }
}
