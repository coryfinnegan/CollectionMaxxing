using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Infrastructure.Catalog;

/// <summary>Trigram similarity on <c>works.normalized_title</c> (the GIN index), through the caller-filtered release set.</summary>
internal sealed class LocalCatalogSearch(AppDbContext db) : ILocalCatalogSearch
{
    private const double MinimumSimilarity = 0.3;

    public async Task<IReadOnlyList<LocalHit>> FindAsync(LocalSearch search, CancellationToken cancellationToken)
    {
        var title = search.NormalizedTitle;
        var releases = db.Releases.AsNoTracking(); // the query filter hides other libraries' releases
        var works = db.Works.AsNoTracking()
            .Where(w => EF.Functions.TrigramsSimilarity(w.NormalizedTitle, title) >= MinimumSimilarity || w.NormalizedTitle.Contains(title));

        if (search.Kind is { } kind)
        {
            works = works.Where(w => w.Kind == kind);
        }

        if (search.Year is { } year)
        {
            works = works.Where(w => w.OriginalYear == year);
        }

        if (search.PlatformIds is { } platformIds)
        {
            var ids = platformIds.ToList();
            releases = releases.Where(r => r.PlatformId != null && ids.Contains(r.PlatformId.Value));
        }

        var rows = await (from r in releases
                          join w in works on r.WorkId equals w.Id
                          orderby EF.Functions.TrigramsSimilarity(w.NormalizedTitle, title) descending, w.Title, r.Id
                          select new { Release = r, Work = w })
            .Take(search.Cap)
            .ToListAsync(cancellationToken);
        return rows.Select(r => new LocalHit(r.Release, r.Work)).ToList();
    }
}
