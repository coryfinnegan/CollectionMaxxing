using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;

namespace CollectionMaxxing.Api.Tests.Catalog;

/// <summary>
/// A provider that answers a search from a script: the releases it was told to know for a title, whatever its case, spacing and
/// punctuation, and nothing for any other title. It remembers every title it was asked about, in order, so a test can say what
/// was asked and what was not. Set <see cref="Hangs"/> to make a search wait until its budget cancels it.
/// </summary>
public sealed class ScriptedByTitle : ICatalogProvider, ISearchableCatalogProvider
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ProviderRelease[]> _known = new(StringComparer.Ordinal);
    private readonly List<string> _asked = [];

    public string Name => "scripted";

    /// <summary>The searches this is true for (by the title asked) never answer: they wait for the budget to cancel them.</summary>
    public Func<string, bool>? Hangs { get; set; }

    /// <summary>Every title asked about so far, in order.</summary>
    public IReadOnlyList<string> Asked
    {
        get
        {
            lock (_gate)
            {
                return [.. _asked];
            }
        }
    }

    public bool Supports(WorkKind kind) => true;

    /// <summary>A release the script can hand out. Titles are the catalogue's, not the reads'.</summary>
    public static ProviderRelease Release(string title, WorkKind kind, string externalId, int? year = null, params string[] creators) => new()
    {
        Provider = "scripted",
        ExternalId = externalId,
        Title = title,
        Kind = kind,
        Format = kind == WorkKind.Book ? ReleaseFormat.Paperback : kind == WorkKind.Film ? ReleaseFormat.Dvd : null,
        Year = year,
        Creators = creators,
    };

    /// <summary>The releases a search for exactly this title finds.</summary>
    public ScriptedByTitle Know(string askedTitle, params ProviderRelease[] releases)
    {
        _known[Key(askedTitle)] = releases;
        return this;
    }

    public Task<ProviderRelease?> LookupAsync(Identifier id, CancellationToken ct) => Task.FromResult<ProviderRelease?>(null);

    public async Task<IReadOnlyList<ProviderRelease>> SearchAsync(CatalogQuery query, CancellationToken ct)
    {
        lock (_gate)
        {
            _asked.Add(query.Title);
        }

        if (Hangs?.Invoke(query.Title) == true)
        {
            await Task.Delay(Timeout.Infinite, ct);
        }

        return _known.TryGetValue(Key(query.Title), out var found) ? found : [];
    }

    private static string Key(string title) => string.Concat(title.Where(char.IsLetterOrDigit)).ToLowerInvariant();
}
