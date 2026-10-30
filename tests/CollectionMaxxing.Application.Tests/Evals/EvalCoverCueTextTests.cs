using CollectionMaxxing.Application.Evals;

namespace CollectionMaxxing.Application.Tests.Evals;

/// <summary>
/// The line the phone sends about what a single cover's cue saw. It is for the eval record only, so a bad one is never a reason to refuse
/// a run: it is cleaned and cut instead.
/// </summary>
public class EvalCoverCueTextTests
{
    private const string Cue = "cue=ready fmt=shape:bluray r=0.86 cov=0.73 rects=1 kind=film code=none";

    [Fact]
    public void A_cue_the_phone_sent_is_kept_as_it_is()
    {
        EvalCoverCue.Clean(Cue).ShouldBe(Cue);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    public void A_missing_or_blank_cue_is_none(string? sent)
    {
        EvalCoverCue.Clean(sent).ShouldBeNull();
    }

    [Fact]
    public void A_cue_over_the_limit_is_cut_to_it_not_refused()
    {
        var cleaned = EvalCoverCue.Clean(new string('x', EvalCoverCue.MaxLength + 200));

        cleaned.ShouldBe(new string('x', EvalCoverCue.MaxLength));
    }

    [Fact]
    public void A_cue_at_the_limit_is_kept_whole()
    {
        var atLimit = new string('y', EvalCoverCue.MaxLength);

        EvalCoverCue.Clean(atLimit).ShouldBe(atLimit);
    }

    [Fact]
    public void The_limit_is_300_characters()
    {
        EvalCoverCue.MaxLength.ShouldBe(300);
    }

    [Fact]
    public void Line_breaks_and_other_control_characters_become_spaces_so_the_report_keeps_one_line()
    {
        EvalCoverCue.Clean("cue=ready\r\nfmt=none\tkind=none\u0007code=none").ShouldBe("cue=ready  fmt=none kind=none code=none");
    }

    [Fact]
    public void Backticks_are_replaced_so_the_cue_cannot_break_out_of_the_report_s_code_span()
    {
        EvalCoverCue.Clean("code=`rm`").ShouldBe("code='rm'");
    }

    [Fact]
    public void Surrounding_space_is_trimmed()
    {
        EvalCoverCue.Clean("  " + Cue + " \n").ShouldBe(Cue);
    }

    [Fact]
    public void A_cut_never_splits_a_surrogate_pair()
    {
        var emoji = "\U0001F600";
        var sent = new string('z', EvalCoverCue.MaxLength - 1) + emoji;

        var cleaned = EvalCoverCue.Clean(sent)!;

        cleaned.Length.ShouldBeLessThanOrEqualTo(EvalCoverCue.MaxLength);
        char.IsHighSurrogate(cleaned[^1]).ShouldBeFalse();
    }
}
