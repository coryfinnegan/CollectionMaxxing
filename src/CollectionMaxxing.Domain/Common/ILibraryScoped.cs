namespace CollectionMaxxing.Domain.Common;

/// <summary>
/// Marks an entity that belongs to exactly one library. Infrastructure applies a global query filter
/// on <see cref="LibraryId"/> to every entity that implements this.
/// </summary>
public interface ILibraryScoped
{
    Guid LibraryId { get; }
}
