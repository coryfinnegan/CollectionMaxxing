using CollectionMaxxing.Domain.Items;

namespace CollectionMaxxing.Domain.Tests.Items;

public class ItemStatusesTests
{
    [Theory]
    [InlineData(ItemStatus.Owned, true)]
    [InlineData(ItemStatus.Lent, true)]
    [InlineData(ItemStatus.Missing, true)]
    [InlineData(ItemStatus.Wishlist, false)]
    [InlineData(ItemStatus.Sold, false)]
    public void Only_what_you_have_is_in_the_collection(ItemStatus status, bool expected)
    {
        ItemStatuses.IsInCollection(status).ShouldBe(expected);
        ItemStatuses.InCollection.Contains(status).ShouldBe(expected);
    }

    [Fact]
    public void Every_status_is_either_in_the_collection_or_one_of_the_two_that_live_beside_it()
    {
        var beside = new[] { ItemStatus.Wishlist, ItemStatus.Sold };

        Enum.GetValues<ItemStatus>()
            .Where(status => !ItemStatuses.IsInCollection(status))
            .ShouldBe(beside, ignoreOrder: true);
    }
}
