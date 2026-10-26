using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using Microsoft.Extensions.Logging;

namespace CollectionMaxxing.Application.Catalog;

/// <summary>Mapping and provider calls shared by the catalogue use cases.</summary>
internal static class CatalogProjection
{
    /// <summary>The source of a result that came from the local catalogue.</summary>
    public const string LocalSource = "local";

    public static readonly ScoringProfile Profile = new ScoringOptions().Profile(ScoringProfileKind.BarcodeProduct);

    /// <summary>Stand-in id of a result that exists only at a provider and has not been saved to the catalogue.</summary>
    public static readonly Guid UnsavedId = Guid.Empty;

    public static string CoverUrl(Guid assetId) => $"/media/covers/{assetId}";

    public static WorkDto ToWorkDto(ProviderRelease p) =>
        new(UnsavedId, p.Kind, TitleCaser.ToTitleCase(p.Title), p.Year, p.Creators, [], p.Synopsis, p.CoverUrl);

    public static ReleaseDto ToReleaseDto(ProviderRelease p) =>
        new(UnsavedId, UnsavedId, TitleCaser.ToTitleCase(p.Title), p.Format ?? ReleaseFormat.Other, null, null, p.Edition, p.Publisher, null, p.Year,
            p.Creators, null, null, p.CoverUrl, ReleaseSource.Provider,
            p.Identifiers.Select(i => new ReleaseIdentifierDto(i.Type, i.Value)).ToList());

    /// <summary>A key under which a provider result and a catalogue release of the same product collide.</summary>
    public static string DuplicateKey(string title, int? year, string? platform) =>
        $"{TitleNormalizer.Normalize(title)}|{year}|{PlatformNames.Canonicalize(platform)}";

    /// <summary>
    /// Asks one provider, bounded by the timeout. A provider that throws, times out or ignores cancellation yields nothing
    /// and never fails the request; only the caller's own cancellation propagates.
    /// </summary>
    public static async Task<T?> AskProviderAsync<T>(
        ICatalogProvider provider, Func<CancellationToken, Task<T>> ask, TimeSpan timeout, ILogger logger, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        try
        {
            return await ask(linked.Token).WaitAsync(timeout, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // A provider may fail in any way; none of them may fail the request.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            logger.LogWarning(exception, "Catalogue provider {Provider} failed or timed out; carrying on without it.", provider.Name);
            return default;
        }
    }
}
