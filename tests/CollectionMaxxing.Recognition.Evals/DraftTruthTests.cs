using System.Diagnostics;
using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.TestSupport.Evals;
using static CollectionMaxxing.Recognition.Evals.EvalFixtures;

namespace CollectionMaxxing.Recognition.Evals;

/// <summary>Draft truth files and the skeleton script: neither is ever truth until the owner renames it.</summary>
public sealed class DraftTruthTests : IDisposable
{
    private readonly SyntheticGolden _golden = new();

    public void Dispose() => _golden.Dispose();

    [Fact]
    public void A_picture_with_only_a_draft_has_no_truth_and_is_never_scored()
    {
        _golden.AddShelf("s1", null);
        _golden.AddFile(
            "shelf", "s1.truth.draft.json",
            TruthFiles.WriteShelfDraft([new ShelfTruthItem("Star Fox 64", "game", "Nintendo 64", 1997)], 0));
        var set = GoldenSetOf(_golden);

        set.ReadTruth("s1").ShouldBeNull();
        set.HasDraft("s1").ShouldBeTrue();
        set.List().Single().HasTruth.ShouldBeFalse();
        TruthsOf(_golden).ShouldBeEmpty();
        EvalScorer.Score([Photo("s1", Apple, [Matched("Star Fox 64")])], TruthsOf(_golden)).Engines.ShouldBeEmpty();
    }

    [Fact]
    public void A_draft_is_marked_as_a_draft_and_becomes_truth_only_when_renamed()
    {
        _golden.AddShelf("s1", null);
        var draft = TruthFiles.WriteShelfDraft([new ShelfTruthItem("Star Fox 64", "game", "Nintendo 64", 1997)], 1);
        draft.ShouldContain("\"draft\": true");
        var path = _golden.AddFile("shelf", "s1.truth.draft.json", draft);

        File.Move(path, Path.Combine(_golden.Golden, "shelf", "s1.truth.json"));

        var truth = TruthsOf(_golden)["s1"];
        truth.Shelf!.Items.Single().Title.ShouldBe("Star Fox 64");
        truth.Shelf.Unreadable.ShouldBe(1);
    }

    [Fact]
    public void A_draft_is_written_beside_the_picture_and_never_replaces_one_that_is_there()
    {
        _golden.AddShelf("s1", null);
        var set = GoldenSetOf(_golden);

        set.TryWriteDraft("s1", "first").ShouldBeTrue();
        set.TryWriteDraft("s1", "second").ShouldBeFalse();

        File.ReadAllText(Path.Combine(_golden.Golden, "shelf", "s1.truth.draft.json")).ShouldBe("first");
    }

    [Fact]
    public void A_truth_file_that_is_not_valid_truth_is_no_truth()
    {
        _golden.AddShelf("s1", null);
        _golden.AddFile("shelf", "s1.truth.json", "{ \"items\": \"not a list\" }");
        _golden.AddSingle("c1", null);
        _golden.AddFile("single", "c1.truth.json", "{ \"title\": \"\" }");

        TruthsOf(_golden).ShouldBeEmpty();
        GoldenSetOf(_golden).List().ShouldAllBe(p => !p.HasTruth);
    }

    [Fact]
    public async Task The_skeleton_script_writes_a_draft_for_each_picture_without_truth_and_touches_nothing_else()
    {
        _golden.AddShelf("s1", [ShelfItem("Star Fox 64")]);
        _golden.AddShelf("s2", null);
        _golden.AddSingle("c1", null);
        _golden.AddSingle("c2", null, ".heic");
        _golden.AddSingle("c3", null);
        _golden.AddFile("single", "c3.truth.draft.json", "MY EDITS");
        _golden.AddFile("single", "notes.txt", "keep");
        _golden.AddFile("single", "not a valid id.jpg", "keep");
        var before = SyntheticGolden.Files(_golden.Golden);

        var (exitCode, output) = await RunScriptAsync(_golden.Golden);

        exitCode.ShouldBe(0, output);
        var after = SyntheticGolden.Files(_golden.Golden);
        after.Except(before).Select(f => f.Replace(Path.DirectorySeparatorChar, '/')).Order().ToList()
            .ShouldBe(["shelf/s2.truth.draft.json", "single/c1.truth.draft.json", "single/c2.truth.draft.json"]);
        after.Count.ShouldBe(before.Count + 3); // nothing was removed or renamed
        File.ReadAllText(Path.Combine(_golden.Golden, "single", "c3.truth.draft.json")).ShouldBe("MY EDITS");
    }

    [Fact]
    public async Task Skeletons_are_drafts_so_they_are_not_truth_and_leave_existing_files_as_they_were()
    {
        _golden.AddShelf("s2", null);
        _golden.AddSingle("c1", null);
        _golden.AddSingle("c3", null);
        var edited = _golden.AddFile("single", "c3.truth.draft.json", "MY EDITS");

        var (exitCode, output) = await RunScriptAsync(_golden.Golden);

        exitCode.ShouldBe(0, output);
        File.ReadAllText(edited).ShouldBe("MY EDITS");
        File.ReadAllText(Path.Combine(_golden.Golden, "single", "c1.truth.draft.json")).ShouldContain("\"draft\": true");
        TruthsOf(_golden).ShouldBeEmpty();
        GoldenSetOf(_golden).List().ShouldAllBe(p => !p.HasTruth);
    }

    [Fact]
    public async Task The_skeleton_script_accepts_one_set_folder_and_refuses_a_folder_that_is_neither()
    {
        _golden.AddSingle("c1", null);
        var stranger = Path.Combine(_golden.Root, "stranger");
        Directory.CreateDirectory(stranger);
        File.WriteAllText(Path.Combine(stranger, "x.jpg"), "x");

        var one = await RunScriptAsync(Path.Combine(_golden.Golden, "single"));
        var refused = await RunScriptAsync(stranger);

        one.ExitCode.ShouldBe(0, one.Output);
        File.Exists(Path.Combine(_golden.Golden, "single", "c1.truth.draft.json")).ShouldBeTrue();
        refused.ExitCode.ShouldNotBe(0);
        SyntheticGolden.Files(stranger).ShouldBe(["x.jpg"]);
    }

    [Fact]
    public async Task The_skeleton_script_does_not_follow_a_set_folder_that_is_a_junction_to_outside()
    {
        _golden.AddShelf("s1", null);
        var outside = Path.Combine(_golden.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "o1.jpg"), "x");
        var single = Path.Combine(_golden.Golden, "single");
        Directory.Delete(single, true);
        FolderLink.Create(single, outside);

        var (exitCode, output) = await RunScriptAsync(_golden.Golden);

        exitCode.ShouldBe(0, output);
        SyntheticGolden.Files(outside).ShouldBe(["o1.jpg"]);
        File.Exists(Path.Combine(_golden.Golden, "shelf", "s1.truth.draft.json")).ShouldBeTrue();
    }

    private static async Task<(int ExitCode, string Output)> RunScriptAsync(string folder)
    {
        var script = Path.Combine(RepoRoot(), "scripts", "eval-truth-stub.ps1");
        var start = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", script, folder })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start pwsh (PowerShell 7).");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await error);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CollectionMaxxing.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Can't find the repository root (CollectionMaxxing.slnx).");
    }
}
