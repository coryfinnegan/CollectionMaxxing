namespace CollectionMaxxing.Application.Catalog.Providers;

/// <summary>
/// A catalogue provider that can list the other titles of a release it found (a regional title, the title printed on a tape).
/// The title search asks it for a read nothing matched, so the scorer can compare the read with those titles too.
/// </summary>
public interface IAlternativeTitleProvider : ICatalogProvider
{
    /// <summary>Whether this provider has alternative titles for that release (its own, and of a kind it lists them for).</summary>
    bool HasAlternativeTitles(ProviderRelease release);

    /// <summary>
    /// The release's alternative titles, from the provider's cache when it was asked before. Null when the provider did not
    /// answer (a failure, a refused call): the caller leaves the release as it was. Never throws for a provider failure.
    /// </summary>
    Task<IReadOnlyList<string>?> AlternativeTitlesAsync(ProviderRelease release, CancellationToken ct);
}
