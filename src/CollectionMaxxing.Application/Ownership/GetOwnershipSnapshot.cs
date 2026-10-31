using System.Globalization;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Scanning.Learning;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Items;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Ownership;

/// <summary>
/// Use case: a delta of the library's items for the phone's offline cache. Changes are ordered by "updated at, then id", and the
/// cursor is the last position handed out, so a write that lands while the phone is paging gets a later position and is
/// picked up by the next call rather than skipped or repeated. With no cursor the answer is every live item now;
/// with one, every item changed since, including tombstones for items deleted (kept 30 days). Each live entry carries its
/// status, so the cache keeps the collection (owned, lent, missing) apart from the wishlist and the sold items and answers
/// "you own this", "on your wishlist" and "you sold this" offline; an item that moves between statuses is a changed entry, not a
/// tombstone. An item removed for good (an undone scan commit) has no row left, so its <see cref="ItemTombstone"/> stands in:
/// same order, same cursor.
/// </summary>
public static class GetOwnershipSnapshot
{
    public const int DefaultLimit = 200;
    public const int MaxLimit = 500;

    public sealed record Query(Guid LibraryId, string? Since, int? Limit);

    public sealed class Handler(IAppDbContext db, OwnershipQuery ownership)
    {
        public async Task<Result<OwnershipSnapshotDto>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            var limit = query.Limit is null or < 1 ? DefaultLimit : Math.Min(query.Limit.Value, MaxLimit);

            long? sinceTicks = null;
            var sinceId = Guid.Empty;
            if (!string.IsNullOrEmpty(query.Since))
            {
                if (!TryDecode(query.Since, out var ticks, out sinceId))
                {
                    return new DomainError(ErrorKind.Validation, "ownership.cursor_invalid", "That cursor isn't valid.", "since");
                }

                sinceTicks = ticks;
            }

            var changed = db.Items.AsNoTracking().Where(i => i.LibraryId == query.LibraryId);
            if (sinceTicks is { } t)
            {
                var at = new DateTimeOffset(t, TimeSpan.Zero);
                var id = sinceId;
                changed = changed.Where(i => i.UpdatedAt > at || (i.UpdatedAt == at && i.Id.CompareTo(id) > 0));
            }
            else
            {
                changed = changed.Where(i => i.DeletedAt == null);
            }

            var rows = await changed
                .OrderBy(i => i.UpdatedAt).ThenBy(i => i.Id)
                .Take(limit + 1)
                .Select(i => new Row(i.Id, i.ReleaseId, i.Status, i.DeletedAt, i.LocationId, i.UpdatedAt))
                .ToListAsync(cancellationToken);

            if (sinceTicks is { } since)
            {
                var at = new DateTimeOffset(since, TimeSpan.Zero);
                var id = sinceId;
                var removed = await db.ItemTombstones.AsNoTracking()
                    .Where(t => t.LibraryId == query.LibraryId && (t.DeletedAt > at || (t.DeletedAt == at && t.ItemId.CompareTo(id) > 0)))
                    .OrderBy(t => t.DeletedAt).ThenBy(t => t.ItemId)
                    .Take(limit + 1)
                    .Select(t => new Row(t.ItemId, Guid.Empty, Domain.Items.ItemStatus.Owned, t.DeletedAt, null, t.DeletedAt))
                    .ToListAsync(cancellationToken);
                rows = rows.Concat(removed).OrderBy(r => r.UpdatedAt).ThenBy(r => r.Id).Take(limit + 1).ToList();
            }

            var hasMore = rows.Count > limit;
            if (hasMore)
            {
                rows.RemoveAt(rows.Count - 1);
            }

            var releaseIds = rows.Where(r => r.IsLive).Select(r => r.ReleaseId).Distinct().ToList();

            var releases = await (
                from release in db.Releases.AsNoTracking()
                join work in db.Works.AsNoTracking() on release.WorkId equals work.Id
                join platform in db.Platforms.AsNoTracking() on release.PlatformId equals platform.Id into platforms
                from platform in platforms.DefaultIfEmpty()
                where releaseIds.Contains(release.Id)
                select new { release.Id, release.WorkId, work.Title, release.Format, Platform = platform == null ? null : platform.Name })
                .ToDictionaryAsync(r => r.Id, cancellationToken);

            // The shared barcodes and the ones this library taught, not a barcode only another library taught.
            var identifiers = (await db.ReleaseIdentifiers.AsNoTracking()
                .UsableBy(query.LibraryId)
                .Where(i => releaseIds.Contains(i.ReleaseId) && i.Type != IdentifierType.Asin)
                .Select(i => new { i.ReleaseId, i.Value })
                .ToListAsync(cancellationToken))
                .GroupBy(i => i.ReleaseId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(i => i.Value).Distinct().Order().ToList());

            var paths = rows.Any(r => r.IsLive && r.InCollection && r.LocationId is not null)
                ? await ownership.LocationPathsAsync(query.LibraryId, cancellationToken)
                : [];

            var entries = new List<OwnershipSnapshotEntryDto>(rows.Count);
            foreach (var row in rows)
            {
                if (row.IsLive && releases.TryGetValue(row.ReleaseId, out var release))
                {
                    entries.Add(new OwnershipSnapshotEntryDto(
                        row.Id, false, release.Id, release.WorkId, TitleCaser.ToTitleCase(release.Title), release.Format, release.Platform,
                        identifiers.GetValueOrDefault(release.Id) ?? [],
                        row.InCollection && row.LocationId is { } loc && paths.TryGetValue(loc, out var path) ? path : null,
                        row.Status));
                }
                else
                {
                    entries.Add(new OwnershipSnapshotEntryDto(row.Id, true, null, null, null, null, null, [], null));
                }
            }

            var next = rows.Count > 0 ? Encode(rows[^1].UpdatedAt.UtcTicks, rows[^1].Id) : query.Since ?? Encode(0, Guid.Empty);
            return new OwnershipSnapshotDto(entries, next, hasMore);
        }

        private static string Encode(long ticks, Guid id) => string.Create(CultureInfo.InvariantCulture, $"{ticks}.{id:N}");

        private static bool TryDecode(string text, out long ticks, out Guid id)
        {
            ticks = 0;
            id = Guid.Empty;
            var parts = text.Split('.');
            return parts.Length == 2
                && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out ticks)
                && ticks <= DateTimeOffset.MaxValue.UtcTicks
                && Guid.TryParseExact(parts[1], "N", out id);
        }

        private sealed record Row(Guid Id, Guid ReleaseId, Domain.Items.ItemStatus Status, DateTimeOffset? DeletedAt, Guid? LocationId, DateTimeOffset UpdatedAt)
        {
            public bool IsLive => DeletedAt is null;

            public bool InCollection => ItemStatuses.IsInCollection(Status);
        }
    }
}
