using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;

namespace CollectionMaxxing.Application.Catalog.Providers;

/// <summary>
/// The port every catalogue provider (Open Library, Google Books, UPCitemdb, TMDB, IGDB) implements; the HTTP clients live
/// in Infrastructure (docs/02 section 3, "Providers").
/// </summary>
/// <remarks>
/// docs/02 writes <c>Supports(MediaKind kind)</c>. The kind is the existing <see cref="WorkKind"/> (book, film, TV, game,
/// other), so no second enum was introduced.
/// </remarks>
public interface ICatalogProvider
{
    /// <summary>A short stable name ("openlibrary", "igdb"), used in resolution traces and in cache keys.</summary>
    string Name { get; }

    /// <summary>Whether this provider can answer for works of that kind.</summary>
    bool Supports(WorkKind kind);

    /// <summary>Looks one release up by barcode or catalogue number. Null when the provider does not know it.</summary>
    Task<ProviderRelease?> LookupAsync(Identifier id, CancellationToken ct);

    /// <summary>Searches by title and whatever else is known, best provider order first.</summary>
    Task<IReadOnlyList<ProviderRelease>> SearchAsync(CatalogQuery query, CancellationToken ct);
}
