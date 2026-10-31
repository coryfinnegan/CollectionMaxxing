using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Items;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Items;

/// <summary>
/// Use case: counts of a library's items by kind, format, platform and decade. Deleted items don't count, and neither do
/// wishlist or sold items: the total and the four breakdowns are the collection (<see cref="ItemStatuses.InCollection"/>).
/// The wishlist, sold, lent and missing counts come from the same single query.
/// </summary>
public static class GetStats
{
    public sealed record Query(Guid LibraryId);

    public sealed class Handler(IAppDbContext db)
    {
        public async Task<StatsDto> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            // One GROUP BY on everything the counts are keyed by (the four breakdowns and the status): the database sends
            // a row per distinct combination, not one per item, and the counts are rolled up here.
            var buckets = (
                from i in db.Items.AsNoTracking()
                join r in db.Releases on i.ReleaseId equals r.Id
                join w in db.Works on r.WorkId equals w.Id
                join p in db.Platforms on r.PlatformId equals p.Id into platforms
                from p in platforms.DefaultIfEmpty()
                where i.LibraryId == query.LibraryId && i.DeletedAt == null
                group i by new
                {
                    w.Kind,
                    r.Format,
                    Platform = p == null ? null : p.Name,
                    Decade = (r.ReleaseDate != null ? r.ReleaseDate!.Value.Year : w.OriginalYear) / 10 * 10,
                    i.Status,
                }
                into bucket
                select new { bucket.Key.Kind, bucket.Key.Format, bucket.Key.Platform, bucket.Key.Decade, bucket.Key.Status, Count = bucket.Count() })
                .AsAsyncEnumerable();

            var total = 0;
            var wishlist = 0;
            var sold = 0;
            var lent = 0;
            var missing = 0;
            var byKind = Counts();
            var byFormat = Counts();
            var byPlatform = Counts();
            var byDecade = Counts();
            await foreach (var bucket in buckets.WithCancellation(cancellationToken))
            {
                // Beside the collection: counted on their own, in none of the totals or breakdowns.
                if (!ItemStatuses.IsInCollection(bucket.Status))
                {
                    if (bucket.Status == ItemStatus.Wishlist)
                    {
                        wishlist += bucket.Count;
                    }
                    else if (bucket.Status == ItemStatus.Sold)
                    {
                        sold += bucket.Count;
                    }

                    continue;
                }

                if (bucket.Status == ItemStatus.Lent)
                {
                    lent += bucket.Count;
                }
                else if (bucket.Status == ItemStatus.Missing)
                {
                    missing += bucket.Count;
                }

                total += bucket.Count;
                Add(byKind, EnumText.ToText(bucket.Kind), bucket.Count);
                Add(byFormat, EnumText.ToText(bucket.Format), bucket.Count);
                if (bucket.Platform is not null)
                {
                    Add(byPlatform, bucket.Platform, bucket.Count);
                }

                if (bucket.Decade is { } decade)
                {
                    Add(byDecade, $"{decade}s", bucket.Count);
                }
            }

            return new StatsDto(total, byKind, byFormat, byPlatform, byDecade, wishlist, sold, lent, missing);
        }

        /// <summary>Counts keyed by text and listed in ordinal order, whatever the database collation.</summary>
        private static SortedDictionary<string, int> Counts() => new(StringComparer.Ordinal);

        private static void Add(SortedDictionary<string, int> counts, string key, int count) =>
            counts[key] = counts.GetValueOrDefault(key) + count;
    }
}
