using CollectionMaxxing.Application.Scanning.Artwork;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Infrastructure.Scanning;
using CollectionMaxxing.TestSupport;
using Microsoft.Extensions.Options;
using OpenCvSharp;

namespace CollectionMaxxing.Infrastructure.Tests.Scanning;

/// <summary>
/// The stage-2 verifier against the Suddenly You fixtures (#438): the photo-matching edition must score far above the
/// other edition and the unrelated cover, and the thresholds in <see cref="CoverVerifyOptions"/> must sit between the
/// measured matches and non-matches (227 and 159 inliers for the matching edition at a gentle and hard tilt, 24 for the
/// same-title other edition, 11 for the unrelated cover). If this fails, re-measure and re-tune: never adjust blind.
/// </summary>
public class SiftCoverVerifierTests(ITestOutputHelper output)
{
    private static readonly CoverVerifyOptions Options = new();

    private sealed class FixedScoring(ScoringOptions value) : IOptionsMonitor<ScoringOptions>
    {
        public ScoringOptions CurrentValue => value;

        public ScoringOptions Get(string? name) => value;

        public IDisposable OnChange(Action<ScoringOptions, string?> listener) => new Nop();

        private sealed class Nop : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private static SiftCoverVerifier Verifier() =>
        new(new FixedScoring(new ScoringOptions()));

    private static CoverQuad QuadOf(CoverFixtures.CaptureCorners corners) => new(
        new CoverPoint(corners.TopLeft.X / CoverFixtures.CaptureWidth, corners.TopLeft.Y / CoverFixtures.CaptureHeight),
        new CoverPoint(corners.TopRight.X / CoverFixtures.CaptureWidth, corners.TopRight.Y / CoverFixtures.CaptureHeight),
        new CoverPoint(corners.BottomRight.X / CoverFixtures.CaptureWidth, corners.BottomRight.Y / CoverFixtures.CaptureHeight),
        new CoverPoint(corners.BottomLeft.X / CoverFixtures.CaptureWidth, corners.BottomLeft.Y / CoverFixtures.CaptureHeight));

    [Fact]
    public async Task The_photo_matching_edition_scores_far_above_the_other()
    {
        using var edition2001 = CoverFixtures.Edition2001();
        using var edition2003 = CoverFixtures.Edition2003();
        using var other = CoverFixtures.OtherBook();
        var corners = CoverFixtures.GentleCorners();
        var capture = CoverFixtures.CaptureOf(edition2001, corners);
        var quad = QuadOf(corners);
        var verifier = Verifier();

        var match = await verifier.CompareAsync(capture, CoverFixtures.PngOf(edition2001), quad, default);
        var sameTitle = await verifier.CompareAsync(capture, CoverFixtures.PngOf(edition2003), quad, default);
        var unrelated = await verifier.CompareAsync(capture, CoverFixtures.PngOf(other), quad, default);
        output.WriteLine($"match={match.Inliers} same-title={sameTitle.Inliers} unrelated={unrelated.Inliers}");

        match.Inliers.ShouldBeGreaterThan(Options.StrongInliers);
        sameTitle.Inliers.ShouldBeLessThan(Options.WeakInliers);
        unrelated.Inliers.ShouldBeLessThan(Options.WeakInliers);
        match.Inliers.ShouldBeGreaterThan(sameTitle.Inliers * 3);
    }

    [Fact]
    public async Task Warping_by_the_quad_recovers_a_tilted_capture()
    {
        using var edition = CoverFixtures.Edition2001();
        var corners = CoverFixtures.HardCorners();
        var capture = CoverFixtures.CaptureOf(edition, corners);
        var cover = CoverFixtures.PngOf(edition);
        var verifier = Verifier();

        var flat = await verifier.CompareAsync(capture, cover, QuadOf(corners), default);
        var raw = await verifier.CompareAsync(capture, cover, null, default);
        output.WriteLine($"warped={flat.Inliers} raw={raw.Inliers}");

        flat.Inliers.ShouldBeGreaterThan(Options.StrongInliers);
        flat.Inliers.ShouldBeGreaterThanOrEqualTo(raw.Inliers);
    }

    [Fact]
    public async Task A_blank_cover_matches_nothing()
    {
        using var edition = CoverFixtures.Edition2001();
        var corners = CoverFixtures.GentleCorners();
        var capture = CoverFixtures.CaptureOf(edition, corners);

        var comparison = await Verifier().CompareAsync(capture, CoverFixtures.PngOf(CoverFixtures.Blank()), QuadOf(corners), default);

        comparison.Inliers.ShouldBe(0);
    }

    [Fact]
    public async Task Undecodable_bytes_fail_loudly()
    {
        using var edition = CoverFixtures.Edition2001();

        await Should.ThrowAsync<InvalidOperationException>(() =>
            Verifier().CompareAsync(new byte[] { 1, 2, 3, 4 }, CoverFixtures.PngOf(edition), null, default));
    }
}
