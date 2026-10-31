using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Scanning.Learning;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Domain.Items;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Ownership;

/// <summary>
/// The one definition of "do I own this?", shared by the ownership endpoint and the scanner. A copy counts when it isn't
/// deleted and its status is Owned or Lent. Only the given library's items are ever read, and releases made by other
/// libraries are invisible, so nothing leaks across libraries.
/// </summary>
public sealed class OwnershipQuery(IAppDbContext db)
{
    public static readonly ItemStatus[] OwnedStatuses = [ItemStatus.Owned, ItemStatus.Lent];

    public static readonly OwnershipDto None = new(false, 0, [], []);

    /// <summary>
    /// Copies of the releases that carry this barcode. 13-digit codes may be stored as ISBN or GTIN; the digits identify the product.
    /// A barcode another library taught by hand does not count here.
    /// </summary>
    public async Task<OwnershipDto> ForCodeAsync(Guid libraryId, Identifier identifier, CancellationToken cancellationToken)
    {
        var releaseIds = await (from i in db.ReleaseIdentifiers.AsNoTracking().UsableBy(libraryId)
                                join r in db.Releases.AsNoTracking() on i.ReleaseId equals r.Id
                                where i.Value == identifier.Value
                                select r.Id).Distinct().ToListAsync(cancellationToken);
        return await ForReleasesAsync(libraryId, releaseIds, cancellationToken);
    }

    public Task<OwnershipDto> ForReleaseAsync(Guid libraryId, Guid releaseId, CancellationToken cancellationToken) =>
        ForReleasesAsync(libraryId, [releaseId], cancellationToken);

    public async Task<OwnershipDto> ForReleasesAsync(Guid libraryId, IReadOnlyCollection<Guid> releaseIds, CancellationToken cancellationToken)
    {
        // The release filter hides other libraries' private releases.
        var asked = await db.Releases.AsNoTracking()
            .Where(r => releaseIds.Contains(r.Id))
            .Select(r => new { r.Id, r.WorkId, r.Format, r.PlatformId })
            .ToListAsync(cancellationToken);
        if (asked.Count == 0)
        {
            return None;
        }

        var workIds = asked.Select(a => a.WorkId).Distinct().ToList();
        var shapes = asked.Select(a => (a.Format, a.PlatformId)).ToHashSet();
        var askedIds = asked.Select(a => a.Id).ToHashSet();

        var rows = await CopiesOfWorksAsync(libraryId, workIds, cancellationToken);
        var exact = rows.Where(r => askedIds.Contains(r.Copy.ReleaseId)).Select(r => r.Copy).ToList();
        var soft = rows.Where(r => !askedIds.Contains(r.Copy.ReleaseId) && !shapes.Contains((r.Copy.Format, r.PlatformId)))
            .Select(r => r.Copy).ToList();
        return new OwnershipDto(exact.Count > 0, exact.Count, exact, soft);
    }

    public async Task<OwnershipDto> ForWorkAsync(Guid libraryId, Guid workId, CancellationToken cancellationToken)
    {
        var rows = await CopiesOfWorksAsync(libraryId, [workId], cancellationToken);
        var copies = rows.Select(r => r.Copy).ToList();
        return new OwnershipDto(copies.Count > 0, copies.Count, copies, []);
    }

    /// <summary>Location names from the root down, joined with " › ", for every location of the library.</summary>
    public async Task<Dictionary<Guid, string>> LocationPathsAsync(Guid libraryId, CancellationToken cancellationToken)
    {
        var locations = await db.Locations.AsNoTracking()
            .Where(l => l.LibraryId == libraryId)
            .Select(l => new { l.Id, l.ParentId, l.Name })
            .ToDictionaryAsync(l => l.Id, cancellationToken);

        var paths = new Dictionary<Guid, string>();
        foreach (var id in locations.Keys)
        {
            var names = new List<string>();
            Guid? current = id;
            while (current is { } step && locations.TryGetValue(step, out var location) && names.Count <= locations.Count)
            {
                names.Add(location.Name);
                current = location.ParentId;
            }

            names.Reverse();
            paths[id] = string.Join(" › ", names);
        }

        return paths;
    }

    private sealed record CopyRow(OwnedCopyDto Copy, Guid? PlatformId);

    private async Task<List<CopyRow>> CopiesOfWorksAsync(Guid libraryId, IReadOnlyCollection<Guid> workIds, CancellationToken cancellationToken)
    {
        var rows = await (
            from item in db.Items.AsNoTracking()
            join release in db.Releases.AsNoTracking() on item.ReleaseId equals release.Id
            join work in db.Works.AsNoTracking() on release.WorkId equals work.Id
            join platform in db.Platforms.AsNoTracking() on release.PlatformId equals platform.Id into platforms
            from platform in platforms.DefaultIfEmpty()
            where item.LibraryId == libraryId
                && item.DeletedAt == null
                && OwnedStatuses.Contains(item.Status)
                && workIds.Contains(release.WorkId)
            orderby item.CreatedAt, item.Id
            select new
            {
                item.Id,
                ReleaseId = release.Id,
                release.WorkId,
                work.Title,
                release.Format,
                release.PlatformId,
                Platform = platform == null ? null : platform.Name,
                item.Status,
                item.LocationId,
            }).ToListAsync(cancellationToken);

        var paths = rows.Any(r => r.LocationId is not null)
            ? await LocationPathsAsync(libraryId, cancellationToken)
            : [];

        return rows.Select(r => new CopyRow(
            new OwnedCopyDto(
                r.Id, r.ReleaseId, r.WorkId, TitleCaser.ToTitleCase(r.Title), r.Format, r.Platform, r.Status, r.LocationId,
                r.LocationId is { } loc && paths.TryGetValue(loc, out var path) ? path : null),
            r.PlatformId)).ToList();
    }
}
