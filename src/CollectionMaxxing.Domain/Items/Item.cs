using System.Text.Json.Serialization;
using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Domain.Items;

public enum ItemStatus
{
    Owned,
    Wishlist,
    Lent,
    Sold,
    Missing,
}

public enum ItemCondition
{
    Sealed,
    Mint,
    VeryGood,
    Good,
    Fair,
    Poor,
}

public enum ItemCompleteness
{
    Complete,
    DiscOnly,
    CartOnly,
    BoxOnly,
    NoManual,

    [JsonStringEnumMemberName("n_a")]
    NotApplicable,
}

public enum CoverChoice
{
    Release,
    MyPhoto,
}

public enum AddedVia
{
    Barcode,
    Picture,
    Search,
    Import,
    Manual,
}

/// <summary>Your copy of a release: its condition, where it lives, whether it is lent out. Deleting it is soft for 30 days.</summary>
public sealed class Item : ILibraryScoped, IConcurrencyVersioned
{
    public const int MaxNotesLength = 4000;

    public Guid Id { get; init; }

    public Guid LibraryId { get; init; }

    public Guid ReleaseId { get; set; }

    public ItemStatus Status { get; set; }

    public ItemCondition? Condition { get; set; }

    public ItemCompleteness? Completeness { get; set; }

    public Guid? LocationId { get; set; }

    public string? Notes { get; set; }

    public DateOnly? AcquiredOn { get; set; }

    public string? LentTo { get; set; }

    public DateOnly? LentOn { get; set; }

    public string? CustomTitle { get; set; }

    /// <summary>Your own photo of this copy, used as its cover when <see cref="CoverChoice"/> is <c>MyPhoto</c>.</summary>
    public Guid? CustomCoverAssetId { get; set; }

    /// <summary>Loose cartridges default to <c>MyPhoto</c>.</summary>
    public CoverChoice CoverChoice { get; set; }

    /// <summary>
    /// Whether this copy's edition is proven: a barcode, a confirmed match, or the person's own entry. A T1 auto-add
    /// leaves it false until the edition is verified in place (docs/16 §6, §11); everything else arrives true.
    /// </summary>
    public bool EditionVerified { get; set; } = true;

    public AddedVia AddedVia { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public Guid CreatedBy { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Set when deleted; the row is purged after 30 days.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    public uint Version { get; private set; }
}
