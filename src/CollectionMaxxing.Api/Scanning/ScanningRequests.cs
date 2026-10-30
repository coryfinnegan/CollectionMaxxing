using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.AspNetCore.Mvc;

namespace CollectionMaxxing.Api.Scanning;

/// <summary>
/// Body of <c>POST …/scan-sessions</c>. There are no modes: defaults only save typing at Review. <c>MediaHint</c> says what
/// is being scanned (mixed when missing) and <c>Channel</c> says where (home when missing; in a store, Review asks for each location later).
/// <c>AutoCommitTiers</c> says which tiers auto-add (none when missing, since #452; pre-cutover sessions never
/// auto-add whatever they carry, #489); <c>AutoCommitCopies</c> whether an exact-owned hit still adds another copy.
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
/// Where a read sits in its picture: <c>X</c>, <c>Y</c>, <c>Width</c> and <c>Height</c>, each a fraction of the picture from 0 to 1,
/// the origin at the top left (the phone's <c>Rect</c>). The width and height are above 0 and the box ends inside the picture.
/// </summary>
public sealed record ItemBox(double? X = null, double? Y = null, double? Width = null, double? Height = null);

/// <summary>
/// One item the phone read in a photo. At most 16 KB. No image is ever sent. <c>Format</c> and <c>Year</c> are deliberate
/// additions to the original contract (SCAN-2a): without the format a VHS and a DVD of one film are indistinguishable.
/// <c>SegmentId</c> is the phone's own id for the segment the item belongs to, so the items of a multi-item snap stay linked (#338).
/// Every field is a claim to check against the catalogue, never a verdict: the server decides the match.
/// <c>ProductCode</c> is the code printed on a game cartridge or disc (<c>SNS-ADNE-USA</c>, <c>SLUS-00594</c>), at most 32
/// characters: when the server's table says it belongs to one game, the read is identified as that game (ADR-031).
/// <c>Box</c> is where the read sits in the picture (optional): two reads of one release in one place are one item read twice,
/// in two places they are two items. Without a box (an older phone) two reads of one release are one item.
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
    Guid? LocalMatchReleaseId = null,
    string? ProductCode = null,
    ItemBox? Box = null);

/// <summary>
/// Body of <c>POST …/identify</c>: what the phone read from one photo (at most 40 items, 256 KB). <c>FellBackFrom</c> says why
/// the preferred engine wasn't used (a deliberate addition, SCAN-2a), or carries the classifier's reshoot signal (#344);
/// <c>ShotKind</c> says what the phone's framing said the shot was (#338); <c>Kind</c> is a shelf read unless the phone says
/// cover; <c>CandidateId</c> ties a cover read to the T2 row it was shot for, upgrading it in place (#344);
/// <c>ItemId</c> ties an edition-proof read to the library item it verifies, upgrading it in place (#346). Idempotent per
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
    Guid? CandidateId = null,
    Guid? ItemId = null);

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

/// <summary>
/// Body of <c>POST …/provisionals</c> (#345): the T2 row to keep as unverified, and the uploaded segment crop to claim
/// as its visual anchor (null when the phone had no crop to send).
/// </summary>
public sealed record CreateProvisionalRequest(Guid? CandidateId = null, Guid? CropAssetId = null);

/// <summary>Body of <c>POST …/provisionals/{pid}:promote</c> (#345): keep the crop as the new item's cover (the default).</summary>
public sealed record PromoteProvisionalRequest(bool? KeepCrop = null);

/// <summary>Body of <c>POST …/provisionals/{pid}:resolve</c> (#345): the library item this kept row is.</summary>
public sealed record ResolveProvisionalRequest(Guid? ItemId = null);
