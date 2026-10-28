using CollectionMaxxing.Application.Catalog;
using Microsoft.AspNetCore.Mvc;

namespace CollectionMaxxing.Api.Catalog;

/// <summary>
/// Query of <c>GET /api/v1/catalog/search</c>. <c>kind</c> and <c>scope</c> are text (minimal APIs bind enums case-sensitively;
/// the API spells them lower snake_case); the handler validates them. Results rank exact title-or-creator matches first,
/// then what the caller's libraries own or made for themselves, then score. <c>scope=library</c> keeps owned
/// absolute-first (the scanner's typed search), with exact matches first inside each half.
/// </summary>
public sealed record CatalogSearchQuery(
    [FromQuery(Name = "q")] string Q,
    [FromQuery(Name = "kind")] string? Kind = null,
    [FromQuery(Name = "platform")] string? Platform = null,
    [FromQuery(Name = "year")] int? Year = null,
    [FromQuery(Name = "limit")] int? Limit = null,
    [FromQuery(Name = "scope")] string? Scope = null);

/// <summary>Query of <c>GET /api/v1/catalog/lookup</c>: a single barcode or catalogue number.</summary>
public sealed record CatalogLookupQuery([FromQuery(Name = "code")] string Code);

/// <summary>
/// Query of <c>GET …/feature-prints</c>: only prints written after <c>since</c>, to sync a phone. Pages of up to <c>limit</c> (200 at most);
/// pass the <c>nextCursor</c> of a page as <c>cursor</c> to get the next one.
/// </summary>
public sealed record FeaturePrintsQuery(
    [FromQuery(Name = "since")] DateTimeOffset? Since = null,
    [FromQuery(Name = "limit")] int? Limit = null,
    [FromQuery(Name = "cursor")] string? Cursor = null);

/// <summary>Body of <c>PUT …/feature-prints</c>: the phone's prints to back up.</summary>
public sealed record PutFeaturePrintsRequest(IReadOnlyList<FeaturePrintDto>? Prints = null);
