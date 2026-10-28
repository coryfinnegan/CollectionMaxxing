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
    /// Which tiers auto-add in this session (docs/16 §6), spelled as <see cref="EnumText"/> spells them: none unless
    /// the session says otherwise (#452 — nothing enters the library straight from a scan; every capture waits for the
    /// Review commit). An empty list turns auto-add off entirely, and so does predating the #452 cutover (#489:
    /// pre-cutover rows kept the old T0 default, which silently kept auto-adding after the fix). Use
    /// <see cref="AutoCommits"/> to ask.
    /// </summary>
    public string[] AutoCommitTiers { get; set; } = [];

    /// <summary>
    /// The #452 cutover: #469 merged 2026-10-27T22:17:11Z, moving the session default to no auto-add. Sessions
    /// created before that instant follow the no-auto-add rule whatever their stored tiers say (#489).
    /// </summary>
    public static readonly DateTimeOffset AutoCommitCutover = new(2026, 10, 27, 22, 17, 11, TimeSpan.Zero);

    /// <summary>
    /// Whether <paramref name="tier"/> auto-adds in this session: named in <see cref="AutoCommitTiers"/> and created
    /// after the #452 cutover. Pre-cutover sessions never auto-add (#489).
    /// </summary>
    public bool AutoCommits(ScanTier tier) => CreatedAt >= AutoCommitCutover && AutoCommitTiers.Contains(EnumText.ToText(tier));

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
