using System.Text.Json.Serialization;
using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Items;
using Microsoft.AspNetCore.Mvc;

namespace CollectionMaxxing.Api.Items;

/// <summary>Query of <c>GET …/items</c>. <c>kind</c>, <c>format</c>, <c>status</c>, <c>platform</c> and <c>sort</c> are text (minimal APIs bind enums case-sensitively, the API spells them lower snake_case); a handler validates them. <c>editionVerified</c> narrows to items whose edition is proven (or not).</summary>
public sealed record ListItemsQuery(
    [FromQuery(Name = "q")] string? Q = null,
    [FromQuery(Name = "kind")] string? Kind = null,
    [FromQuery(Name = "format")] string? Format = null,
    [FromQuery(Name = "platform")] string? Platform = null,
    [FromQuery(Name = "status")] string? Status = null,
    [FromQuery(Name = "locationId")] Guid? LocationId = null,
    [FromQuery(Name = "tag")] string? Tag = null,
    [FromQuery(Name = "sort")] string? Sort = null,
    [FromQuery(Name = "limit")] int? Limit = null,
    [FromQuery(Name = "cursor")] string? Cursor = null,
    [FromQuery(Name = "editionVerified")] bool? EditionVerified = null);

/// <summary>What to create when an item isn't being added from a known release.</summary>
public sealed record ManualItemDetails(
    WorkKind? Kind = null,
    string? Title = null,
    int? Year = null,
    ReleaseFormat? Format = null,
    Guid? PlatformId = null,
    IReadOnlyList<string>? Creators = null,
    string? Barcode = null);

/// <summary>
/// Body of <c>POST …/items</c>: from a <c>releaseId</c>, from a search result only a provider knows (<c>providerRef</c>, as the
/// search returned it on the release), or manual details; exactly one of the three. Needs an <c>Idempotency-Key</c> header.
/// </summary>
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
    IReadOnlyList<Guid>? TagIds = null,
    ProviderRefDto? ProviderRef = null);

/// <summary>
/// Body of <c>PATCH …/items/{id}</c> (JSON Merge Patch, RFC 7396). Needs an <c>If-Match</c> header. A property that is
/// absent is left alone; one sent as <c>null</c> is cleared. The setters below run only for properties present in the body.
/// </summary>
public sealed record UpdateItemRequest
{
    private ItemCondition? _condition;
    private ItemCompleteness? _completeness;
    private Guid? _locationId;
    private string? _notes;
    private DateOnly? _acquiredOn;
    private string? _lentTo;
    private DateOnly? _lentOn;
    private string? _customTitle;

    public ItemStatus? Status { get; init; }

    public ItemCondition? Condition { get => _condition; init { _condition = value; ConditionSpecified = true; } }

    public ItemCompleteness? Completeness { get => _completeness; init { _completeness = value; CompletenessSpecified = true; } }

    public Guid? LocationId { get => _locationId; init { _locationId = value; LocationIdSpecified = true; } }

    public string? Notes { get => _notes; init { _notes = value; NotesSpecified = true; } }

    public DateOnly? AcquiredOn { get => _acquiredOn; init { _acquiredOn = value; AcquiredOnSpecified = true; } }

    public string? LentTo { get => _lentTo; init { _lentTo = value; LentToSpecified = true; } }

    public DateOnly? LentOn { get => _lentOn; init { _lentOn = value; LentOnSpecified = true; } }

    public string? CustomTitle { get => _customTitle; init { _customTitle = value; CustomTitleSpecified = true; } }

    public CoverChoice? CoverChoice { get; init; }

    public IReadOnlyList<Guid>? TagIds { get; init; }

    [JsonIgnore]
    public bool ConditionSpecified { get; private init; }

    [JsonIgnore]
    public bool CompletenessSpecified { get; private init; }

    [JsonIgnore]
    public bool LocationIdSpecified { get; private init; }

    [JsonIgnore]
    public bool NotesSpecified { get; private init; }

    [JsonIgnore]
    public bool AcquiredOnSpecified { get; private init; }

    [JsonIgnore]
    public bool LentToSpecified { get; private init; }

    [JsonIgnore]
    public bool LentOnSpecified { get; private init; }

    [JsonIgnore]
    public bool CustomTitleSpecified { get; private init; }

    public ItemPatch ToPatch() => new()
    {
        Status = Status,
        Condition = ConditionSpecified ? PatchValue<ItemCondition?>.Of(Condition) : default,
        Completeness = CompletenessSpecified ? PatchValue<ItemCompleteness?>.Of(Completeness) : default,
        LocationId = LocationIdSpecified ? PatchValue<Guid?>.Of(LocationId) : default,
        Notes = NotesSpecified ? PatchValue<string?>.Of(Notes) : default,
        AcquiredOn = AcquiredOnSpecified ? PatchValue<DateOnly?>.Of(AcquiredOn) : default,
        LentTo = LentToSpecified ? PatchValue<string?>.Of(LentTo) : default,
        LentOn = LentOnSpecified ? PatchValue<DateOnly?>.Of(LentOn) : default,
        CustomTitle = CustomTitleSpecified ? PatchValue<string?>.Of(CustomTitle) : default,
        CoverChoice = CoverChoice,
        TagIds = TagIds,
    };
}

/// <summary>
/// Body of <c>POST …/items/{id}:rematch</c>: swap the release, keep your copy data. With <c>Barcode</c> instead of a
/// release, the barcode verifies the edition in place (docs/16 §11): a barcode of the item's own work swaps to that
/// edition and verifies it, anything else is refused and nothing changes.
/// </summary>
public sealed record RematchItemRequest(Guid? ReleaseId = null, string? Barcode = null);

public enum BulkAction
{
    Patch,
    Delete,
}

/// <summary>
/// The change a bulk request applies to every listed item. A <c>locationId</c> that is absent leaves locations alone; one sent as
/// <c>null</c> clears them (the same merge-patch reading as <see cref="UpdateItemRequest"/>).
/// </summary>
public sealed record BulkItemsPatch
{
    private Guid? _locationId;

    public Guid? LocationId { get => _locationId; init { _locationId = value; LocationIdSpecified = true; } }

    public ItemStatus? Status { get; init; }

    public IReadOnlyList<Guid>? AddTagIds { get; init; }

    public IReadOnlyList<Guid>? RemoveTagIds { get; init; }

    [JsonIgnore]
    public bool LocationIdSpecified { get; private init; }

    public BulkChange ToChange() => new(
        LocationIdSpecified ? PatchValue<Guid?>.Of(LocationId) : default, Status, AddTagIds, RemoveTagIds);
}

/// <summary>Body of <c>POST …/items:restore</c>: undo the delete of up to 500 items at once.</summary>
public sealed record RestoreItemsRequest(IReadOnlyList<Guid>? Ids = null);

/// <summary>Body of <c>POST …/items:bulk</c>: patch or delete up to 500 items at once.</summary>
public sealed record BulkItemsRequest(IReadOnlyList<Guid>? Ids = null, BulkAction? Action = null, BulkItemsPatch? Patch = null);
