using System.Text.Json;
using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Scanning;

namespace CollectionMaxxing.Application.Scanning;

// Response shapes of the scanning endpoints (docs/04). Fields are the contract: change them deliberately.

/// <summary>Where an item sits in the photo, each value 0–1.</summary>
public sealed record BoxDto(double X, double Y, double W, double H);

/// <summary>
/// One thing the scanner saw, what we think it is, and what the user decided. <c>NeedsArt</c> says the top releases are a close
/// call: the phone should compare the alternatives' <c>artworkUrl</c>s with the item and post the distances to
/// <c>POST …/candidates/{id}:art</c>.
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
    JsonElement? Alternatives,
    CandidateDecision Decision,
    int Copies,
    int AlreadyOwnedCount,
    IReadOnlyList<string> OwnedAt,
    Guid? CreatedItemId);

/// <summary>A scan session with all its candidates (or those after <c>?since=seq</c>).</summary>
public sealed record ScanSessionDto(
    Guid Id,
    Guid LibraryId,
    ScanSessionStatus Status,
    Guid? DefaultLocationId,
    ItemStatus? DefaultStatus,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CommittedAt,
    IReadOnlyList<CandidateDto> Candidates);

public sealed record CreatedScanSessionDto(Guid Id);

/// <summary>Candidates created or updated by one request. Slow ones keep resolving and arrive over SSE.</summary>
public sealed record CandidatesDto(IReadOnlyList<CandidateDto> Candidates);

/// <summary>What committing a session did.</summary>
public sealed record CommitResultDto(int Created, int Skipped, IReadOnlyList<Guid> ItemIds);
