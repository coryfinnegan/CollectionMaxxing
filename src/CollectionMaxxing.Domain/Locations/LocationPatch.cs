namespace CollectionMaxxing.Domain.Locations;

/// <summary>
/// A partial update (JSON Merge Patch). <c>null</c> on <c>Name</c> or <c>SortOrder</c> means "leave alone".
/// A null parent is meaningful ("move to the top level"), so <c>ParentSpecified</c> says whether to apply it.
/// </summary>
public sealed record LocationPatch(
    string? Name = null,
    bool ParentSpecified = false,
    Guid? ParentId = null,
    int? SortOrder = null);
