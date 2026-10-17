using System.Diagnostics;

namespace CollectionMaxxing.Recognition.Evals;

/// <summary>
/// The repo is public and docs/09 names <c>evals/results/</c> as the results folder. The report, the latest run and the drafts carry the
/// owner's titles, so git must ignore them; only the dated aggregate files may ever be committed.
/// </summary>
public sealed class ResultsFolderIgnoreTests
{
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CollectionMaxxing.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Can't find the repository root (CollectionMaxxing.slnx).");
    }

    /// <summary>True when git would ignore <paramref name="relativePath"/> (the file need not exist).</summary>
    private static bool IsIgnored(string relativePath)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = RepoRoot(), RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("check-ignore");
        start.ArgumentList.Add("-q");
        start.ArgumentList.Add(relativePath);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        return process.ExitCode == 0; // 0: ignored, 1: not ignored
    }

    [Theory]
    [InlineData("evals/results/report.md")]
    [InlineData("evals/results/latest-run.json")]
    [InlineData("evals/results/drafts/x.json")]
    [InlineData("evals/results/drafts/2026-10-17-abc1234.json")]
    [InlineData("evals/results/notes.json")]
    public void Anything_in_the_results_folder_that_can_hold_titles_is_ignored_by_git(string path) =>
        IsIgnored(path).ShouldBeTrue(path);

    [Theory]
    [InlineData("evals/results/2026-10-17-abc1234.json")]
    [InlineData("evals/results/2026-01-02-0123456.json")]
    public void A_dated_aggregate_file_can_be_committed(string path) =>
        IsIgnored(path).ShouldBeFalse(path);
}
