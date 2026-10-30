using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.TestSupport.Evals;
using static CollectionMaxxing.Recognition.Evals.EvalFixtures;

namespace CollectionMaxxing.Recognition.Evals;

/// <summary>
/// A wrong format on a single picture could not be explained: nothing recorded what the phone's cover cue saw. The phone now sends a short
/// line about it with the photo, the run keeps it, and the report shows it beside the picture's miss.
/// </summary>
public sealed class CoverCueReportTests : IDisposable
{
    private const string Cue = "cue=ready fmt=shape:dvd r=0.71 cov=0.61 rects=2 (1 frame-hugging) kind=film code=none";

    private readonly SyntheticGolden _golden = new();

    public void Dispose() => _golden.Dispose();

    private static GroundedPhoto Wrong(string id, string? coverCue) =>
        Photo(id, Apple, [NotFound()]) with { CoverCue = coverCue };

    private EvaluatedRun EvaluateSingle(params GroundedPhoto[] photos)
    {
        foreach (var photo in photos)
        {
            _golden.AddSingle(photo.PhotoId, new SyntheticGolden.SingleItem("Star Fox 64", "game_cartridge", "cart_only", "loose_cartridge", "Nintendo 64"));
        }

        return EvalReport.Evaluate(RunOf(photos), TruthsOf(_golden), baseline: null, "abc1234", skipped: []);
    }

    private static string MissLine(EvaluatedRun run, string photoId) =>
        run.ReportMarkdown.Split('\n').Single(line => line.StartsWith($"- `{photoId}`", StringComparison.Ordinal)).TrimEnd('\r');

    [Fact]
    public void A_single_pictures_miss_line_shows_what_the_cover_cue_saw()
    {
        var run = EvaluateSingle(Wrong("c1", Cue));

        var line = MissLine(run, "c1");
        line.ShouldStartWith("- `c1` (single, miss): truth \"Star Fox 64\", read ");
        line.ShouldEndWith($", cover cue `{Cue}`");
    }

    [Fact]
    public void A_miss_of_a_photo_with_no_cue_reads_as_it_always_did()
    {
        var run = EvaluateSingle(Wrong("c1", null));

        var line = MissLine(run, "c1");
        line.ShouldStartWith("- `c1` (single, miss): truth \"Star Fox 64\", read ");
        line.ShouldNotContain("cover cue");
    }

    [Fact]
    public void Each_picture_shows_its_own_cue()
    {
        var run = EvaluateSingle(Wrong("c1", "cue=timeout"), Wrong("c2", Cue));

        MissLine(run, "c1").ShouldEndWith("cover cue `cue=timeout`");
        MissLine(run, "c2").ShouldEndWith($"cover cue `{Cue}`");
    }

    [Fact]
    public void The_miss_carries_the_cue_of_its_photo()
    {
        var run = EvaluateSingle(Wrong("c1", Cue));

        run.Misses.Single().CoverCue.ShouldBe(Cue);
    }

    [Fact]
    public void A_refused_read_also_shows_the_cue()
    {
        _golden.AddSingle("c1", new SyntheticGolden.SingleItem("Star Fox 64", "game_cartridge", "cart_only", "loose_cartridge", "Nintendo 64"));
        var refused = Photo("c1", Apple, [], outcome: "refused") with { CoverCue = "cue=failed" };

        var run = EvalReport.Evaluate(RunOf(refused), TruthsOf(_golden), baseline: null, "abc1234", skipped: []);

        MissLine(run, "c1").ShouldEndWith("cover cue `cue=failed`");
    }

    [Fact]
    public void A_backtick_in_a_cue_cannot_end_the_code_span_early()
    {
        var run = EvaluateSingle(Wrong("c1", "code=`x` kind=none"));

        MissLine(run, "c1").ShouldEndWith("cover cue `code='x' kind=none`");
    }

    [Fact]
    public void The_summary_that_is_safe_to_commit_does_not_carry_the_cue()
    {
        var run = EvaluateSingle(Wrong("c1", Cue));

        System.Text.Json.JsonSerializer.Serialize(run.Summary).ShouldNotContain("fmt=shape");
    }
}
