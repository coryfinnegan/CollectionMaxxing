using System.Security.Cryptography;
using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Scanning.Photo;
using CollectionMaxxing.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CollectionMaxxing.Application.Scanning;

/// <summary>
/// Gives the releases an add is about to use the cover their provider named. The release only remembers the cover's address
/// (<c>coverUrl</c> in its provider payload); this downloads it once, stores it as a shared cover asset and points the release
/// at it, so the book shows its picture in the library. A cover that can't be fetched is skipped — the item is still added —
/// and the outcome is recorded on the release (<see cref="CoverOutcome"/>), so a missing picture is debuggable and a later
/// pass can retry it. One download per distinct address: releases sharing a cover share its asset.
/// </summary>
public sealed class ReleaseCovers(
    IAppDbContext db, ICoverFetcher fetcher, IPhotoStore store, IIdGenerator ids, TimeProvider clock, ILogger<ReleaseCovers> logger)
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(12);

    /// <summary>Adds the assets and sets the releases' covers (saved by the caller). Returns the files written, to delete if the save fails.</summary>
    public async Task<IReadOnlyList<string>> FetchMissingAsync(IReadOnlyCollection<Guid> releaseIds, CancellationToken cancellationToken)
    {
        var releases = await db.Releases
            .Where(r => releaseIds.Contains(r.Id) && r.CoverAssetId == null && r.ProviderPayloadsJson != null)
            .ToListAsync(cancellationToken);
        // Plus what the caller just added and hasn't saved yet (an edited edition): it is no rows until the save.
        var seen = releases.Select(r => r.Id).ToHashSet();
        releases.AddRange(db.Releases.Local
            .Where(r => releaseIds.Contains(r.Id) && r.CoverAssetId == null && r.ProviderPayloadsJson != null && seen.Add(r.Id)));
        if (releases.Count == 0)
        {
            return [];
        }

        // No provider named a cover for these: nothing to fetch, but the answer is worth keeping.
        foreach (var release in releases)
        {
            if (CoverOutcome.CoverUrlOf(release.ProviderPayloadsJson) is null)
            {
                release.ProviderPayloadsJson = CoverOutcome.WithOutcome(release.ProviderPayloadsJson, CoverOutcome.NoCover, null);
            }
        }

        var wanted = releases
            .Select(release => (Release: release, Url: CoverOutcome.CoverUrlOf(release.ProviderPayloadsJson)))
            .Where(w => w.Url is not null)
            .GroupBy(w => w.Url!, StringComparer.Ordinal)
            .ToList();
        if (wanted.Count == 0)
        {
            return [];
        }

        // A retry after a crash must not mint a second asset for an address that already has one.
        var urls = wanted.Select(g => g.Key).ToList();
        var kept = (await db.Assets.AsNoTracking()
                .Where(a => a.Kind == AssetKind.Cover && a.OwnerLibraryId == null && a.SourceUrl != null && urls.Contains(a.SourceUrl))
                .ToListAsync(cancellationToken))
            .GroupBy(a => a.SourceUrl!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Budget);
        var fetched = await Task.WhenAll(wanted.Select(async group =>
        {
            if (kept.ContainsKey(group.Key))
            {
                return null; // already stored: the loop below just points the releases at it
            }

            try
            {
                return await fetcher.FetchAsync(group.Key, budget.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new CoverFetchResult(null, CoverFailure.TimedOut);
            }
        }));

        var written = new List<string>();
        var now = clock.GetUtcNow();
        for (var i = 0; i < wanted.Count; i++)
        {
            var group = wanted[i];
            if (fetched[i] is { Cover: { } cover })
            {
                var assetId = ids.NewId();
                var blobPath = BlobPathFor(assetId, cover.ContentType);
                await store.SaveAsync(blobPath, cover.Bytes, cancellationToken);
                written.Add(blobPath);
                db.Assets.Add(new Asset
                {
                    Id = assetId,
                    Kind = AssetKind.Cover,
                    BlobPath = blobPath,
                    ContentType = cover.ContentType,
                    Sha256 = Convert.ToHexStringLower(SHA256.HashData(cover.Bytes.Span)),
                    SourceUrl = group.Key,
                    CreatedAt = now,
                });
                kept[group.Key] = assetId;
            }

            if (kept.TryGetValue(group.Key, out var keptId))
            {
                foreach (var (release, _) in group)
                {
                    release.CoverAssetId = keptId;
                    release.ProviderPayloadsJson = CoverOutcome.WithOutcome(release.ProviderPayloadsJson, CoverOutcome.Fetched, null);
                }
            }
            else
            {
                var reason = fetched[i]?.FailureReason ?? CoverFailure.FetchFailed;
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("A provider cover could not be fetched ({Reason}); the item is added without one.", reason);
                }

                foreach (var (release, _) in group)
                {
                    release.ProviderPayloadsJson = CoverOutcome.WithOutcome(release.ProviderPayloadsJson, CoverOutcome.Failed, reason);
                }
            }
        }

        return written;
    }

    public async Task DeleteFilesAsync(IEnumerable<string> blobPaths)
    {
        foreach (var path in blobPaths)
        {
            await store.TryDeleteAsync(path, CancellationToken.None);
        }
    }

    private static string BlobPathFor(Guid assetId, string contentType)
    {
        var name = assetId.ToString("N");
        var extension = contentType switch { "image/png" => "png", "image/webp" => "webp", _ => "jpg" };
        return $"covers/{name[..2]}/{name}.{extension}";
    }
}
