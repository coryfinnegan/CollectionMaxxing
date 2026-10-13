using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.AspNetCore.Mvc;

namespace CollectionMaxxing.Api.Scanning;

/// <summary>Body of <c>POST …/scan-sessions</c>. There are no modes: defaults only save typing at Review.</summary>
public sealed record CreateScanSessionRequest(Guid? DefaultLocationId = null, ItemStatus? DefaultStatus = null);

/// <summary>Query of <c>GET /api/v1/scan-sessions/{id}</c>: with <c>since</c>, only candidates after that sequence number.</summary>
public sealed record ScanSessionQuery([FromQuery(Name = "since")] int? Since = null);

public sealed record ScannedCode(string? Value = null, string? Symbology = null, int? Copies = null);

/// <summary>Body of <c>POST …/barcodes</c>: up to 50 codes per call. Needs an <c>Idempotency-Key</c> header.</summary>
public sealed record BarcodeBatchRequest(IReadOnlyList<ScannedCode>? Codes = null);

/// <summary>One item the phone read in a photo. At most 16 KB. No image is ever sent.</summary>
public sealed record IdentifyItem(
    string? LocalId = null,
    string? Text = null,
    string? Title = null,
    string? Creator = null,
    string? Edition = null,
    ItemCompleteness? Completeness = null,
    CollectionMaxxing.Domain.Catalog.WorkKind? Kind = null,
    string? Platform = null,
    Guid? LocalMatchReleaseId = null);

/// <summary>Body of <c>POST …/identify</c>: what the phone read from one photo.</summary>
public sealed record IdentifyRequest(
    Guid? SnapId = null,
    SnapTrigger? Trigger = null,
    ScanEngine? Engine = null,
    IReadOnlyList<IdentifyItem>? Items = null);

/// <summary>A feature-print distance the phone computed between the item and a release's artwork.</summary>
public sealed record ReleaseDistance(Guid? ReleaseId = null, double? Distance = null);

/// <summary>Body of <c>POST …/candidates/{cid}:art</c>: the server re-scores the candidate with these distances.</summary>
public sealed record ArtDistancesRequest(IReadOnlyList<ReleaseDistance>? Distances = null);

/// <summary>Body of <c>PATCH …/candidates/{cid}</c>: accept, reject, or choose an alternative release.</summary>
public sealed record DecideCandidateRequest(CandidateDecision? Decision = null, Guid? ReleaseId = null, int? Copies = null);

/// <summary>Body of <c>POST …/candidates/{cid}:search</c>: re-search with the user's own words.</summary>
public sealed record SearchCandidateRequest(string? Text = null);
