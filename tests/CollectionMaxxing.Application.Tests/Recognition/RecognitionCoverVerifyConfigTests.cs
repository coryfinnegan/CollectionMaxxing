using CollectionMaxxing.Application.Recognition;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Application.Tests.Recognition;

/// <summary>The cover-verification settings of the scoring config (#438 stage 2): defaults, the override file, and what is refused.</summary>
public class RecognitionCoverVerifyConfigTests
{
    private static RecognitionBuild Build(string? overrideJson = null) => RecognitionConfigBuilder.Build(RecognitionDefaults.Json, overrideJson);

    [Fact]
    public void The_defaults_carry_the_documented_cover_verification_settings()
    {
        var cover = Build().Scoring!.CoverVerify;

        cover.ShouldBe(new CoverVerifyOptions());
        cover.LoweRatio.ShouldBe(0.75);
        cover.RansacReprojThreshold.ShouldBe(3.0);
        cover.WeakInliers.ShouldBe(10);
        cover.StrongInliers.ShouldBe(30);
        cover.PreselectMargin.ShouldBe(0.10);
        cover.VisualWeight.ShouldBe(0.5);
        cover.MaxDimension.ShouldBe(1024);
    }

    [Fact]
    public void An_override_can_change_one_cover_setting_and_leaves_the_rest()
    {
        var build = Build("""{ "scoring": { "coverVerify": { "preselectMargin": 0.2 } } }""");

        build.Problems.ShouldBeEmpty();
        build.Scoring!.CoverVerify.PreselectMargin.ShouldBe(0.2);
        build.Scoring.CoverVerify.StrongInliers.ShouldBe(30);
        build.Scoring.Art.ShouldBe(new ArtScoringOptions());
    }

    [Theory]
    [InlineData("loweRatio", "0.4")]
    [InlineData("loweRatio", "0.96")]
    [InlineData("ransacReprojThreshold", "0.5")]
    [InlineData("ransacReprojThreshold", "11")]
    [InlineData("weakInliers", "0")]
    [InlineData("weakInliers", "1001")]
    [InlineData("strongInliers", "1")]
    [InlineData("strongInliers", "10001")]
    [InlineData("preselectMargin", "-0.1")]
    [InlineData("preselectMargin", "1.5")]
    [InlineData("visualWeight", "-0.1")]
    [InlineData("visualWeight", "1.5")]
    [InlineData("maxDimension", "255")]
    [InlineData("maxDimension", "4097")]
    public void A_cover_setting_out_of_range_is_refused_whole_and_named(string key, string value)
    {
        var build = Build($$"""{ "scoring": { "coverVerify": { "{{key}}": {{value}} } } }""");

        build.IsValid.ShouldBeFalse();
        build.Scoring.ShouldBeNull();
        build.Problems.ShouldContain(p => p.Contains($"scoring.coverVerify.{key}", StringComparison.Ordinal));
    }

    [Fact]
    public void Strong_inliers_at_or_below_weak_inliers_are_refused()
    {
        var build = Build("""{ "scoring": { "coverVerify": { "weakInliers": 30, "strongInliers": 30 } } }""");

        build.IsValid.ShouldBeFalse();
        build.Problems.ShouldContain(p => p.Contains("scoring.coverVerify.strongInliers", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unknown_cover_setting_is_refused()
    {
        Build("""{ "scoring": { "coverVerify": { "siftOctaves": 3 } } }""").IsValid.ShouldBeFalse();
    }
}
