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
        new(UnsavedId, UnsavedId, TitleCaser.ToTitleCase(p.Title), p.Format ?? ReleaseFormat.Unknown, null, null, p.Edition, p.Publisher, null, p.Year,
            p.Creators, null, null, p.CoverUrl, ReleaseSource.Provider,
            p.Identifiers.Select(i => new ReleaseIdentifierDto(i.Type, i.Value)).ToList());

    /// <summary>A key under which a provider result and a catalogue release of the same product collide.</summary>
    public static string DuplicateKey(string title, int? year, string? platform) =>
        $"{TitleNormalizer.Normalize(title)}|{year}|{PlatformNames.Canonicalize(platform)}";

    /// <summary>
    /// A provider hit's work identity within its own provider: two hits with different keys from the same provider are
    /// different works (two IGDB games sharing a title). Null when the provider names only releases.
    /// </summary>
    public static string? ProviderWorkKey(ProviderRelease p) =>
        p.WorkKey is null ? null : $"{p.Provider}:{p.WorkKey}";

    /// <summary>
    /// The cross-provider work fallback (memo 18, "Data mapping"): kind plus the normalized title and creators, so the
    /// same book from Open Library and Google Books lands in one work. Films and TV add the year, so remakes stay apart.
    /// </summary>
    public static string ProviderFallbackKey(ProviderRelease p)
    {
        var creators = string.Join(
            '|', p.Creators.Select(TitleNormalizer.Normalize).Where(c => c.Length > 0).OrderBy(c => c, StringComparer.Ordinal));
        var title = TitleNormalizer.Normalize(p.Title);
        return p.Kind is WorkKind.Film or WorkKind.Tv
            ? $"{p.Kind}|{title}|{p.Year}|{creators}"
            : $"{p.Kind}|{title}|{creators}";
    }

    /// <summary>
    /// Per-type edition identity within one work (memo 18): books are format plus publisher plus year, games are
    /// platform plus format, films and TV are format plus label plus year. Hits sharing a key are the same edition.
    /// </summary>
    public static string ProviderEditionKey(ProviderRelease p) => p.Kind switch
    {
        WorkKind.Game => $"g|{PlatformNames.Canonicalize(p.Platform)}|{p.Format}|{TitleNormalizer.Normalize(p.Edition)}",
        WorkKind.Book => $"b|{p.Format}|{TitleNormalizer.Normalize(p.Publisher)}|{p.Year}|{TitleNormalizer.Normalize(p.Edition)}",
        WorkKind.Film or WorkKind.Tv => $"f|{p.Format}|{TitleNormalizer.Normalize(p.Publisher)}|{p.Year}|{TitleNormalizer.Normalize(p.Edition)}",
        _ => $"o|{p.Format}|{TitleNormalizer.Normalize(p.Publisher)}|{p.Year}|{TitleNormalizer.Normalize(p.Edition)}|{PlatformNames.Canonicalize(p.Platform)}",
    };

    /// <summary>
    /// Groups scored provider hits into works: by the fallback key across providers, split only when one provider's
    /// own work keys disagree, with duplicate editions keeping their best score. Releases arrive best-scored first.
    /// </summary>
    public static IReadOnlyList<ProviderWorkGroup> GroupProviderReleases(IEnumerable<(ProviderRelease Release, double Score)> scored)
    {
        var groups = new List<ProviderWorkGroup>();
        foreach (var fallback in scored.GroupBy(s => ProviderFallbackKey(s.Release)))
        {
            var keyed = fallback.Where(s => ProviderWorkKey(s.Release) is not null).GroupBy(s => ProviderWorkKey(s.Release)!).ToList();
            if (keyed.Count <= 1)
            {
                groups.Add(ToWorkGroup(fallback));
            }
            else
            {
                // One provider says these are different works: each keeps its own, and hits without a work key join the best-scored.
                var unkeyed = fallback.Where(s => ProviderWorkKey(s.Release) is null);
                var ordered = keyed.OrderByDescending(g => g.Max(s => s.Score)).ToList();
                groups.Add(ToWorkGroup(ordered[0].Concat(unkeyed)));
                groups.AddRange(ordered.Skip(1).Select(ToWorkGroup));
            }
        }

        return groups;
    }

    private static ProviderWorkGroup ToWorkGroup(IEnumerable<(ProviderRelease Release, double Score)> hits)
    {
        var best = new Dictionary<string, (ProviderRelease Release, double Score)>(StringComparer.Ordinal);
        foreach (var hit in hits)
        {
            var key = ProviderEditionKey(hit.Release);
            if (!best.TryGetValue(key, out var existing) || hit.Score > existing.Score)
            {
                best[key] = hit;
            }
        }

        return new ProviderWorkGroup(best.Values.OrderByDescending(v => v.Score).ToList());
    }

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

/// <summary>One work's share of scored provider hits, best-scored first; the first release is the work's face.</summary>
internal sealed record ProviderWorkGroup(IReadOnlyList<(ProviderRelease Release, double Score)> Releases);
