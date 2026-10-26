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

    /// <summary>
    /// Which tiers auto-add in this session (docs/16 §6), spelled as <see cref="EnumText"/> spells them: T0 unless the
    /// session says otherwise. T1 needs an explicit opt-in until the eval gate passes on real data; an empty list turns
    /// auto-add off entirely. Use <see cref="AutoCommits"/> to ask.
    /// </summary>
    public string[] AutoCommitTiers { get; set; } = [EnumText.ToText(ScanTier.T0)];

    /// <summary>Whether <paramref name="tier"/> auto-adds in this session.</summary>
    public bool AutoCommits(ScanTier tier) => AutoCommitTiers.Contains(EnumText.ToText(tier));

    /// <summary>
    /// Whether an exact-owned release still auto-adds (as another copy). Off by default: re-scanning an owned shelf
    /// answers each hit on its card and adds nothing.
    /// </summary>
    public bool AutoCommitCopies { get; set; }

    /// <summary>
    /// The items a barcode auto-add currently owns in this session (docs/16 §6): appended when one fires, shortened when
    /// one is undone. Undoing an auto-add removes exactly the event's own ids (see <see cref="ScanCandidate.AutoCommittedItemIds"/>);
    /// the full-commit undo leaves these alone.
    /// </summary>
    public Guid[]? AutoCommittedItemIds { get; set; }

    public uint Version { get; private set; }
}
