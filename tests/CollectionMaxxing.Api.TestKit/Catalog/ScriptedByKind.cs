using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;

namespace CollectionMaxxing.Api.Tests.Catalog;

/// <summary>
/// A provider of one kind, as the real ones are: it is asked only for the kinds it <see cref="Supports"/> (books, or films and
/// shows), and answers a search from a script. It knows the releases it was told for a title, whatever its case, spacing and
/// punctuation, either in any ask or only in an ask that names its own kind (<see cref="Know"/>). That is what lets a test say that a
/// read finds films until it is asked as a book. It remembers every title and kind asked, in order, and can hang on an ask until the
/// budget cancels it (<see cref="Hangs"/>).
/// </summary>
public sealed class ScriptedByKind(WorkKind own) : ICatalogProvider, ISearchableCatalogProvider
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (ProviderRelease[] Releases, bool OnlyAsItsKind)> _known = new(StringComparer.Ordinal);
    private readonly List<(string Title, WorkKind? Kind)> _asked = [];

    public string Name => own == WorkKind.Book ? "scripted-books" : "scripted-films";

    /// <summary>The asks this is true for (by the title and the kind asked) never answer: they wait for the budget to cancel them.</summary>
    public Func<string, WorkKind?, bool>? Hangs { get; set; }

    /// <summary>Every title and kind asked about so far, in order.</summary>
    public IReadOnlyList<(string Title, WorkKind? Kind)> Asked
    {
        get
        {
            lock (_gate)
            {
                return [.. _asked];
            }
        }
    }

    public bool Supports(WorkKind kind) => own == WorkKind.Book ? kind == WorkKind.Book : kind is WorkKind.Film or WorkKind.Tv;

    /// <summary>The releases a search for exactly this title finds; with <paramref name="onlyAsItsKind"/>, only when the ask names this provider's own kind.</summary>
    public ScriptedByKind Know(string askedTitle, bool onlyAsItsKind, params ProviderRelease[] releases)
    {
        _known[Key(askedTitle)] = (releases, onlyAsItsKind);
        return this;
    }

    public Task<ProviderRelease?> LookupAsync(Identifier id, CancellationToken ct) => Task.FromResult<ProviderRelease?>(null);

    public async Task<IReadOnlyList<ProviderRelease>> SearchAsync(CatalogQuery query, CancellationToken ct)
    {
        lock (_gate)
        {
            _asked.Add((query.Title, query.Kind));
        }

        if (Hangs?.Invoke(query.Title, query.Kind) == true)
        {
            await Task.Delay(Timeout.Infinite, ct);
        }

        return _known.TryGetValue(Key(query.Title), out var found) && (!found.OnlyAsItsKind || query.Kind == own) ? found.Releases : [];
    }

    private static string Key(string title) => string.Concat(title.Where(char.IsLetterOrDigit)).ToLowerInvariant();
}
