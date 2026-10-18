using CollectionMaxxing.Application.Items;

namespace CollectionMaxxing.Application.Tests.Items;

public class ItemVersionTests
{
    [Theory]
    [InlineData(null, 1u, false)]
    [InlineData("", 1u, false)]
    [InlineData("   ", 1u, false)]
    [InlineData("\"1\"", 1u, true)]
    [InlineData("\"2\"", 1u, false)]
    [InlineData("W/\"1\"", 1u, true)]
    [InlineData("\"2\", \"1\"", 1u, true)]
    [InlineData("*", 7u, true)]
    [InlineData("nonsense", 1u, false)]
    public void If_Match_matches_only_the_current_version(string? header, uint current, bool expected)
    {
        ItemVersion.Matches(header, current).ShouldBe(expected);
    }
}
