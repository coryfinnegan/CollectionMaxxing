using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Jobs;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CollectionMaxxing.Application.Catalog;

/// <summary>
/// Recurring job (every UTC night): gives covers to the shared releases that have none — books added before covers were
/// fetched, or while fetching silently failed. A payload that names a cover is downloaded; a payload that names none is
/// re-resolved against the book providers when its answer is missing or stale. Anything else keeps its outcome for the
/// next night. The product lookup's daily quota is never touched: only books by ISBN are re-asked, through the same
/// rate-limited providers as a scan, and only provider failures (never a person waiting) are retried the next night.
/// Safe to repeat: a fetched release is no longer a candidate, and a stored address is reused, never downloaded twice.
/// </summary>
public sealed class BackfillCovers(
    IAppDbContext db, ReleaseCovers covers, BookLookup books, CatalogOptions options, TimeProvider clock, ILogger<BackfillCovers> logger)
    : IJobHandler<BackfillCovers.Job>
{
    public const string JobType = "catalog.backfill_covers";

    /// <summary>Recurring jobs carry no data.</summary>
    public sealed record Job;

    /// <summary>How many releases one run takes on, oldest first; the rest wait for the next night.</summary>
    internal const int MaxPerRun = 500;

    /// <summary>Releases per save; each batch downloads its covers side by side under the cover budget.</summary>
    internal const int BatchSize = 10;

    /// <summary>How long a "no provider names a cover" answer stands before the providers are asked again.</summary>
    internal static readonly TimeSpan RecheckAfter = TimeSpan.FromDays(30);

    public async Task HandleAsync(Job payload, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var staleBefore = now - RecheckAfter;
        var rows = await db.Releases.AsNoTracking()
            .Where(r => r.OwnerLibraryId == null && r.CoverAssetId == null && r.ProviderPayloadsJson != null)
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id)
            .Select(r => new { r.Id, r.ProviderPayloadsJson, r.RefreshedAt })
            .ToListAsync(cancellationToken);
        var todo = rows.Where(r => NeedsWork(r.ProviderPayloadsJson, r.RefreshedAt, staleBefore)).Take(MaxPerRun).ToList();
        if (todo.Count == 0)
        {
            return;
        }

        var fetched = 0;
        foreach (var batch in todo.Chunk(BatchSize))
        {
            fetched += await RunBatchAsync(batch.Select(b => b.Id).ToList(), now, cancellationToken);
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Cover backfill looked at {Count} releases and fetched {Fetched} covers; the rest wait for the next night.",
                todo.Count, fetched);
        }
    }

    /// <summary>A release needs work when a cover address waits to be fetched, or no fresh answer says none exists.</summary>
    private static bool NeedsWork(string? payloadJson, DateTimeOffset? refreshedAt, DateTimeOffset staleBefore)
    {
        if (CoverOutcome.CoverUrlOf(payloadJson) is not null)
        {
            return true;
        }

        return CoverOutcome.Read(payloadJson).Status != CoverOutcome.NoCover || refreshedAt is null || refreshedAt < staleBefore;
    }

    private async Task<int> RunBatchAsync(IReadOnlyList<Guid> ids, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var releases = await db.Releases.Where(r => ids.Contains(r.Id)).ToListAsync(cancellationToken);
        foreach (var release in releases.Where(r => CoverOutcome.CoverUrlOf(r.ProviderPayloadsJson) is null))
        {
            await ReResolveAsync(release, now, cancellationToken);
        }

        var written = await covers.FetchMissingAsync(ids, cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await covers.DeleteFilesAsync(written); // nothing points at the covers just downloaded
            throw;
        }

        return releases.Count(r => r.CoverAssetId is not null);
    }

    /// <summary>
    /// Asks the book providers again for a cover address. Only books by ISBN are re-asked (a product lookup would spend
    /// the daily allowance); anything else is marked as having no cover. Stale kept answers are forgotten first, so the
    /// providers are really asked; what they say is kept for good, as in a scan.
    /// </summary>
    private async Task ReResolveAsync(Release release, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var isbn = await IsbnAsync(release.Id, cancellationToken);
        if (isbn is null)
        {
            release.ProviderPayloadsJson = CoverOutcome.WithOutcome(release.ProviderPayloadsJson, CoverOutcome.NoCover, null)
                ?? release.ProviderPayloadsJson;
            release.RefreshedAt = now;
            return;
        }

        await ForgetStaleAnswersAsync(isbn.Value, cancellationToken);
        var book = await books.LookupAsync(isbn, null, options.ProviderTimeout, cancellationToken);
        if (book.Release is { CoverUrl: { } url })
        {
            release.ProviderPayloadsJson =
                CoverOutcome.WithCover(release.ProviderPayloadsJson, book.Release.Provider, book.Release.ExternalId, url);
            return;
        }

        // Asked and answered: no provider names a cover. A provider failure answers nothing and is asked again next night.
        release.ProviderPayloadsJson = CoverOutcome.WithOutcome(release.ProviderPayloadsJson, CoverOutcome.NoCover, null)
            ?? release.ProviderPayloadsJson;
        if (!book.Failed)
        {
            release.RefreshedAt = now;
        }
    }

    private async Task<Identifier?> IsbnAsync(Guid releaseId, CancellationToken cancellationToken)
    {
        var values = await db.ReleaseIdentifiers.AsNoTracking()
            .Where(i => i.ReleaseId == releaseId)
            .Select(i => i.Value)
            .ToListAsync(cancellationToken);
        foreach (var value in values)
        {
            if (Identifier.TryParse(value, out var id) && id.Type == IdentifierType.Isbn13)
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>
    /// Forgets the kept ISBN answers (the permanent copy and every provider's cached lookup) so the re-ask is fresh.
    /// Only the permanent copy keys rows by the bare ISBN; every provider lookup prefixes its key.
    /// </summary>
    private async Task ForgetStaleAnswersAsync(string isbn13, CancellationToken cancellationToken)
    {
        await db.ProviderCache
            .Where(c => c.Key == isbn13 || c.Key == $"lookup:isbn:{isbn13}")
            .ExecuteDeleteAsync(cancellationToken);
    }
}
