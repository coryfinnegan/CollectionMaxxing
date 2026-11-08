using CollectionMaxxing.Application.Scanning.Identify;

namespace CollectionMaxxing.Application.Tests.Scanning.Identify;

// A line the phone sent as the creator that the sanitiser kept no creator from ("THE RESCUE" under "FREE WILLY 3") reaches the scorer
// as ReadItem.CreatorLine, never the providers.
public class ReadFactsCreatorLineTests
{
    private static ReadFacts Facts(string? creator, string? creatorAsRead) =>
        new("FREE WILLY 3", "FREE WILLY 3", creator, null, null, null, null, null, null, CreatorAsRead: creatorAsRead);

    [Fact]
    public void A_creator_line_the_sanitiser_kept_no_creator_from_reaches_the_scorer()
    {
        Facts(null, "THE RESCUE").ToReadItem().CreatorLine.ShouldBe("THE RESCUE");
    }

    [Fact]
    public void A_creator_the_sanitiser_kept_is_no_creator_line()
    {
        Facts("Lewis Carroll", "Lewis Carroll").ToReadItem().CreatorLine.ShouldBeNull();
    }

    [Fact]
    public void The_creator_line_is_not_part_of_the_ask()
    {
        Facts(null, "THE RESCUE").ToQuery().ShouldBe(Facts(null, null).ToQuery());
    }
}
