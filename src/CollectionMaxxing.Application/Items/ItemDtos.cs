using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Items;

namespace CollectionMaxxing.Application.Items;

// Response shapes of the items endpoints (docs/04). Fields are the contract: change them deliberately.

/// <summary>An item as it appears in a list: enough to draw a row or a card. <c>LentTo</c> is who has it, for a lent item, else null.</summary>
public sealed record ItemSummaryDto(
    Guid Id,
    Guid ReleaseId,
    Guid WorkId,
    string Title,
    WorkKind Kind,
    ReleaseFormat Format,
    string? Platform,
    int? Year,
    ItemStatus Status,
    ItemCondition? Condition,
    Guid? LocationId,
    string? CoverUrl,
    DateTimeOffset CreatedAt,
    bool EditionVerified,
    string? LentTo = null);

/// <summary>
/// Your copy of a release. <c>CreatedBy</c> is who added it and <c>CreatedByName</c> is that person's display name (the "Added by" line);
/// the name stays even after the person was removed from the library. <c>EditionVerified</c> is false only for a T1
/// auto-add whose edition is still unproven (docs/16 §6).
/// </summary>
public sealed record ItemDto(
    Guid Id,
    Guid ReleaseId,
    ItemStatus Status,
    ItemCondition? Condition,
    ItemCompleteness? Completeness,
    Guid? LocationId,
    string? Notes,
    DateOnly? AcquiredOn,
    string? LentTo,
    DateOnly? LentOn,
    string? CustomTitle,
    Guid? CustomCoverAssetId,
    CoverChoice CoverChoice,
    AddedVia AddedVia,
    IReadOnlyList<Guid> TagIds,
    DateTimeOffset CreatedAt,
    Guid CreatedBy,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DeletedAt,
    bool EditionVerified,
    string? CreatedByName = null);

/// <summary>Another copy of the same work that the library owns (maybe on a different format).</summary>
public sealed record OtherCopyDto(Guid ItemId, Guid ReleaseId, ReleaseFormat Format, string? Platform, ItemStatus Status, Guid? LocationId);

/// <summary>An item with its work and release, and the other copies of that work.</summary>
public sealed record ItemDetailDto(ItemDto Item, WorkDto Work, ReleaseDto Release, IReadOnlyList<OtherCopyDto> OtherCopies);

/// <summary>What a bulk change did.</summary>
public sealed record BulkItemsResultDto(int Affected);

/// <summary>
/// Counts for the library's stats screen. <c>Total</c> and every breakdown count the collection only (owned, lent and
/// missing: what you have). The four counts after them say how many items sit beside it: on the wishlist, sold, lent out
/// (already inside <c>Total</c>) and missing (also inside it).
/// </summary>
public sealed record StatsDto(
    int Total,
    IReadOnlyDictionary<string, int> ByKind,
    IReadOnlyDictionary<string, int> ByFormat,
    IReadOnlyDictionary<string, int> ByPlatform,
    IReadOnlyDictionary<string, int> ByDecade,
    int WishlistCount,
    int SoldCount,
    int LentCount,
    int MissingCount);
