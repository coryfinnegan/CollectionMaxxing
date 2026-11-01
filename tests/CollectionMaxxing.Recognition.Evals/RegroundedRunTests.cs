using System.Text.Json;
using System.Text.Json.Nodes;
using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.TestSupport.Evals;
using static CollectionMaxxing.Recognition.Evals.EvalFixtures;

namespace CollectionMaxxing.Recognition.Evals;

/// <summary>
/// A run the PC scored again by itself, from the phone readings of an earlier run, says so: its summary names the run it came from and when that run
/// was read, and its report opens with a line about it. A run the phone made carries neither.
/// </summary>
public sealed class RegroundedRunTests : IDisposable
{
    private readonly SyntheticGolden _golden = new();

    private static readonly DateTimeOffset ReadAt = new(2026, 10, 30, 18, 45, 0, TimeSpan.Zero);

    public void Dispose() => _golden.Dispose();

    private EvaluatedRun Evaluate(EvalRegroundedFrom? from)
    {
        _golden.AddSingle("c1", new SyntheticGolden.SingleItem("Star Fox 64", "game_cartridge", "cart_only", "loose_cartridge", "Nintendo 64"));
        var run = RunOf(Photo("c1", Apple, [Matched("Star Fox 64")]));
        return EvalReport.Evaluate(run, TruthsOf(_golden), null, "bbbb222", [], regroundedFrom: from);
    }

    [Fact]
    public void A_re_scored_run_names_the_run_it_came_from_and_when_that_run_was_read()
    {
        var evaluated = Evaluate(new EvalRegroundedFrom("run0", ReadAt));

        evaluated.Summary.RegroundedFrom.ShouldBe(new EvalRegroundedFrom("run0", ReadAt));
        evaluated.Summary.BuildSha.ShouldBe("bbbb222");
    }

    [Fact]
    public void The_report_of_a_re_scored_run_opens_with_a_line_saying_the_phone_readings_of_that_run_were_scored_with_this_server_version()
    {
        var evaluated = Evaluate(new EvalRegroundedFrom("run0", ReadAt));

        var first = evaluated.ReportMarkdown.Split('\n')[0].TrimEnd('\r');

        first.ShouldStartWith("Re-scored the phone readings of run run0");
        first.ShouldContain("2026-10-30 18:45 UTC");
        first.ShouldContain("bbbb222");
        evaluated.ReportMarkdown.ShouldContain("# Eval report");
    }

    [Fact]
    public void A_phone_run_has_no_marker_and_its_report_opens_with_the_heading()
    {
        var evaluated = Evaluate(null);

        evaluated.Summary.RegroundedFrom.ShouldBeNull();
        evaluated.ReportMarkdown.ShouldStartWith("# Eval report");
        evaluated.ReportMarkdown.ShouldNotContain("Re-scored");
    }

    [Fact]
    public void The_marker_is_additive_in_the_result_file_so_a_file_written_before_it_still_reads()
    {
        var evaluated = Evaluate(null);
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var older = JsonSerializer.SerializeToNode(evaluated.Summary, web)!.AsObject();
        older.Remove("regroundedFrom").ShouldBeTrue("the field is written, as null");

        var read = older.Deserialize<EvalSummary>(web);

        read.ShouldNotBeNull();
        read.RegroundedFrom.ShouldBeNull();
        read.RunId.ShouldBe(evaluated.Summary.RunId);
    }
}
