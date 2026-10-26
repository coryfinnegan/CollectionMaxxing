using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Scanning;

namespace CollectionMaxxing.Domain.Tests.Scanning;

public class ScanTierTests
{
    [Theory]
    [InlineData(ScanTier.T0, "T0")]
    [InlineData(ScanTier.T1, "T1")]
    [InlineData(ScanTier.T2, "T2")]
    public void Tiers_are_spelled_as_the_design_writes_them(ScanTier tier, string text)
    {
        EnumText.ToText(tier).ShouldBe(text);
        EnumText.Parse<ScanTier>(text).ShouldBe(tier);
    }

    [Fact]
    public void All_tiers_are_known()
    {
        EnumText.All<ScanTier>().ShouldBe(["T0", "T1", "T2"]);
    }
}
