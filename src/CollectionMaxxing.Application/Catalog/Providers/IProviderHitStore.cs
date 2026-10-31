namespace CollectionMaxxing.Application.Catalog.Providers;

/// <summary>
/// The provider hits a catalogue search showed, kept briefly so an add can name one by its provider and id
/// (<c>ProviderRefDto</c>) instead of by a catalogue id it doesn't have yet. The add reads the hit back from here, never from
/// the caller: a request can only add what the server itself showed, so the shared catalogue never takes text from a client.
/// </summary>
public interface IProviderHitStore
{
    /// <summary>Keeps the hits (replacing any earlier copy of the same provider and id) and forgets the ones whose day has passed.</summary>
    Task SaveAsync(IReadOnlyCollection<ProviderRelease> hits, CancellationToken ct);

    /// <summary>The kept hit, or null when it was never shown or its day has passed.</summary>
    Task<ProviderRelease?> FindAsync(string provider, string externalId, CancellationToken ct);
}
