using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Identifiers;

namespace CollectionMaxxing.Application.Scanning;

/// <summary>How a product-barcode lookup ended.</summary>
public enum ProductLookupStatus
{
    /// <summary>The provider knows the product; <see cref="ProductLookupResult.Release"/> is set.</summary>
    Found,

    /// <summary>The provider answered and does not know the code.</summary>
    NotFound,

    /// <summary>The provider could not answer (down, timing out, an error). Worth trying again later.</summary>
    Unavailable,

    /// <summary>The provider's daily quota is spent. Worth trying again tomorrow.</summary>
    QuotaExhausted,
}

public sealed record ProductLookupResult(ProductLookupStatus Status, ProviderRelease? Release = null)
{
    public static ProductLookupResult NotFound { get; } = new(ProductLookupStatus.NotFound);

    public static ProductLookupResult Unavailable { get; } = new(ProductLookupStatus.Unavailable);

    public static ProductLookupResult QuotaExhausted { get; } = new(ProductLookupStatus.QuotaExhausted);

    public static ProductLookupResult Found(ProviderRelease release) => new(ProductLookupStatus.Found, release);
}

/// <summary>
/// UPC/EAN to a product name (UPCitemdb; docs/02 section 3, step 3). A port of its own, because the catalogue port
/// answers a failed call and an unknown code the same way (null), and the scanner must tell them apart: an unknown code is
/// Not found, a failed call is Pending.
/// </summary>
public interface IProductLookup
{
    Task<ProductLookupResult> LookupAsync(Identifier id, CancellationToken cancellationToken);
}

/// <summary>
/// Serialises the writers of one scan session across requests (and processes), so a retried or concurrent batch finds the
/// first one's rows instead of racing it. Hold it only while claiming rows, never across a provider call.
/// </summary>
public interface ISessionLock
{
    Task<IAsyncDisposable> AcquireAsync(Guid sessionId, CancellationToken cancellationToken);
}
