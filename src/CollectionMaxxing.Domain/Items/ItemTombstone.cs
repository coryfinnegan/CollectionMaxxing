using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Domain.Items;

/// <summary>
/// The record that an item was removed for good (not soft-deleted), kept for 30 days so the offline ownership snapshot can tell a phone
/// that already holds the item that it is gone. Soft-deleted items need none: their own row is the tombstone until it is purged.
/// </summary>
public sealed class ItemTombstone : ILibraryScoped
{
    public Guid ItemId { get; init; }

    public Guid LibraryId { get; init; }

    /// <summary>When the item was removed; the snapshot orders and pages by it like an item's <c>updated_at</c>.</summary>
    public DateTimeOffset DeletedAt { get; init; }
}
