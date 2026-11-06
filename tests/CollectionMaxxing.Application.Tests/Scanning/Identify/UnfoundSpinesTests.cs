using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Application.Tests.Scanning.Identify;

// A shelf spine the catalogue found nothing for, after every retry, is skipped. Only the spine profile and only the final
// verdict NotFound: a cover, a pending read and an ambiguous read all keep their row.
public class UnfoundSpinesTests
{
    private static UnfoundRead Spine(ResolutionVerdict verdict) => new(ScoringProfileKind.ShelfSpine, verdict);

    [Fact]
    public void A_spine_read_the_catalogue_found_nothing_for_is_skipped()
    {
        var reads = new[] { Spine(ResolutionVerdict.Matched), Spine(ResolutionVerdict.NotFound), Spine(ResolutionVerdict.NotFound) };

        UnfoundSpines.Of(reads).ShouldBe([1, 2]);
    }

    [Fact]
    public void A_cover_read_that_found_nothing_still_shows()
    {
        var reads = new[] { new UnfoundRead(ScoringProfileKind.Cover, ResolutionVerdict.NotFound) };

        UnfoundSpines.Of(reads).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(ResolutionVerdict.Pending)]
    [InlineData(ResolutionVerdict.Ambiguous)]
    [InlineData(ResolutionVerdict.Matched)]
    public void A_spine_read_that_found_something_or_is_still_pending_is_not_skipped(ResolutionVerdict verdict)
    {
        UnfoundSpines.Of([Spine(verdict)]).ShouldBeEmpty();
    }
}
