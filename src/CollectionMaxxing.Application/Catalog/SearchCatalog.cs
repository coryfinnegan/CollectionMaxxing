using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CollectionMaxxing.Application.Catalog;

/// <summary>
/// Use case: search by title. The local catalogue first (trigram similarity on the normalised title), then every registered
/// provider; merged, scored with the match scorer and ranked. The local part only ever sees global releases and the
/// caller's own libraries' releases (the release query filter), and a work is reached only through such a release, so
/// another library's hand-typed title never comes back.
/// </summary>
public static class SearchCatalog
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 50;

    private const int LocalCandidateCap = 200;

    public const string LibraryScope = "library";

    public sealed record Query(string? Q, string? Kind, string? Platform, int? Year, int? Limit, string? Scope = null);

    public sealed class Handler(
        IAppDbContext db, ILocalCatalogSearch local, IEnumerable<ICatalogProvider> providers, CatalogOptions options, ILogger<Handler> logger)
    {
        public async Task<Result<Page<CatalogSearchResultDto>>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(query.Q))
            {
                return CatalogErrors.Invalid("catalog.query_required", "Type a title to search for.", "q");
            }

            if (query.Year is { } year && (year < 1 || year > 3000))
            {
                return CatalogErrors.Invalid("catalog.year_invalid", "That year doesn't look right.", "year");
            }

            WorkKind? kind = null;
            if (!string.IsNullOrWhiteSpace(query.Kind))
            {
                if (!EnumText.All<WorkKind>().Contains(query.Kind))
                {
                    return CatalogErrors.Invalid("catalog.kind_invalid", "That isn't a kind of item.", "kind");
                }

                kind = EnumText.Parse<WorkKind>(query.Kind);
            }

            if (!string.IsNullOrWhiteSpace(query.Scope) && query.Scope != LibraryScope)
            {
                return CatalogErrors.Invalid("catalog.scope_invalid", "That isn't a search scope.", "scope");
            }

            var libraryFirst = query.Scope == LibraryScope;

            var limit = Math.Clamp(query.Limit ?? DefaultLimit, 1, MaxLimit);
            var title = query.Q.Trim();

            var local = await SearchLocalAsync(title, query, kind, cancellationToken);
            var results = local.ToList();

            // Providers add what the catalogue doesn't have yet. Skip them when the catalogue alone fills the page.
            if (results.Count < limit)
            {
                var known = local.SelectMany(r => r.Releases.Select(rel => CatalogProjection.DuplicateKey(
                    r.Work.Title, rel.Year, rel.Platform?.Name))).ToHashSet(StringComparer.Ordinal);
                results.AddRange(await SearchProvidersAsync(title, query, kind, limit, known, cancellationToken));
            }

            var ordered = results.OrderByDescending(r => r.Score).ThenBy(r => r.Work.Title, StringComparer.OrdinalIgnoreCase).ToList();
            var mine = await MyReleaseIdsAsync(
                ordered.SelectMany(r => r.Releases).Select(r => r.Id).Where(id => id != CatalogProjection.UnsavedId).ToList(), cancellationToken);
            ordered = ordered
                .Select(r => r with { OwnedReleaseIds = r.Releases.Select(rel => rel.Id).Where(mine.Contains).ToList() })
                .ToList();
            if (libraryFirst)
            {
                ordered = ordered.OrderByDescending(r => r.Releases.Any(rel => mine.Contains(rel.Id))).ToList(); // stable: score order kept inside each half
            }

            return new Page<CatalogSearchResultDto>(ordered.Take(limit).ToList());
        }

        /// <summary>
        /// The releases the caller's libraries own (an existing Owned or Lent copy) or made for themselves. The table filters
        /// already hide every other library's items and private releases, so what matches is the caller's by construction.
        /// </summary>
        private async Task<HashSet<Guid>> MyReleaseIdsAsync(List<Guid> releaseIds, CancellationToken cancellationToken)
        {
            if (releaseIds.Count == 0)
            {
                return [];
            }

            var owned = await db.Items.AsNoTracking()
                .Where(i => i.DeletedAt == null && Ownership.OwnershipQuery.OwnedStatuses.Contains(i.Status) && releaseIds.Contains(i.ReleaseId))
                .Select(i => i.ReleaseId)
                .ToListAsync(cancellationToken);
            var local = await db.Releases.AsNoTracking()
                .Where(r => releaseIds.Contains(r.Id) && r.OwnerLibraryId != null)
                .Select(r => r.Id)
                .ToListAsync(cancellationToken);
            return owned.Concat(local).ToHashSet();
        }

        private async Task<List<CatalogSearchResultDto>> SearchLocalAsync(string title, Query query, WorkKind? kind, CancellationToken cancellationToken)
        {
            var normalized = TitleNormalizer.Normalize(title);
            if (normalized.Length == 0)
            {
                return [];
            }

            IReadOnlyCollection<Guid>? platformIds = null;
            if (!string.IsNullOrWhiteSpace(query.Platform))
            {
                platformIds = await PlatformIdsAsync(query.Platform, cancellationToken);
            }

            var rows = await local.FindAsync(
                new LocalSearch(normalized, kind, platformIds, query.Year, LocalCandidateCap), cancellationToken);
            if (rows.Count == 0)
            {
                return [];
            }

            var releaseIds = rows.Select(r => r.Release.Id).ToList();
            var platformsById = await LoadPlatformsAsync(rows.Select(r => r.Release.PlatformId), cancellationToken);
            var identifiers = (await db.ReleaseIdentifiers.AsNoTracking()
                    .Where(i => releaseIds.Contains(i.ReleaseId))
                    .OrderBy(i => i.Type).ThenBy(i => i.Value)
                    .ToListAsync(cancellationToken))
                .ToLookup(i => i.ReleaseId);

            var read = new ReadItem { Title = title, Kind = kind, Platform = query.Platform, Year = query.Year };
            var results = new List<CatalogSearchResultDto>();
            foreach (var group in rows.GroupBy(r => r.Work.Id))
            {
                var work = group.First().Work;
                var scored = new List<(ReleaseDto Dto, double Score)>();
                foreach (var row in group)
                {
                    var platform = row.Release.PlatformId is { } platformId && platformsById.TryGetValue(platformId, out var found) ? found : null;
                    var loaded = new LoadedRelease(
                        row.Release, work, platform,
                        identifiers[row.Release.Id].Select(i => new ReleaseIdentifierDto(i.Type, i.Value)).ToList());
                    var dto = ItemProjection.ToReleaseDto(loaded);
                    var candidate = new CandidateRelease
                    {
                        Key = row.Release.Id.ToString(),
                        Title = work.Title,
                        Kind = work.Kind,
                        Format = row.Release.Format,
                        Platform = platform?.Name,
                        Year = dto.Year,
                        Creators = dto.Creators,
                        Edition = row.Release.Edition,
                        Identifiers = identifiers[row.Release.Id]
                            .Select(i => Domain.Identifiers.Identifier.TryCreate(i.Type, i.Value, out var id) ? id : null)
                            .OfType<Domain.Identifiers.Identifier>().ToList(),
                    };
                    scored.Add((dto, MatchScorer.Score(read, candidate, CatalogProjection.Profile)));
                }

                var ordered = scored.OrderByDescending(s => s.Score).ToList();
                results.Add(new CatalogSearchResultDto(
                    ItemProjection.ToWorkDto(work), ordered.Select(s => s.Dto).ToList(), ordered[0].Score, CatalogProjection.LocalSource, []));
            }

            return results;
        }

        private async Task<List<Guid>> PlatformIdsAsync(string name, CancellationToken cancellationToken)
        {
            var canonical = PlatformNames.Canonicalize(name);
            var all = await db.Platforms.AsNoTracking().ToListAsync(cancellationToken);
            return all
                .Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(p.ShortName, name, StringComparison.OrdinalIgnoreCase)
                            || (canonical is not null && PlatformNames.Canonicalize(p.Name) == canonical))
                .Select(p => p.Id)
                .ToList();
        }

        private async Task<Dictionary<Guid, Platform>> LoadPlatformsAsync(IEnumerable<Guid?> ids, CancellationToken cancellationToken)
        {
            var wanted = ids.OfType<Guid>().Distinct().ToList();
            return wanted.Count == 0
                ? []
                : await db.Platforms.AsNoTracking().Where(p => wanted.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
        }

        private async Task<List<CatalogSearchResultDto>> SearchProvidersAsync(
            string title, Query query, WorkKind? kind, int limit, HashSet<string> known, CancellationToken cancellationToken)
        {
            var asked = providers.Where(p => kind is null || p.Supports(kind.Value)).ToList();
            if (asked.Count == 0)
            {
                return [];
            }

            var catalogQuery = new CatalogQuery { Title = title, Kind = kind, Platform = query.Platform, Year = query.Year, Limit = limit };
            using var failFast = ProviderCalls.FailFast(); // a person is waiting
            var answers = await Task.WhenAll(asked.Select(provider => CatalogProjection.AskProviderAsync(
                provider, token => provider.SearchAsync(catalogQuery, token), options.ProviderTimeout, logger, cancellationToken)));

            var read = new ReadItem { Title = title, Kind = kind, Platform = query.Platform, Year = query.Year };
            var scored = new List<(ProviderRelease Release, double Score)>();
            foreach (var found in answers.Where(a => a is not null).SelectMany(a => a!))
            {
                if (known.Contains(CatalogProjection.DuplicateKey(found.Title, found.Year, found.Platform)))
                {
                    continue; // the catalogue already has it
                }

                scored.Add((found, MatchScorer.Score(read, found.ToCandidate(), CatalogProjection.Profile)));
            }

            return CatalogProjection.GroupProviderReleases(scored)
                .Select(g =>
                {
                    var face = g.Releases[0];
                    var work = CatalogProjection.ToWorkDto(face.Release);
                    var cover = face.Release.CoverUrl ?? g.Releases.Select(r => r.Release.CoverUrl).FirstOrDefault(c => c is not null);
                    return new CatalogSearchResultDto(
                        cover == work.CoverUrl ? work : work with { CoverUrl = cover },
                        g.Releases.Select(r => CatalogProjection.ToReleaseDto(r.Release)).ToList(),
                        face.Score,
                        face.Release.Provider,
                        []);
                })
                .ToList();
        }
    }
}
