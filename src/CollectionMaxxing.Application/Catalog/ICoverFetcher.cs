namespace CollectionMaxxing.Application.Catalog;

/// <summary>A cover image downloaded from a provider: a sound JPEG, PNG or WebP, not larger than the fetcher's cap.</summary>
public sealed record FetchedCover(ReadOnlyMemory<byte> Bytes, string ContentType);

/// <summary>
/// Downloads a cover a provider named. Implemented by Infrastructure over the guarded fetcher (allow-listed hosts, https, public
/// addresses, size cap). Null for anything that isn't a usable image; it never throws for a bad or unreachable URL.
/// </summary>
public interface ICoverFetcher
{
    Task<FetchedCover?> FetchAsync(string url, CancellationToken cancellationToken);
}
