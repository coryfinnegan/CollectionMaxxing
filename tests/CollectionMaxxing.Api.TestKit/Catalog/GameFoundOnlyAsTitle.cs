using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;

namespace CollectionMaxxing.Api.Tests.Catalog;

/// <summary>
/// A games provider that knows one game and finds it for a search of exactly one of the titles it was given (whatever the case) and no other: the title
/// the game is known by without the sequel number a cover got wrong ("Wing Commander The Price of Freedom" for the game numbered
/// IV), so the search of the misread title, and of every shorter title cut from its end, finds nothing. Set <see cref="Slow"/> to
/// make every search wait until its budget cancels it.
/// </summary>
public sealed class GameFoundOnlyAsTitle(ProviderRelease game, params string[] titles) : ICatalogProvider, ISearchableCatalogProvider
{
    public bool Slow { get; set; }

    public string Name => game.Provider;

    public bool Supports(WorkKind kind) => kind == WorkKind.Game;

    public Task<ProviderRelease?> LookupAsync(Identifier id, CancellationToken ct) => Task.FromResult<ProviderRelease?>(null);

    public async Task<IReadOnlyList<ProviderRelease>> SearchAsync(CatalogQuery query, CancellationToken ct)
    {
        if (Slow)
        {
            await Task.Delay(Timeout.Infinite, ct);
        }

        return titles.Contains(query.Title, StringComparer.OrdinalIgnoreCase) ? [game] : [];
    }
}
