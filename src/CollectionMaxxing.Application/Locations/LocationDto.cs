using CollectionMaxxing.Domain.Locations;

namespace CollectionMaxxing.Application.Locations;

/// <summary>A location as the API shows it. The client builds the tree from <c>ParentId</c>.</summary>
public sealed record LocationDto(Guid Id, Guid? ParentId, string Name, int SortOrder)
{
    public static LocationDto From(Location location) =>
        new(location.Id, location.ParentId, location.Name, location.SortOrder);
}
