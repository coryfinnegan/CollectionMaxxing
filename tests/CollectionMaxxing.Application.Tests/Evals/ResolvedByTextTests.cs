using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.Domain.Scanning;

namespace CollectionMaxxing.Application.Tests.Evals;

/// <summary>What settled an item when the phone did not say: the engine that read it.</summary>
public class ResolvedByTextTests
{
    [Theory]
    [InlineData(ScanEngine.AppleModel, "model")]
    [InlineData(ScanEngine.FmText, "model")]
    [InlineData(ScanEngine.VisionPipeline, "vision_text")]
    public void An_unspoken_step_defaults_to_the_engine_that_read_it(ScanEngine engine, string expected)
    {
        ResolvedByText.For(engine, null).ShouldBe(expected);
    }

    [Fact]
    public void What_the_phone_said_wins_over_the_engine_default()
    {
        ResolvedByText.For(ScanEngine.FmText, ResolvedStep.VisionText).ShouldBe("vision_text");
        ResolvedByText.For(ScanEngine.VisionPipeline, ResolvedStep.Model).ShouldBe("model");
        ResolvedByText.For(ScanEngine.AppleModel, ResolvedStep.Artwork).ShouldBe("artwork");
        ResolvedByText.For(ScanEngine.AppleModel, ResolvedStep.FixIt).ShouldBe("fix_it");
    }
}
