using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Items;

namespace CollectionMaxxing.Application.Ownership;

/// <summary>One copy the library already has, and where it lives (<c>LocationPath</c> reads "Room › Shelf › Bin").</summary>
public sealed record OwnedCopyDto(
    Guid ItemId,
    Guid ReleaseId,
    Guid WorkId,
    string Title,
    ReleaseFormat Format,
    string? Platform,
    ItemStatus Status,
    Guid? LocationId,
    string? LocationPath);

/// <summary>
/// The answer to "do I own this?" for a barcode, a release or a work. <c>Copies</c> are exact matches that are in the collection
/// (owned, lent or missing: see <see cref="ItemStatuses"/>); <c>SameWorkOtherFormat</c> are copies of the same work in a different
/// format or on a different platform (a soft match). Beside them, two facts that are never "owned": <c>WishlistItemId</c> is the
/// item on this library's wishlist for the release asked about (the oldest, if there are several), and <c>SoldItemId</c> is the
/// library's most recently added copy of it that was sold, traded or given away. Both are null when there is none, and for a
/// question about a whole work.
/// </summary>
public sealed record OwnershipDto(
    bool Owned,
    int Count,
    IReadOnlyList<OwnedCopyDto> Copies,
    IReadOnlyList<OwnedCopyDto> SameWorkOtherFormat,
    Guid? WishlistItemId = null,
    Guid? SoldItemId = null);

/// <summary>
/// One item for the phone's offline cache, or (<c>Deleted</c>) a tombstone telling it to forget the item. A tombstone
/// carries only <c>ItemId</c>. <c>Status</c> says which side of the line the item is on (<see cref="ItemStatuses"/>): owned, lent
/// and missing are the collection; wishlist and sold are carried so the cache can answer "on your wishlist" and "you sold this"
/// offline, and are never counted as owned. <c>Identifiers</c> are normalised ISBN-13 / GTIN values.
/// </summary>
public sealed record OwnershipSnapshotEntryDto(
    Guid ItemId,
    bool Deleted,
    Guid? ReleaseId,
    Guid? WorkId,
    string? Title,
    ReleaseFormat? Format,
    string? Platform,
    IReadOnlyList<string> Identifiers,
    string? LocationPath,
    ItemStatus? Status = null);

/// <summary>A page of changes. Keep calling with <c>NextCursor</c> while <c>HasMore</c>; store the last one for next time.</summary>
public sealed record OwnershipSnapshotDto(IReadOnlyList<OwnershipSnapshotEntryDto> Entries, string NextCursor, bool HasMore);
