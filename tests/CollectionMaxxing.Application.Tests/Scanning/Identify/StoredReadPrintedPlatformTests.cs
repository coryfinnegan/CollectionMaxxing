using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Scanning;

namespace CollectionMaxxing.Application.Tests.Scanning.Identify;

// A read the providers ran out of time for is asked again later: the platforms its shot printed are kept in the stored read.
public class StoredReadPrintedPlatformTests
{
    private static readonly ReadFacts Ico = new("ICO", "ICO", null, null, null, null, null, null, null);

    [Fact]
    public void A_pending_read_is_asked_again_with_the_platforms_its_shot_printed()
    {
        var json = StoredRead.Serialize(Ico with { PlatformContext = "PS2|Switch" }, ScoringProfileKind.Cover);

        var (facts, _) = StoredRead.From(new ScanCandidate { VisibleText = "ICO", InferredJson = json });

        facts.PlatformContext.ShouldBe("PS2|Switch");
    }

    [Fact]
    public void A_read_whose_shot_printed_no_platform_stores_none()
    {
        var json = StoredRead.Serialize(Ico, ScoringProfileKind.Cover);

        json.ShouldNotContain("snapPlatforms");
        StoredRead.From(new ScanCandidate { VisibleText = "ICO", InferredJson = json }).Facts.PlatformContext.ShouldBeNull();
    }
}
