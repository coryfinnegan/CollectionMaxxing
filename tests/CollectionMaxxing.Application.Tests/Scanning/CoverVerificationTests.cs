using CollectionMaxxing.Application.Scanning.Artwork;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Application.Tests.Scanning;

/// <summary>The stage-2 rules (#438): inliers to a visual score, the visual blended with the text, and when the top cover is preselected.</summary>
public class CoverVerificationTests
{
    private static readonly CoverVerifyOptions Options = new();

    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(40, 0.0)]
    [InlineData(70, 0.5)]
    [InlineData(100, 1.0)]
    [InlineData(300, 1.0)]
    public void Inliers_map_to_a_visual_score_between_weak_and_strong(int inliers, double visual)
    {
        CoverVerification.VisualScore(inliers, Options).ShouldBe(visual);
    }

    [Fact]
    public void The_combined_score_blends_text_and_visual()
    {
        CoverVerification.CombinedScore(0.78, 1.0, Options).ShouldBe(0.89);
        CoverVerification.CombinedScore(0.78, 0.0, Options).ShouldBe(0.39);
    }

    [Fact]
    public void A_strong_cover_with_a_clear_margin_is_preselected()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var (ranked, preselected) = CoverVerification.Rank(
            [(first, 0.78125, 120), (second, 0.78125, 4)], Options);

        ranked.Select(c => c.ReleaseId).ShouldBe([first, second]);
        preselected.ShouldBe(first);
    }

    [Fact]
    public void Without_a_margin_the_covers_rank_but_none_is_preselected()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        // Both covers match the capture (same art on two editions): ranked by combined score, no pick.
        var (ranked, preselected) = CoverVerification.Rank(
            [(first, 0.78125, 120), (second, 0.78125, 118)], Options);

        ranked.Select(c => c.ReleaseId).ShouldBe([first, second]);
        preselected.ShouldBeNull();
    }

    [Fact]
    public void A_relative_best_that_matches_nothing_is_never_preselected()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var (_, preselected) = CoverVerification.Rank(
            [(first, 0.9, 9), (second, 0.6, 2)], Options);

        preselected.ShouldBeNull();
    }

    [Fact]
    public void A_lone_strong_cover_is_preselected()
    {
        var only = Guid.NewGuid();

        var (_, preselected) = CoverVerification.Rank([(only, 0.7, 150)], Options);

        preselected.ShouldBe(only);
    }

    [Fact]
    public void A_full_picture_quad_is_usable()
    {
        CoverQuad.Validate(new CoverQuad(new(0, 0), new(1, 0), new(1, 1), new(0, 1))).ShouldBeNull();
    }

    [Theory]
    [InlineData(-0.1, 0, 1, 0, 1, 1, 0, 1)] // outside the picture
    [InlineData(0, 0, 0, 0, 0, 0, 0, 0)] // no area
    [InlineData(0, 0, 1, 1, 0, 1, 1, 0)] // a bowtie, not a quad
    public void A_degenerate_quad_is_refused(
        double x1, double y1, double x2, double y2, double x3, double y3, double x4, double y4)
    {
        var quad = new CoverQuad(new(x1, y1), new(x2, y2), new(x3, y3), new(x4, y4));

        CoverQuad.Validate(quad).ShouldNotBeNull();
    }
}
