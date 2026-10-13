using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Domain.Locations;

/// <summary>
/// A place in the physical tree: Room › Shelf › Bin. Changes go through <see cref="LocationTree"/>,
/// which enforces the rules that involve more than one location.
/// </summary>
public sealed class Location : ILibraryScoped
{
    public const int MaxNameLength = 100;

    // EF Core materialises entities through this constructor.
    private Location()
    {
        Name = string.Empty;
    }

    internal Location(Guid id, Guid libraryId, Guid? parentId, string name, int sortOrder)
    {
        Id = id;
        LibraryId = libraryId;
        ParentId = parentId;
        Name = name;
        SortOrder = sortOrder;
    }

    public Guid Id { get; private set; }

    public Guid LibraryId { get; private set; }

    public Guid? ParentId { get; private set; }

    public string Name { get; private set; }

    public int SortOrder { get; private set; }

    internal void Rename(string name) => Name = name;

    internal void MoveTo(Guid? parentId) => ParentId = parentId;

    internal void Reorder(int sortOrder) => SortOrder = sortOrder;
}
