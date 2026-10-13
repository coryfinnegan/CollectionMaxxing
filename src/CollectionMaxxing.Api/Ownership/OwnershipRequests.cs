using Microsoft.AspNetCore.Mvc;

namespace CollectionMaxxing.Api.Ownership;

/// <summary>Query of <c>GET …/ownership</c>: give one of <c>code</c> (a barcode), <c>releaseId</c> or <c>workId</c>.</summary>
public sealed record OwnershipQuery(
    [FromQuery(Name = "code")] string? Code = null,
    [FromQuery(Name = "releaseId")] Guid? ReleaseId = null,
    [FromQuery(Name = "workId")] Guid? WorkId = null);
