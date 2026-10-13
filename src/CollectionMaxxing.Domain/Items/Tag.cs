using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Domain.Items;

/// <summary>A label you put on items. Names are unique per library, ignoring case.</summary>
public sealed class Tag : ILibraryScoped, IConcurrencyVersioned
{
    public const int MaxNameLength = 50;

    public Guid Id { get; init; }

    public Guid LibraryId { get; init; }

    public string Name { get; set; } = string.Empty;

    public uint Version { get; private set; }
}

/// <summary>Puts a tag on an item. Both must be in <see cref="LibraryId"/>, which the database enforces.</summary>
public sealed class ItemTag : ILibraryScoped
{
    public Guid LibraryId { get; init; }

    public Guid ItemId { get; init; }

    public Guid TagId { get; init; }
}
