using System.Text.Json;
using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Scanning;

/// <summary>A release the scanner can point a candidate at; <see cref="IsNew"/> means it was just added (so nobody owns it yet).</summary>
public sealed record EnsuredRelease(Guid Id, bool IsNew);

/// <summary>
/// Saves what a provider told us as global catalogue rows, so a candidate (and its alternatives) can point at real releases.
/// It reuses what is there: a release with the same barcode, else one of the same work (the same title and creators)
/// in the same format and platform, else a new release on the same work, else a new work. Nothing is saved until the
/// caller saves. One instance serves one request, and remembers what it added so two candidates for the same game share
/// one release.
/// </summary>
public sealed class ReleaseStore(IAppDbContext db, IIdGenerator ids, TimeProvider clock)
{
    private readonly Dictionary<string, EnsuredRelease> _added = new(StringComparer.Ordinal);
    private List<Platform>? _platforms;

    public async Task<EnsuredRelease> EnsureAsync(ProviderRelease provider, CancellationToken cancellationToken)
    {
        // The same barcode is the same product.
        foreach (var identifier in provider.Identifiers)
        {
            var existing = await (from i in db.ReleaseIdentifiers.AsNoTracking()
                                  join r in db.Releases.AsNoTracking() on i.ReleaseId equals r.Id
                                  where i.Value == identifier.Value
                                  orderby r.CreatedAt, r.Id
                                  select (Guid?)r.Id).FirstOrDefaultAsync(cancellationToken);
            if (existing is { } found)
            {
                await FillMissingCoverAsync(found, provider, cancellationToken);
                return new EnsuredRelease(found, false);
            }
        }

        var normalized = TitleNormalizer.Normalize(provider.Title);
        var format = provider.Format ?? ReleaseFormat.Unknown;
        var platformId = await PlatformIdAsync(provider.Platform, cancellationToken);
        var creators = NormalizedCreators(provider.Creators);
        var key = $"{normalized}|{provider.Kind}|{format}|{platformId}|{provider.Year}|{string.Join(",", creators.OrderBy(c => c, StringComparer.Ordinal))}";
        if (_added.TryGetValue(key, out var already))
        {
            await FillMissingCoverAsync(already.Id, provider, cancellationToken);
            return already;
        }

        var sameShape = await (from r in db.Releases.AsNoTracking()
                               join w in db.Works.AsNoTracking() on r.WorkId equals w.Id
                               where r.OwnerLibraryId == null
                                   && w.NormalizedTitle == normalized
                                   && w.Kind == provider.Kind
                                   && w.OriginalYear == provider.Year
                                   && r.Format == format
                                   && r.PlatformId == platformId
                               orderby r.CreatedAt, r.Id
                               select new { r.Id, w.CreatorsJson }).ToListAsync(cancellationToken);
        var compatible = sameShape.FirstOrDefault(c => CreatorsCompatible(c.CreatorsJson, creators));
        if (compatible is not null)
        {
            await FillMissingCoverAsync(compatible.Id, provider, cancellationToken);
            return _added[key] = new EnsuredRelease(compatible.Id, false);
        }

        // Only a work that has a global release is shared: a library's hand-typed work is not reused.
        // A same-title work by a different author is never reused: it is a different work (#518).
        var candidates = await (from w in db.Works.AsNoTracking()
                                join r in db.Releases.AsNoTracking() on w.Id equals r.WorkId
                                where r.OwnerLibraryId == null
                                    && w.NormalizedTitle == normalized
                                    && w.Kind == provider.Kind
                                    && w.OriginalYear == provider.Year
                                orderby w.CreatedAt, w.Id
                                select new { w.Id, w.CreatorsJson }).ToListAsync(cancellationToken);
        var workId = candidates.FirstOrDefault(c => CreatorsCompatible(c.CreatorsJson, creators))?.Id;

        var now = clock.GetUtcNow();
        if (workId is null)
        {
            var title = provider.Title.Trim();
            var work = new Work
            {
                Id = ids.NewId(),
                Kind = provider.Kind,
                Title = title,
                SortTitle = CreateItem.SortTitle(title),
                NormalizedTitle = normalized,
                OriginalYear = provider.Year,
                CreatorsJson = JsonSerializer.Serialize(provider.Creators),
                Synopsis = provider.Synopsis,
                ExternalIdsJson = JsonSerializer.Serialize(new Dictionary<string, string> { [provider.Provider] = provider.ExternalId }),
                CreatedAt = now,
            };
            db.Works.Add(work);
            workId = work.Id;
        }

        var added = new Release
        {
            Id = ids.NewId(),
            WorkId = workId.Value,
            Format = format,
            PlatformId = platformId,
            Edition = provider.Edition,
            Publisher = provider.Publisher,
            Source = ReleaseSource.Provider,
            ProviderPayloadsJson = JsonSerializer.Serialize(new { provider = provider.Provider, externalId = provider.ExternalId, coverUrl = provider.CoverUrl }),
            RefreshedAt = now,
            CreatedAt = now,
        };
        db.Releases.Add(added);
        foreach (var identifier in provider.Identifiers.DistinctBy(i => (i.Type, i.Value)))
        {
            db.ReleaseIdentifiers.Add(new ReleaseIdentifier { ReleaseId = added.Id, Type = identifier.Type, Value = identifier.Value });
        }

        return _added[key] = new EnsuredRelease(added.Id, true);
    }

