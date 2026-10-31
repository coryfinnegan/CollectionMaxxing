using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Infrastructure.Catalog;

/// <summary>Trigram similarity on <c>works.normalized_title</c> (the GIN index), through the caller-filtered release set.</summary>
internal sealed class LocalCatalogSearch(AppDbContext db) : ILocalCatalogSearch
{
    private const double MinimumSimilarity = 0.3;

    /// <summary>
    /// The most searches one query answers. Each is its own ranked, capped branch of a union, so the statement grows with the count;
    /// a fixed size keeps the statement shapes few (EF compiles each shape once) and the statements small.
    /// </summary>
    private const int SearchesPerQuery = 10;

    public async Task<IReadOnlyList<LocalHit>> FindAsync(LocalSearch search, CancellationToken cancellationToken)
    {
        var title = search.NormalizedTitle;
        var (releases, works) = Candidates(search);

        var rows = await (from r in releases
                          join w in works on r.WorkId equals w.Id
                          orderby EF.Functions.TrigramsSimilarity(w.NormalizedTitle, title) descending, w.Title, r.Id
                          select new { Release = r, Work = w })
            .Take(search.Cap)
            .ToListAsync(cancellationToken);
        return rows.Select(r => new LocalHit(r.Release, r.Work)).ToList();
    }

    public async Task<IReadOnlyList<IReadOnlyList<LocalHit>>> FindManyAsync(IReadOnlyList<LocalSearch> searches, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(searches);
        if (searches.Count == 0)
        {
            return [];
        }

        // Which releases each search finds, in its order: one query per few searches, each search a ranked and capped branch of a union.
        var matches = new List<Match>();
        for (var start = 0; start < searches.Count; start += SearchesPerQuery)
        {
            IQueryable<Match>? union = null;
            for (var index = start; index < Math.Min(start + SearchesPerQuery, searches.Count); index++)
            {
                var branch = Ranked(searches[index], index);
                union = union is null ? branch : union.Concat(branch);
            }

            matches.AddRange(await union!
                .OrderBy(m => m.Search).ThenByDescending(m => m.Similarity).ThenBy(m => m.Title).ThenBy(m => m.ReleaseId)
                .ToListAsync(cancellationToken));
        }

        // The rows themselves, once for all the searches. A release found by two searches is one row.
        var releaseIds = matches.Select(m => m.ReleaseId).Distinct().ToList();
        var workIds = matches.Select(m => m.WorkId).Distinct().ToList();
        var releases = releaseIds.Count == 0
            ? []
            : await db.Releases.AsNoTracking().Where(r => releaseIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, cancellationToken);
        var works = workIds.Count == 0
            ? []
            : await db.Works.AsNoTracking().Where(w => workIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, cancellationToken);

        var found = new List<LocalHit>[searches.Count];
        for (var i = 0; i < found.Length; i++)
        {
            found[i] = [];
        }

        foreach (var match in matches)
        {
            if (releases.TryGetValue(match.ReleaseId, out var release) && works.TryGetValue(match.WorkId, out var work))
            {
                found[match.Search].Add(new LocalHit(release, work));
            }
        }

        return found;
    }

    /// <summary>The releases the caller may see, and the works whose title is close to the search's: the rows a search ranks.</summary>
    private (IQueryable<Release> Releases, IQueryable<Work> Works) Candidates(LocalSearch search)
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

        return (releases, works);
    }

    /// <summary>One search's best releases, best first, as ids and the keys they were ranked by (so a union of searches can be put back in order).</summary>
    private IQueryable<Match> Ranked(LocalSearch search, int index)
    {
        var title = search.NormalizedTitle;
        var (releases, works) = Candidates(search);
        return (from r in releases
                join w in works on r.WorkId equals w.Id
                orderby EF.Functions.TrigramsSimilarity(w.NormalizedTitle, title) descending, w.Title, r.Id
                select new Match
                {
                    Search = index,
                    ReleaseId = r.Id,
                    WorkId = w.Id,
                    Similarity = EF.Functions.TrigramsSimilarity(w.NormalizedTitle, title),
                    Title = w.Title,
                })
            .Take(search.Cap);
    }

    private sealed class Match
    {
        public int Search { get; init; }

        public Guid ReleaseId { get; init; }

        public Guid WorkId { get; init; }

        public double Similarity { get; init; }

        public string Title { get; init; } = string.Empty;
    }
}
