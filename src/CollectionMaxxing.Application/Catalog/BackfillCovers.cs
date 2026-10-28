using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Jobs;
using CollectionMaxxing.Application.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CollectionMaxxing.Application.Catalog;

/// <summary>
/// Recurring job (every UTC night): gives covers to the shared releases that have none — books added before covers were
/// fetched, or while fetching silently failed. A payload that names a cover is downloaded; a payload that names none is
/// re-resolved against the book providers when its answer is missing or stale. Anything else keeps its outcome for the
/// next night. Only books by ISBN are re-asked, and only through the scan's own book-provider list — the backfill
/// names no quota'd provider and never calls the product lookup, so a nightly run cannot spend its daily quota, however
/// many releases it takes on. Only provider failures (never a person waiting) are retried the next night.
/// Safe to repeat: a fetched release is no longer a candidate, and a stored address is reused, never downloaded twice.
/// </summary>
public sealed class BackfillCovers(
    IAppDbContext db, ReleaseCovers covers, CoverReResolver reResolve, TimeProvider clock, ILogger<BackfillCovers> logger)
    : IJobHandler<BackfillCovers.Job>
{
    public const string JobType = "catalog.backfill_covers";

    /// <summary>Recurring jobs carry no data.</summary>
    public sealed record Job;

    /// <summary>How many releases one run takes on, oldest first; the rest wait for the next night.</summary>
    internal const int MaxPerRun = 500;

    /// <summary>Releases per save; each batch downloads its covers side by side under the cover budget.</summary>
    internal const int BatchSize = 10;

    /// <summary>
    /// How long a "no provider names a cover" answer stands before the providers are asked again. One night, not
    /// thirty days (#490): the verdict only means no <em>consulted</em> provider named one, and provider availability
    /// changes (a key lands, a catalog gains the cover), so every no-cover release is re-verified by the next run.
    /// </summary>
    internal static readonly TimeSpan RecheckAfter = TimeSpan.FromDays(1);

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
            await reResolve.ReResolveAsync(release, now, cancellationToken);
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

}
