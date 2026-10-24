using System.Security.Cryptography;
using System.Text.Json;
using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Scanning.Photo;
using CollectionMaxxing.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CollectionMaxxing.Application.Scanning;

/// <summary>
/// Gives the shared releases a commit is about to use the cover their provider named. The release only remembers the cover's address
/// (<c>coverUrl</c> in its provider payload); this downloads it once, stores it as a shared cover asset and points the release at it, so
/// the book shows its picture in the library. A cover that can't be fetched is skipped: the item is still added and the next commit tries again.
/// </summary>
public sealed class ReleaseCovers(
    IAppDbContext db, ICoverFetcher fetcher, IPhotoStore store, IIdGenerator ids, TimeProvider clock, ILogger<ReleaseCovers> logger)
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(12);

    /// <summary>Adds the assets and sets the releases' covers (saved by the caller). Returns the files written, to delete if the save fails.</summary>
    public async Task<IReadOnlyList<string>> FetchMissingAsync(IReadOnlyCollection<Guid> releaseIds, CancellationToken cancellationToken)
    {
        var releases = await db.Releases
            .Where(r => releaseIds.Contains(r.Id) && r.OwnerLibraryId == null && r.CoverAssetId == null && r.ProviderPayloadsJson != null)
            .ToListAsync(cancellationToken);
        var wanted = new List<(Release Release, string Url)>();
        foreach (var release in releases)
        {
            if (CoverUrlOf(release.ProviderPayloadsJson) is { } url)
            {
                wanted.Add((release, url));
            }
        }

        if (wanted.Count == 0)
        {
            return [];
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Budget);
        var fetched = await Task.WhenAll(wanted.Select(async w =>
        {
            try
            {
                return await fetcher.FetchAsync(w.Url, budget.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }));

        var written = new List<string>();
        var now = clock.GetUtcNow();
        for (var i = 0; i < wanted.Count; i++)
        {
            if (fetched[i] is not { } cover)
            {
                logger.LogInformation("A provider cover could not be fetched; the item is added without one.");
                continue;
            }

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
                SourceUrl = wanted[i].Url,
                CreatedAt = now,
            });
            wanted[i].Release.CoverAssetId = assetId;
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

    private static string? CoverUrlOf(string? payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson!);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("coverUrl", out var url)
                && url.ValueKind == JsonValueKind.String
                    ? url.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
