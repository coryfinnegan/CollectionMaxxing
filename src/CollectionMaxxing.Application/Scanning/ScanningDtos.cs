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
    GuidanceCode? GuidanceCode);

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

/// <summary>A scan session with all its candidates (or those after <c>?since=seq</c>).</summary>
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
    IReadOnlyList<CandidateDto> Candidates);

public sealed record CreatedScanSessionDto(Guid Id);

/// <summary>Candidates created or updated by one request. Slow ones keep resolving and arrive over SSE.</summary>
public sealed record CandidatesDto(IReadOnlyList<CandidateDto> Candidates);

/// <summary>What committing a session did.</summary>
public sealed record CommitResultDto(int Created, int Skipped, IReadOnlyList<Guid> ItemIds);
