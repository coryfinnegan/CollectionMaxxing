namespace CollectionMaxxing.Application.Catalog.Providers;

/// <summary>
/// A catalogue provider that can answer a title search (<see cref="ICatalogProvider.SearchAsync"/>). Lookup-only
/// providers (UPCitemdb, whose search quota is kept for barcodes) implement <see cref="ICatalogProvider"/> but not
/// this: the barcode scorer only trusts a title search that a searchable provider actually ran.
/// </summary>
public interface ISearchableCatalogProvider : ICatalogProvider
{
}
