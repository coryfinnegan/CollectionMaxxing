using Microsoft.AspNetCore.Mvc;

namespace CollectionMaxxing.Api.Ownership;

/// <summary>Query of <c>GET …/ownership</c>: give one of <c>code</c> (a barcode), <c>releaseId</c> or <c>workId</c>.</summary>
public sealed record OwnershipRequest(
    [FromQuery(Name = "code")] string? Code = null,
    [FromQuery(Name = "releaseId")] Guid? ReleaseId = null,
    [FromQuery(Name = "workId")] Guid? WorkId = null);

/// <summary>Query of <c>GET …/ownership/snapshot</c>: the <c>nextCursor</c> of the last call (none for everything), and a page size.</summary>
public sealed record OwnershipSnapshotRequest(
    [FromQuery(Name = "since")] string? Since = null,
    [FromQuery(Name = "limit")] int? Limit = null);
