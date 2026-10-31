using CollectionMaxxing.Domain.Catalog;

namespace CollectionMaxxing.Application.Scanning.Learning;

/// <summary>
/// Which barcode links a library may answer from. A link from a provider or the catalogue is shared and has no library; a
/// link a person taught by picking a release by hand belongs to the library they taught it in. Every lookup that finds
/// a release by its barcode goes through here, so one library's pick, right or wrong, is never another library's answer.
/// </summary>
public static class ReleaseIdentifierScope
{
    /// <summary>The shared links and the ones <paramref name="libraryId"/> taught itself.</summary>
    public static IQueryable<ReleaseIdentifier> UsableBy(this IQueryable<ReleaseIdentifier> links, Guid libraryId) =>
        links.Where(i => i.LibraryId == null || i.LibraryId == libraryId);

    /// <summary>The shared links and the ones any of <paramref name="libraryIds"/> taught (a caller who belongs to several libraries).</summary>
    public static IQueryable<ReleaseIdentifier> UsableBy(this IQueryable<ReleaseIdentifier> links, IReadOnlyCollection<Guid> libraryIds) =>
        links.Where(i => i.LibraryId == null || libraryIds.Contains(i.LibraryId.Value));

    /// <summary>True when <paramref name="link"/> is shared or <paramref name="libraryId"/> taught it (for rows not saved yet).</summary>
    public static bool IsUsableBy(this ReleaseIdentifier link, Guid libraryId) => link.LibraryId is null || link.LibraryId == libraryId;
}
