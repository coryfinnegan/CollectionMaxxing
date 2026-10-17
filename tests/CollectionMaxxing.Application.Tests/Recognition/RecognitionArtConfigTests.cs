using CollectionMaxxing.Application.Recognition;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Application.Tests.Recognition;

/// <summary>The artwork settings of the scoring config (SCAN-2b): defaults, the override file, and what is refused.</summary>
public class RecognitionArtConfigTests
{
    private static RecognitionBuild Build(string? overrideJson = null) => RecognitionConfigBuilder.Build(RecognitionDefaults.Json, overrideJson);

    [Fact]
    public void The_defaults_carry_the_documented_art_settings()
    {
        var art = Build().Scoring!.Art;

        art.ShouldBe(new ArtScoringOptions());
        art.CloseCallMargin.ShouldBe(0.15);
        art.DistanceScale.ShouldBe(1.0);
        art.MaxDistance.ShouldBe(100.0);
    }

    [Fact]
    public void An_override_can_change_one_art_setting_and_leaves_the_rest()
    {
        var build = Build("""{ "scoring": { "art": { "closeCallMargin": 0.25 } } }""");

        build.Problems.ShouldBeEmpty();
        build.Scoring!.Art.CloseCallMargin.ShouldBe(0.25);
        build.Scoring.Art.DistanceScale.ShouldBe(1.0);
        build.Scoring.Cover.ShouldBe(new ScoringOptions().Cover);
    }

    [Theory]
    [InlineData("closeCallMargin", "-0.1")]
    [InlineData("closeCallMargin", "1.5")]
    [InlineData("distanceScale", "0")]
    [InlineData("distanceScale", "-1")]
    [InlineData("distanceScale", "100000")]
    [InlineData("maxDistance", "0")]
    [InlineData("maxDistance", "-5")]
    [InlineData("maxDistance", "1e9")]
    public void An_art_setting_out_of_range_is_refused_whole_and_named(string key, string value)
    {
        var build = Build($$"""{ "scoring": { "art": { "{{key}}": {{value}} } } }""");

        build.IsValid.ShouldBeFalse();
        build.Scoring.ShouldBeNull();
        build.Problems.ShouldContain(p => p.Contains($"scoring.art.{key}", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unknown_art_setting_is_refused()
    {
        Build("""{ "scoring": { "art": { "closeCall": 0.2 } } }""").IsValid.ShouldBeFalse();
    }
}
