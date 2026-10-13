using System.Text.Json.Serialization;

namespace CollectionMaxxing.Api.Locations;

/// <summary>Body of <c>POST …/locations</c>. A missing name is reported by the domain, with the same error as a blank one.</summary>
public sealed record CreateLocationRequest(string? Name = null, Guid? ParentId = null, int? SortOrder = null);

/// <summary>
/// Body of <c>PATCH …/locations/{id}</c> (JSON Merge Patch, RFC 7396). A property that is absent is left alone.
/// <c>"parentId": null</c> means "move to the top level", which is why we need to know whether it was sent at all.
/// </summary>
public sealed record UpdateLocationRequest
{
    private Guid? _parentId;

    public string? Name { get; init; }

    public int? SortOrder { get; init; }

    public Guid? ParentId
    {
        get => _parentId;
        init
        {
            // The JSON serializer calls this setter only when the property is present in the body.
            _parentId = value;
            ParentIdSpecified = true;
        }
    }

    [JsonIgnore]
    public bool ParentIdSpecified { get; private init; }
}
