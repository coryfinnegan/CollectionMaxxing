using CollectionMaxxing.Domain.Catalog;

namespace CollectionMaxxing.Application.Catalog;

/// <summary>What to look for in the local catalogue. <see cref="NormalizedTitle"/> is already run through the title normaliser.</summary>
public sealed record LocalSearch(string NormalizedTitle, WorkKind? Kind, IReadOnlyCollection<Guid>? PlatformIds, int? Year, int Cap);

/// <summary>One release found locally, with its work.</summary>
public sealed record LocalHit(Release Release, Work Work);

/// <summary>
/// The trigram search on the local catalogue (Postgres <c>pg_trgm</c>, so it lives in Infrastructure). It reads through the
/// caller's database filter: only global releases and releases of the caller's libraries are ever returned, and a work is
/// reached only through such a release. Best match first.
/// </summary>
public interface ILocalCatalogSearch
{
    Task<IReadOnlyList<LocalHit>> FindAsync(LocalSearch search, CancellationToken cancellationToken);

    /// <summary>
    /// <see cref="FindAsync"/> for each of <paramref name="searches"/>: the answer holds, in the same order, exactly the hits that
    /// search finds on its own. An implementation can answer many in far fewer round trips than one query each, which is why a
    /// caller with a list of titles (a picture of a shelf) asks for them together.
    /// </summary>
    async Task<IReadOnlyList<IReadOnlyList<LocalHit>>> FindManyAsync(IReadOnlyList<LocalSearch> searches, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(searches);
        var found = new List<IReadOnlyList<LocalHit>>(searches.Count);
        foreach (var search in searches)
        {
            found.Add(await FindAsync(search, cancellationToken));
        }

        return found;
    }
}
