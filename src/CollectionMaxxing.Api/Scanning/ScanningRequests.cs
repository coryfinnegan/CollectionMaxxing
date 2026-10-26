using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.AspNetCore.Mvc;

namespace CollectionMaxxing.Api.Scanning;

/// <summary>
/// Body of <c>POST …/scan-sessions</c>. There are no modes: defaults only save typing at Review. <c>MediaHint</c> says what
/// is being scanned (mixed when missing) and <c>Channel</c> says where (home when missing; in a store, Review asks for each location later).
/// <c>AutoCommitTiers</c> says which tiers auto-add (T0 when missing; empty turns auto-add off); <c>AutoCommitCopies</c> whether
/// an exact-owned hit still adds another copy.
/// </summary>
public sealed record CreateScanSessionRequest(
    Guid? DefaultLocationId = null,
    ItemStatus? DefaultStatus = null,
    ScanMediaHint? MediaHint = null,
    ScanChannel? Channel = null,
    IReadOnlyList<ScanTier>? AutoCommitTiers = null,
    bool AutoCommitCopies = false);

/// <summary>Query of <c>GET /api/v1/scan-sessions/{id}</c>: with <c>since</c>, only candidates after that sequence number.</summary>
public sealed record ScanSessionQuery([FromQuery(Name = "since")] int? Since = null);

public sealed record ScannedCode(string? Value = null, string? Symbology = null, int? Copies = null);

/// <summary>
/// Body of <c>POST …/barcodes</c>: up to 50 codes per call. Needs an <c>Idempotency-Key</c> header.
/// With <c>CandidateId</c> (Review's "Scan barcode", design §8(c)), the batch carries exactly one code and
/// upgrades that row in place instead of adding candidates.
/// </summary>
public sealed record BarcodeBatchRequest(IReadOnlyList<ScannedCode>? Codes = null, Guid? CandidateId = null);

/// <summary>
/// One item the phone read in a photo. At most 16 KB. No image is ever sent. <c>Format</c> and <c>Year</c> are deliberate
/// additions to the original contract (SCAN-2a): without the format a VHS and a DVD of one film are indistinguishable.
/// <c>SegmentId</c> is the phone's own id for the segment the item belongs to, so the items of a multi-item snap stay linked (#338).
/// Every field is a claim to check against the catalogue, never a verdict: the server decides the match.
/// </summary>
public sealed record IdentifyItem(
    string? LocalId = null,
    string? SegmentId = null,
    string? Text = null,
    string? Title = null,
    string? Creator = null,
    string? Edition = null,
    ItemCompleteness? Completeness = null,
    CollectionMaxxing.Domain.Catalog.WorkKind? Kind = null,
    CollectionMaxxing.Domain.Catalog.ReleaseFormat? Format = null,
    int? Year = null,
    string? Platform = null,
    Guid? LocalMatchReleaseId = null);

/// <summary>
/// Body of <c>POST …/identify</c>: what the phone read from one photo (at most 40 items, 256 KB). <c>FellBackFrom</c> says why
/// the preferred engine wasn't used (a deliberate addition, SCAN-2a), or carries the classifier's reshoot signal (#344);
/// <c>ShotKind</c> says what the phone's framing said the shot was (#338); <c>Kind</c> is a shelf read unless the phone says
/// cover; <c>CandidateId</c> ties a cover read to the T2 row it was shot for, upgrading it in place (#344). Idempotent per
/// <c>SnapId</c>.
/// </summary>
public sealed record IdentifyRequest(
    Guid? SnapId = null,
    SnapTrigger? Trigger = null,
    ShotKind? ShotKind = null,
    ScanEngine? Engine = null,
    FallbackReason? FellBackFrom = null,
    IReadOnlyList<IdentifyItem>? Items = null,
    IdentifyKind? Kind = null,
    Guid? CandidateId = null);

/// <summary>A feature-print distance the phone computed between the item and a release's artwork.</summary>
public sealed record ReleaseDistance(Guid? ReleaseId = null, double? Distance = null);

/// <summary>Body of <c>POST …/candidates/{cid}:art</c>: the server re-scores the candidate with these distances.</summary>
public sealed record ArtDistancesRequest(IReadOnlyList<ReleaseDistance>? Distances = null);

/// <summary>
/// Body of <c>PATCH …/candidates/{cid}</c>: accept, reject, or choose an alternative release; change how many copies; or
/// correct the format, platform or edition (SCAN-1d). A blank <c>Edition</c> clears the correction.
/// </summary>
public sealed record DecideCandidateRequest(
    CandidateDecision? Decision = null,
    Guid? ReleaseId = null,
    int? Copies = null,
    CollectionMaxxing.Domain.Catalog.ReleaseFormat? Format = null,
    string? Platform = null,
    string? Edition = null);

/// <summary>What Review changes for one scanned item at commit; a field left out means "use the default".</summary>
public sealed record CommitOverrideRequest(
    Guid? CandidateId = null, Guid? LocationId = null, ItemStatus? Status = null, ItemCondition? Condition = null, int? Copies = null);

/// <summary>
/// Optional body of <c>POST …/commit</c> (SCAN-1d): <c>AddTo</c> is the location for every item, <c>Status</c> and
/// <c>Condition</c> apply to every item, and <c>Overrides</c> change single scanned items. Applied in the commit's own
/// transaction; with no body the commit is exactly what it was before.
/// </summary>
public sealed record CommitScanRequest(
    Guid? AddTo = null, ItemStatus? Status = null, ItemCondition? Condition = null, IReadOnlyList<CommitOverrideRequest>? Overrides = null);

/// <summary>
/// Body of <c>POST …/candidates/{cid}:search</c>: re-search with the user's own words. <c>ContextWorkIds</c> carries the
/// segment neighbours' works (#344): a ranking bias toward them, never a filter.
/// </summary>
public sealed record SearchCandidateRequest(string? Text = null, IReadOnlyList<Guid>? ContextWorkIds = null);

/// <summary>
/// Optional body of <c>POST …/auto-commit:undo</c>: which auto-add to take back. Missing undoes the most recent
/// standing auto-add of the session.
/// </summary>
public sealed record UndoAutoCommitRequest(Guid? CandidateId = null);
