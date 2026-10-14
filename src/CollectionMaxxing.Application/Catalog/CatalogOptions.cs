namespace CollectionMaxxing.Application.Catalog;

/// <summary>Tuning for the catalogue use cases. Registered as a singleton; tests shorten the timeout.</summary>
public sealed class CatalogOptions
{
    /// <summary>How long one provider may take to answer before the request carries on without it.</summary>
    public TimeSpan ProviderTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
