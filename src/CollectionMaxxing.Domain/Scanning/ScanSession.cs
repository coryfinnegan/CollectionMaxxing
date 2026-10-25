using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Items;

namespace CollectionMaxxing.Domain.Scanning;

public enum ScanSessionStatus
{
    Open,
    Committed,
    Abandoned,
}

/// <summary>What the person is scanning in this session: one kind of item, or a bit of everything (docs/16 §4).</summary>
public enum ScanMediaHint
{
    Books,
    Games,
    Movies,
    Mixed,
}

/// <summary>Where the scanning happens: at home (items go straight to the session's location) or in a store (locations are decided later).</summary>
public enum ScanChannel
{
    Home,
    InStore,
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

    /// <summary>What the person is scanning: one kind, or <see cref="ScanMediaHint.Mixed"/> when they didn't say.</summary>
    public ScanMediaHint MediaHint { get; set; } = ScanMediaHint.Mixed;

    /// <summary>Where the scanning happens; <see cref="ScanChannel.Home"/> when they didn't say.</summary>
    public ScanChannel Channel { get; set; } = ScanChannel.Home;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? CommittedAt { get; set; }

    /// <summary>
    /// The ids of the items the commit created, set when the session is committed and cleared when the commit is undone (SCAN-1e). Undo removes
    /// exactly these, never items found by when or by whom they were made. Null before the first commit, and for a commit made before this was kept.
    /// </summary>
    public Guid[]? CommittedItemIds { get; set; }

    public uint Version { get; private set; }
}
