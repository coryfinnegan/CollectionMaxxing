using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Domain.Scanning;

/// <summary>What the library knows about a provisional record: nothing yet. It stays unverified until the person acts on it.</summary>
public enum ProvisionalStatus
{
    Unverified,
}

/// <summary>What the person did with a provisional record.</summary>
public enum ProvisionalResolveAction
{
    /// <summary>Kept for real: an item was made from the guessed title (the crop optionally became its cover).</summary>
    Promoted,

    /// <summary>Linked to an item the library already has.</summary>
    ResolvedToExisting,
}

/// <summary>
/// An unreadable spine the person kept (design §11): the OCR guess as its title (always a guess, never asserted), the
/// session's location, and the segment crop as its visual anchor. Not part of the 7-day session cleanup: it persists until
/// the person promotes, resolves or deletes it. A resolved row stays as the audit trail (what eval denominators reconcile
/// against); only an explicit delete removes the row, with its crop.
/// </summary>
public sealed class ProvisionalRecord : ILibraryScoped, IConcurrencyVersioned
{
    public Guid Id { get; init; }

    public Guid LibraryId { get; init; }

    /// <summary>The session it was kept from; null once that session is cleaned up (the record outlives it).</summary>
    public Guid? SessionId { get; set; }

    /// <summary>The snap whose segment it is; null once that session's snaps are cleaned up.</summary>
    public Guid? SnapId { get; set; }

    /// <summary>The uploaded segment crop (<c>AssetKind.ScanPhoto</c>); null when the phone had no crop to send, or the crop was dropped on resolve.</summary>
    public Guid? CropAssetId { get; set; }

    /// <summary>What the phone read. Always a guess: responses carry <c>isGuess: true</c> unconditionally.</summary>
    public string TitleGuess { get; init; } = string.Empty;

    public ScanMediaHint MediaHint { get; init; }

    /// <summary>Where the item would go: the session's default location when it was kept.</summary>
    public Guid? LocationId { get; init; }

    public ProvisionalStatus Status { get; init; } = ProvisionalStatus.Unverified;

    /// <summary>The T2 row it was kept from; null once that candidate is gone. The candidate carries the same link back.</summary>
    public Guid? CandidateId { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the person promoted or resolved it; null while it still needs attention.</summary>
    public DateTimeOffset? ResolvedAt { get; set; }

    public ProvisionalResolveAction? ResolveAction { get; set; }

    /// <summary>The item promoting made, or the item it was resolved to; null on delete (the row is gone then).</summary>
    public Guid? CreatedItemId { get; set; }

    public uint Version { get; private set; }
}
