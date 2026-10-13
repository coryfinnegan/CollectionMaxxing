using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Items;
using Microsoft.AspNetCore.Mvc;

namespace CollectionMaxxing.Api.Items;

/// <summary>Query of <c>GET …/items</c>. <c>format</c>, <c>platform</c> and <c>sort</c> are text; a handler validates them.</summary>
public sealed record ListItemsQuery(
    [FromQuery(Name = "q")] string? Q = null,
    [FromQuery(Name = "kind")] WorkKind? Kind = null,
    [FromQuery(Name = "format")] string? Format = null,
    [FromQuery(Name = "platform")] string? Platform = null,
    [FromQuery(Name = "status")] ItemStatus? Status = null,
    [FromQuery(Name = "locationId")] Guid? LocationId = null,
    [FromQuery(Name = "tag")] string? Tag = null,
    [FromQuery(Name = "sort")] string? Sort = null,
    [FromQuery(Name = "limit")] int? Limit = null,
    [FromQuery(Name = "cursor")] string? Cursor = null);

/// <summary>What to create when an item isn't being added from a known release.</summary>
public sealed record ManualItemDetails(
    WorkKind? Kind = null,
    string? Title = null,
    int? Year = null,
    ReleaseFormat? Format = null,
    Guid? PlatformId = null,
    IReadOnlyList<string>? Creators = null,
    string? Barcode = null);

/// <summary>Body of <c>POST …/items</c>: from a <c>releaseId</c>, or manual details. Needs an <c>Idempotency-Key</c> header.</summary>
public sealed record CreateItemRequest(
    Guid? ReleaseId = null,
    ManualItemDetails? Manual = null,
    ItemStatus? Status = null,
    ItemCondition? Condition = null,
    ItemCompleteness? Completeness = null,
    Guid? LocationId = null,
    string? Notes = null,
    DateOnly? AcquiredOn = null,
    AddedVia? AddedVia = null,
    IReadOnlyList<Guid>? TagIds = null);

/// <summary>Body of <c>PATCH …/items/{id}</c> (JSON Merge Patch). Needs an <c>If-Match</c> header.</summary>
public sealed record UpdateItemRequest(
    ItemStatus? Status = null,
    ItemCondition? Condition = null,
    ItemCompleteness? Completeness = null,
    Guid? LocationId = null,
    string? Notes = null,
    DateOnly? AcquiredOn = null,
    string? LentTo = null,
    DateOnly? LentOn = null,
    string? CustomTitle = null,
    CoverChoice? CoverChoice = null,
    IReadOnlyList<Guid>? TagIds = null);

/// <summary>Body of <c>POST …/items/{id}:rematch</c>: swap the release, keep your copy data.</summary>
public sealed record RematchItemRequest(Guid? ReleaseId = null);

public enum BulkAction
{
    Patch,
    Delete,
}

/// <summary>The change a bulk request applies to every listed item.</summary>
public sealed record BulkItemsPatch(
    Guid? LocationId = null,
    ItemStatus? Status = null,
    IReadOnlyList<Guid>? AddTagIds = null,
    IReadOnlyList<Guid>? RemoveTagIds = null);

/// <summary>Body of <c>POST …/items:bulk</c>: patch or delete up to 500 items at once.</summary>
public sealed record BulkItemsRequest(IReadOnlyList<Guid>? Ids = null, BulkAction? Action = null, BulkItemsPatch? Patch = null);
