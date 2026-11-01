using CollectionMaxxing.Application.Catalog;

namespace CollectionMaxxing.Api.Tests.Evals;

/// <summary>The real local catalogue search, except that it refuses to answer while <paramref name="fails"/> says so: a lookup that fails under the PC.</summary>
internal sealed class FlakyLocalSearch(ILocalCatalogSearch inner, Func<bool> fails) : ILocalCatalogSearch
{
    public Task<IReadOnlyList<LocalHit>> FindAsync(LocalSearch search, CancellationToken cancellationToken) =>
        fails() ? throw new InvalidOperationException("the catalogue is unavailable") : inner.FindAsync(search, cancellationToken);

    public Task<IReadOnlyList<IReadOnlyList<LocalHit>>> FindManyAsync(IReadOnlyList<LocalSearch> searches, CancellationToken cancellationToken) =>
        fails() ? throw new InvalidOperationException("the catalogue is unavailable") : inner.FindManyAsync(searches, cancellationToken);
}