    /// <summary>
    /// True when a provider answer may reuse a stored work: the creators match, or one side names none (unverifiable,
    /// so today's reuse stands). A same-title answer naming a different author never reuses (#518).
    /// </summary>
    private static bool CreatorsCompatible(string storedCreatorsJson, HashSet<string> providerCreators)
    {
        var stored = NormalizedCreators(ItemProjection.Creators(storedCreatorsJson));
        return stored.Count == 0 || providerCreators.Count == 0 || stored.SetEquals(providerCreators);
    }

    /// <summary>The creators as a comparison set: normalized like titles, blanks dropped.</summary>
    private static HashSet<string> NormalizedCreators(IEnumerable<string> creators)
    {
        var normalized = new HashSet<string>(StringComparer.Ordinal);
        foreach (var creator in creators)
        {
            var key = TitleNormalizer.Normalize(creator);
            if (key.Length > 0)
            {
                normalized.Add(key);
            }
        }

        return normalized;
    }

    /// <summary>
    /// A reused release keeps its cover address when it has one; when it has none and the fresh answer names one, the
    /// address is filled in, so the commit's cover step can fetch it. A cover the release already stores is never touched.
    /// </summary>
    private async Task FillMissingCoverAsync(Guid releaseId, ProviderRelease provider, CancellationToken cancellationToken)
    {
        if (provider.CoverUrl is null)
        {
            return;
        }

        // Tracked: a release this request already added comes from the change tracker, with no query.
        var release = await db.Releases.FindAsync([releaseId], cancellationToken);
        if (release is { CoverAssetId: null } && CoverOutcome.CoverUrlOf(release.ProviderPayloadsJson) is null)
        {
            release.ProviderPayloadsJson =
                CoverOutcome.WithCover(release.ProviderPayloadsJson, provider.Provider, provider.ExternalId, provider.CoverUrl);
        }
    }

    private async Task<Guid?> PlatformIdAsync(string? name, CancellationToken cancellationToken)
    {
        var canonical = PlatformNames.Canonicalize(name);
        if (canonical is null)
        {
            return null;
        }

        _platforms ??= await db.Platforms.AsNoTracking().ToListAsync(cancellationToken);
        return _platforms
            .FirstOrDefault(p => PlatformNames.Canonicalize(p.Name) == canonical || PlatformNames.Canonicalize(p.ShortName) == canonical)?.Id;
    }
}
