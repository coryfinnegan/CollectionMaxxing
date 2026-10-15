using System.Text.Json;
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
/// It reuses what is there: a release with the same barcode, else one of the same work in the same format and platform, else
/// a new release on the same work, else a new work. Nothing is saved until the caller saves. One instance serves one
/// request, and remembers what it added so two candidates for the same game share one release.
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
                return new EnsuredRelease(found, false);
            }
        }

        var normalized = TitleNormalizer.Normalize(provider.Title);
        var format = provider.Format ?? ReleaseFormat.Other;
        var platformId = await PlatformIdAsync(provider.Platform, cancellationToken);
        var key = $"{normalized}|{provider.Kind}|{format}|{platformId}|{provider.Year}";
        if (_added.TryGetValue(key, out var already))
        {
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
                               select (Guid?)r.Id).FirstOrDefaultAsync(cancellationToken);
        if (sameShape is { } release)
        {
            return _added[key] = new EnsuredRelease(release, false);
        }

        // Only a work that has a global release is shared: a library's hand-typed work is not reused.
        var workId = await (from w in db.Works.AsNoTracking()
                            join r in db.Releases.AsNoTracking() on w.Id equals r.WorkId
                            where r.OwnerLibraryId == null
                                && w.NormalizedTitle == normalized
                                && w.Kind == provider.Kind
                                && w.OriginalYear == provider.Year
                            orderby w.CreatedAt, w.Id
                            select (Guid?)w.Id).FirstOrDefaultAsync(cancellationToken);

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
