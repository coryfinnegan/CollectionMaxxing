using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Items;

namespace CollectionMaxxing.Domain.Scanning;

public enum ScanSessionStatus
{
    Open,
    Committed,
    Abandoned,
}

/// <summary>One scanning run. Abandoned after 7 days if nobody commits it.</summary>
public sealed class ScanSession : ILibraryScoped, IConcurrencyVersioned
{
    public Guid Id { get; init; }

    public Guid LibraryId { get; init; }

    public Guid UserId { get; init; }

    public ScanSessionStatus Status { get; set; }

    public Guid? DefaultLocationId { get; set; }

    public ItemStatus? DefaultStatus { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? CommittedAt { get; set; }

    /// <summary>
    /// The ids of the items the commit created, set when the session is committed and cleared when the commit is undone (SCAN-1e). Undo removes
    /// exactly these, never items found by when or by whom they were made. Null before the first commit, and for a commit made before this was kept.
    /// </summary>
    public Guid[]? CommittedItemIds { get; set; }

    public uint Version { get; private set; }
}
