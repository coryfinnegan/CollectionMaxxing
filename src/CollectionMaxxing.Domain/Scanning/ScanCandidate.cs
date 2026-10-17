using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Domain.Scanning;

public enum CandidateSource
{
    Barcode,
    PictureAppleModel,
    PictureText,
    Search,
}

public enum BoxSource
{
    Ocr,
    Rect,
}

public enum CandidateResolution
{
    Pending,
    Matched,
    Ambiguous,
    NotFound,
    Error,
}

public enum CandidateDecision
{
    Undecided,
    Accepted,
    Rejected,
}

/// <summary>One thing the scanner saw, what we think it is, and what the user decided about it.</summary>
public sealed class ScanCandidate : ILibraryScoped, IConcurrencyVersioned
{
    public Guid Id { get; init; }

    public Guid LibraryId { get; init; }

    public Guid SessionId { get; init; }

    /// <summary>Position in the session; clients resume from here (<c>?since=seq</c>, <c>Last-Event-ID</c>).</summary>
    public int Seq { get; init; }

    public CandidateSource Source { get; init; }

    public string? RawCode { get; init; }

    public string? Symbology { get; init; }

    /// <summary>What the phone read from the item.</summary>
    public string? VisibleText { get; init; }

    /// <summary>The snap the candidate cites. "Use my photo" points it at a snap of its own that holds the uploaded crop.</summary>
    public Guid? SnapId { get; set; }

    /// <summary>The phone's own id for the item, so it can draw the box.</summary>
    public string? LocalId { get; init; }

    /// <summary>The item's box in the photo, each value 0–1.</summary>
    public double? BoxX { get; init; }

    public double? BoxY { get; init; }

    public double? BoxW { get; init; }

    public double? BoxH { get; init; }

    public BoxSource? BoxSource { get; init; }

    public CandidateResolution Resolution { get; set; }

    public Guid? MatchedReleaseId { get; set; }

    public double? Score { get; set; }

    /// <summary>Feature-print distance the phone reported (close calls only).</summary>
    public double? ArtDistance { get; set; }

    /// <summary>JSON: completeness, region, edition marks, box/manual visible.</summary>
    public string? InferredJson { get; set; }

    public string? AlternativesJson { get; set; }

    public string? TraceJson { get; set; }

    public CandidateDecision Decision { get; set; }

    public int Copies { get; set; } = 1;

    public int AlreadyOwnedCount { get; set; }

    public Guid? CreatedItemId { get; set; }

    /// <summary>The format the person chose over what was matched (SCAN-1d); commit uses the matching release of the same work.</summary>
    public ReleaseFormat? EditedFormat { get; set; }

    /// <summary>The platform the person typed over what was matched (at most 100 characters).</summary>
    public string? EditedPlatform { get; set; }

    /// <summary>The edition the person typed over what was matched (at most 100 characters).</summary>
    public string? EditedEdition { get; set; }

    /// <summary>True once the person changed the format, platform or edition: the scanner's guess is no longer the whole story.</summary>
    public bool UserEdited { get; set; }

    /// <summary>
    /// Per-session counter, set by the database on every insert and update of this row (a trigger takes the next value of the
    /// session's counter inside the writing transaction), so it only goes up in commit order. Event ids and <c>?since=</c> use it:
    /// a client that reconnects with the highest value it saw gets every candidate that changed since, whether new or updated.
    /// </summary>
    public int ChangeSeq { get; private set; }

    public string? IdempotencyKey { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public uint Version { get; private set; }
}
