namespace CollectionMaxxing.Domain.Items;

/// <summary>
/// The one rule for what an item's status means, used everywhere (the library list, the counts, "do I own this?", the scanner).
/// <b>Have it</b>: owned, lent and missing are your collection. They count, and they answer "you own this". Missing is
/// still yours, you just can't find it. <b>Want it</b>: wishlist is never in the collection and never counts as owned.
/// <b>Gone</b>: sold (sold, traded or given away) is never in the collection either; it is kept so "didn't I have this?" has an answer.
/// </summary>
public static class ItemStatuses
{
    /// <summary>The statuses that make up the collection. An array, so a query can ask <c>InCollection.Contains(status)</c>.</summary>
    public static readonly ItemStatus[] InCollection = [ItemStatus.Owned, ItemStatus.Lent, ItemStatus.Missing];

    public static bool IsInCollection(ItemStatus status) => status is ItemStatus.Owned or ItemStatus.Lent or ItemStatus.Missing;
}
