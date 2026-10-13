using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Domain.Catalog;
using Microsoft.AspNetCore.Mvc;

namespace CollectionMaxxing.Api.Catalog;

/// <summary>Query of <c>GET /api/v1/catalog/search</c>.</summary>
public sealed record CatalogSearchQuery(
    [FromQuery(Name = "q")] string Q,
    [FromQuery(Name = "kind")] WorkKind? Kind = null,
    [FromQuery(Name = "platform")] string? Platform = null,
    [FromQuery(Name = "year")] int? Year = null,
    [FromQuery(Name = "limit")] int? Limit = null);

/// <summary>Query of <c>GET /api/v1/catalog/lookup</c>: a single barcode or catalogue number.</summary>
public sealed record CatalogLookupQuery([FromQuery(Name = "code")] string Code);

/// <summary>Query of <c>GET …/feature-prints</c>: only prints created after <c>since</c>, to sync a phone.</summary>
public sealed record FeaturePrintsQuery([FromQuery(Name = "since")] DateTimeOffset? Since = null);

/// <summary>Body of <c>PUT …/feature-prints</c>: the phone's prints to back up.</summary>
public sealed record PutFeaturePrintsRequest(IReadOnlyList<FeaturePrintDto>? Prints = null);
