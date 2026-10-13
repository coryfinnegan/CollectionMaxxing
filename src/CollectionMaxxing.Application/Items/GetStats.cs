using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Items;

/// <summary>Use case: counts of a library's items by kind, format, platform and decade. Deleted items don't count.</summary>
public static class GetStats
{
    public sealed record Query(Guid LibraryId);

    public sealed class Handler(IAppDbContext db)
    {
        public async Task<StatsDto> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            var rows = await (
                from i in db.Items.AsNoTracking()
                join r in db.Releases on i.ReleaseId equals r.Id
                join w in db.Works on r.WorkId equals w.Id
                join p in db.Platforms on r.PlatformId equals p.Id into platforms
                from p in platforms.DefaultIfEmpty()
                where i.LibraryId == query.LibraryId && i.DeletedAt == null
                select new
                {
                    w.Kind,
                    r.Format,
                    Platform = p == null ? null : p.Name,
                    r.ReleaseDate,
                    w.OriginalYear,
                })
                .ToListAsync(cancellationToken);

            return new StatsDto(
                rows.Count,
                Count(rows.Select(r => EnumText.ToText(r.Kind))),
                Count(rows.Select(r => EnumText.ToText(r.Format))),
                Count(rows.Select(r => r.Platform)),
                Count(rows.Select(r => (r.ReleaseDate?.Year ?? r.OriginalYear) is { } year ? $"{year / 10 * 10}s" : null)));
        }

        private static Dictionary<string, int> Count(IEnumerable<string?> keys) =>
            keys.Where(k => k is not null).GroupBy(k => k!).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
    }
}
