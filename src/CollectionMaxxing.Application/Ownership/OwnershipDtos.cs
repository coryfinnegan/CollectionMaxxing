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
/// The answer to "do I own this?" for a barcode, a release or a work. <c>Copies</c> are exact matches;
/// <c>SameWorkOtherFormat</c> are copies of the same work in a different format or on a different platform (a soft match).
/// </summary>
public sealed record OwnershipDto(
    bool Owned,
    int Count,
    IReadOnlyList<OwnedCopyDto> Copies,
    IReadOnlyList<OwnedCopyDto> SameWorkOtherFormat);

/// <summary>
/// One owned copy for the phone's offline cache, or (<c>Deleted</c>) a tombstone telling it to forget the item. A tombstone
/// carries only <c>ItemId</c>. <c>Identifiers</c> are normalised ISBN-13 / GTIN values.
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
    string? LocationPath);

/// <summary>A page of changes. Keep calling with <c>NextCursor</c> while <c>HasMore</c>; store the last one for next time.</summary>
public sealed record OwnershipSnapshotDto(IReadOnlyList<OwnershipSnapshotEntryDto> Entries, string NextCursor, bool HasMore);
