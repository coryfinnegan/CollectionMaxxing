using CollectionMaxxing.Domain.Scanning;

namespace CollectionMaxxing.Domain.Tests.Scanning;

public class GuidanceCodesTests
{
    private const string ParsableCode = "012345678905";

    public static TheoryData<FallbackReason, GuidanceCode> ReshootSignals() => new()
    {
        { FallbackReason.MoveCloser, GuidanceCode.MoveCloser },
        { FallbackReason.SplitSegment, GuidanceCode.SplitSegment },
        { FallbackReason.HoldSteady, GuidanceCode.HoldSteady },
        { FallbackReason.KillGlare, GuidanceCode.KillGlare },
    };

    [Theory]
    [MemberData(nameof(ReshootSignals))]
    public void A_reshoot_signal_is_the_guidance_while_the_row_is_unresolved(FallbackReason signal, GuidanceCode code)
    {
        GuidanceCodes.For(signal, CandidateResolution.NotFound, CandidateSource.PictureAppleModel, null, quotaSpent: false)
            .ShouldBe(code);
        GuidanceCodes.For(signal, CandidateResolution.Pending, CandidateSource.PictureAppleModel, null, quotaSpent: false)
            .ShouldBe(code);
    }

    [Fact]
    public void An_engine_fallback_alone_is_not_reshoot_guidance()
    {
        GuidanceCodes.For(FallbackReason.Timeout, CandidateResolution.NotFound, CandidateSource.PictureAppleModel, null, quotaSpent: false)
            .ShouldBe(GuidanceCode.ShootCover);
    }

    [Fact]
    public void An_ambiguous_row_says_pick_one_whatever_the_signal_was()
    {
        GuidanceCodes.For(FallbackReason.MoveCloser, CandidateResolution.Ambiguous, CandidateSource.PictureAppleModel, null, quotaSpent: false)
            .ShouldBe(GuidanceCode.PickMatch);
        GuidanceCodes.For(null, CandidateResolution.Ambiguous, CandidateSource.Barcode, ParsableCode, quotaSpent: false)
            .ShouldBe(GuidanceCode.PickMatch);
    }

    [Fact]
    public void A_matched_row_needs_no_guidance()
    {
        GuidanceCodes.For(FallbackReason.MoveCloser, CandidateResolution.Matched, CandidateSource.PictureAppleModel, null, quotaSpent: false)
            .ShouldBeNull();
    }

    [Fact]
    public void A_not_found_row_without_a_signal_says_shoot_the_cover()
    {
        GuidanceCodes.For(null, CandidateResolution.NotFound, CandidateSource.PictureAppleModel, null, quotaSpent: false)
            .ShouldBe(GuidanceCode.ShootCover);
        GuidanceCodes.For(null, CandidateResolution.NotFound, CandidateSource.Barcode, ParsableCode, quotaSpent: false)
            .ShouldBe(GuidanceCode.ShootCover);
    }

    [Fact]
    public void A_picture_that_gave_up_says_shoot_the_cover()
    {
        GuidanceCodes.For(null, CandidateResolution.Error, CandidateSource.PictureText, null, quotaSpent: false)
            .ShouldBe(GuidanceCode.ShootCover);
    }

    [Fact]
    public void An_unreadable_barcode_says_scan_it_again_but_a_parsable_one_that_gave_up_says_shoot_the_cover()
    {
        GuidanceCodes.For(null, CandidateResolution.Error, CandidateSource.Barcode, "not-a-code", quotaSpent: false)
            .ShouldBe(GuidanceCode.ScanBarcode);
        GuidanceCodes.For(null, CandidateResolution.Error, CandidateSource.Barcode, ParsableCode, quotaSpent: false)
            .ShouldBe(GuidanceCode.ShootCover);
    }

    [Fact]
    public void A_quota_wait_says_shoot_the_cover_but_a_plain_wait_says_nothing_yet()
    {
        GuidanceCodes.For(null, CandidateResolution.Pending, CandidateSource.Barcode, ParsableCode, quotaSpent: true)
            .ShouldBe(GuidanceCode.ShootCover);
        GuidanceCodes.For(null, CandidateResolution.Pending, CandidateSource.Barcode, ParsableCode, quotaSpent: false)
            .ShouldBeNull();
    }

    [Theory]
    [InlineData(GuidanceCode.MoveCloser, true)]
    [InlineData(GuidanceCode.SplitSegment, true)]
    [InlineData(GuidanceCode.HoldSteady, true)]
    [InlineData(GuidanceCode.KillGlare, true)]
    [InlineData(GuidanceCode.ShootCover, false)]
    [InlineData(GuidanceCode.PickMatch, false)]
    [InlineData(GuidanceCode.ScanBarcode, false)]
    public void Only_the_phone_signals_count_as_reshoot_guidance(GuidanceCode code, bool reshoot)
    {
        GuidanceCodes.IsReshoot(code).ShouldBe(reshoot);
    }
}
