using System.Text.Json;
using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Scanning;

namespace CollectionMaxxing.Application.Scanning;

// Response shapes of the scanning endpoints (docs/04). Fields are the contract: change them deliberately.

/// <summary>Where an item sits in the photo, each value 0–1.</summary>
public sealed record BoxDto(double X, double Y, double W, double H);

/// <summary>
/// One thing the scanner saw, what we think it is, and what the user decided. <c>NeedsArt</c> says the top releases are a close
/// call: the phone should compare the alternatives' <c>artworkUrl</c>s with the item and post the distances to
/// <c>POST …/candidates/{id}:art</c>. <c>ShotKind</c> is what the phone's framing said the shot was; <c>SegmentId</c> links the
/// items of a multi-item snap (#338); <c>GuidanceCode</c> is what this unresolved row tells the person to do next (#344).
/// <c>Tier</c> is the commit tier (docs/16 §6): null until the candidate resolves.
/// <c>CropUrl</c> is its kept segment crop, when it has one (#345).
/// <c>CoverPick</c> is the cover verification's preselected alternative (#438 stage 2): Review shows it checked, and the
/// person can still pick another. Null unless a verification preselected one.
/// </summary>
public sealed record CandidateDto(
    Guid Id,
    int Seq,
    CandidateSource Source,
    string? RawCode,
    string? Symbology,
    string? VisibleText,
    string? LocalId,
    BoxDto? Box,
    BoxSource? BoxSource,
    CandidateResolution Resolution,
    ScanTier? Tier,
    double? Score,
    double? ArtDistance,
    bool NeedsArt,
    ReleaseDto? Release,
    JsonElement? Inferred,
    IReadOnlyList<CandidateAlternativeDto>? Alternatives,
    CandidateDecision Decision,
    int Copies,
    int AlreadyOwnedCount,
    IReadOnlyList<string> OwnedAt,
    Guid? CreatedItemId,
    int ChangeSeq,
    CandidateEditsDto? Edits,
    bool UserEdited,
    PendingReason? PendingReason,
    DateTimeOffset? RetryAfter,
    ShotKind ShotKind,
    string? SegmentId,
    GuidanceCode? GuidanceCode,
    string? CropUrl,
    CoverPickDto? CoverPick = null);

/// <summary>
/// The cover verification's preselected alternative (#438 stage 2): the release Review shows checked, and why.
/// <c>Reason</c> is a stable code the app maps to its own copy (<c>cover_match</c>: the cover matched the capture).
/// </summary>
public sealed record CoverPickDto(Guid ReleaseId, string Reason);

/// <summary>Why a candidate is still Pending: <c>Quota</c> (today's UPC allowance is spent, so it waits for the nightly drain) or <c>Lookup</c> (a provider was slow or down, so it is tried again shortly).</summary>
public enum PendingReason
{
    Quota,
    Lookup,
}

/// <summary>What the person corrected on a candidate; a field is null when they left it alone.</summary>
public sealed record CandidateEditsDto(ReleaseFormat? Format, string? Platform, string? Edition);

/// <summary>
/// One release a candidate could be instead (its <c>alternatives</c>, best first): what the person sees when picking, with their own ownership.
/// <c>ArtworkUrl</c> is set only on a close call, for the phone's artwork comparison. The fields are exactly those that were always sent.
/// </summary>
public sealed record CandidateAlternativeDto(
    Guid ReleaseId,
    string Title,
    string Format,
    string? Platform,
    int? Year,
    double Score,
    IReadOnlyList<string> Creators,
    string? CoverUrl,
    int AlreadyOwnedCount,
    string? ArtworkUrl);

/// <summary>
/// A scan session with all its candidates (or those after <c>?since=seq</c>). <c>Counters</c> and <c>Segments</c> always cover
/// the whole session, never just the <c>?since=</c> window, so the phone's bar and strip stay complete while polling.
/// <c>AutoCommitTiers</c> is which tiers auto-add (docs/16 §6); <c>AutoCommitCopies</c> whether an exact-owned hit still adds another copy.
/// <c>Provisionals</c> are the session's records that still need attention (#345).
/// </summary>
public sealed record ScanSessionDto(
    Guid Id,
    Guid LibraryId,
    ScanSessionStatus Status,
    Guid? DefaultLocationId,
    ItemStatus? DefaultStatus,
    ScanMediaHint MediaHint,
    ScanChannel Channel,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CommittedAt,
    IReadOnlyList<CandidateDto> Candidates,
    SessionCountersDto Counters,
    IReadOnlyList<SessionSegmentDto> Segments,
    IReadOnlyList<ScanTier> AutoCommitTiers,
    bool AutoCommitCopies,
    IReadOnlyList<ProvisionalDto> Provisionals);

public sealed record CreatedScanSessionDto(Guid Id);

/// <summary>Candidates created or updated by one request. Slow ones keep resolving and arrive over SSE.</summary>
public sealed record CandidatesDto(IReadOnlyList<CandidateDto> Candidates);

/// <summary>What committing a session did.</summary>
public sealed record CommitResultDto(int Created, int Skipped, IReadOnlyList<Guid> ItemIds);

/// <summary>What undoing one auto-add did: how many items it removed, and the candidate with its evidence back.</summary>
public sealed record UndoAutoCommitResultDto(int Removed, CandidateDto Candidate);

/// <summary>
/// An unreadable spine the person kept (design §11). <c>IsGuess</c> is always true: the title is what the phone read,
/// never an assertion. <c>CropUrl</c> is the kept segment crop, when the phone had one to send.
/// </summary>
public sealed record ProvisionalDto(
    Guid Id,
    Guid LibraryId,
    Guid? SessionId,
    Guid? SnapId,
    string TitleGuess,
    bool IsGuess,
    ScanMediaHint MediaHint,
    Guid? LocationId,
    ProvisionalStatus Status,
    Guid? CandidateId,
    string? CropUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt,
    ProvisionalResolveAction? ResolveAction,
    Guid? CreatedItemId);

/// <summary>The library's "Needs attention" list: its provisional records that still need acting on, stalest first.</summary>
public sealed record ProvisionalsDto(IReadOnlyList<ProvisionalDto> Items);

/// <summary>What keeping a row did: the provisional record, and the retired row (rejected, linked to it).</summary>
public sealed record CreateProvisionalResultDto(ProvisionalDto Provisional, CandidateDto Candidate);

/// <summary>What a segment-crop upload stored: the asset, and where to see it.</summary>
public sealed record CropUploadDto(Guid AssetId, string CropUrl);
