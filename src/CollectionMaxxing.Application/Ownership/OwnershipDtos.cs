using CollectionMaxxing.Domain.Items;

namespace CollectionMaxxing.Application.Ownership;

/// <summary>One copy the library already has, and where it lives.</summary>
public sealed record OwnedCopyDto(Guid ItemId, Guid ReleaseId, ItemStatus Status, Guid? LocationId, string? LocationPath);

/// <summary>The answer to "do I own this?" for a barcode, a release or a work.</summary>
public sealed record OwnershipDto(bool Owned, int Count, IReadOnlyList<OwnedCopyDto> Copies);
