using System.Text.Json;
using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Libraries.Members;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Items;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Items;

/// <summary>Shared reading and mapping for the item use cases.</summary>
internal static class ItemProjection
{
    public static string? CoverUrl(Guid? assetId) => assetId is { } id ? $"/media/covers/{id}" : null;

    public static IReadOnlyList<string> Creators(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var names = new List<string>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                var name = element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString(),
                    JsonValueKind.Object when element.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String => n.GetString(),
                    _ => null,
                };
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }

            return names;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static ItemDto ToDto(Item item, IReadOnlyList<Guid> tagIds) => new(
        item.Id, item.ReleaseId, item.Status, item.Condition, item.Completeness, item.LocationId, item.Notes, item.AcquiredOn,
        item.LentTo, item.LentOn, item.CustomTitle, item.CustomCoverAssetId, item.CoverChoice, item.AddedVia, tagIds,
        item.CreatedAt, item.CreatedBy, item.UpdatedAt, item.DeletedAt, item.EditionVerified);

    /// <summary>The item as its DTO, with the display name of whoever added it ("Added by").</summary>
    public static async Task<ItemDto> ToDtoAsync(Item item, IReadOnlyList<Guid> tagIds, IUserDirectory? users, CancellationToken cancellationToken)
    {
        var dto = ToDto(item, tagIds);
        if (users is null)
        {
            return dto;
        }

        var names = await users.DisplayNamesAsync([item.CreatedBy], cancellationToken);
        return dto with { CreatedByName = names.GetValueOrDefault(item.CreatedBy) };
    }

    public static async Task<List<Guid>> TagIdsAsync(IAppDbContext db, Guid libraryId, Guid itemId, CancellationToken cancellationToken) =>
        await db.ItemTags.AsNoTracking()
            .Where(t => t.LibraryId == libraryId && t.ItemId == itemId)
            .Select(t => t.TagId)
            .OrderBy(id => id)
            .ToListAsync(cancellationToken);

    /// <summary>A release this library may attach: global, or made by this library. Anything else looks like it doesn't exist.</summary>
    public static async Task<bool> CanUseReleaseAsync(IAppDbContext db, Guid libraryId, Guid releaseId, CancellationToken cancellationToken) =>
        await db.Releases.AnyAsync(
            r => r.Id == releaseId && (r.OwnerLibraryId == null || r.OwnerLibraryId == libraryId), cancellationToken);

    public static async Task<bool> LocationInLibraryAsync(IAppDbContext db, Guid libraryId, Guid locationId, CancellationToken cancellationToken) =>
        await db.Locations.AnyAsync(l => l.LibraryId == libraryId && l.Id == locationId, cancellationToken);

    public static async Task<bool> TagsInLibraryAsync(
        IAppDbContext db, Guid libraryId, IReadOnlyCollection<Guid> tagIds, CancellationToken cancellationToken)
    {
        if (tagIds.Count == 0)
        {
            return true;
        }

        var found = await db.Tags.CountAsync(t => t.LibraryId == libraryId && tagIds.Contains(t.Id), cancellationToken);
        return found == tagIds.Count;
    }

    public static async Task<LoadedRelease?> LoadReleaseAsync(IAppDbContext db, Guid releaseId, CancellationToken cancellationToken) =>
        (await LoadReleasesAsync(db, [releaseId], cancellationToken)).SingleOrDefault();

    /// <summary>
    /// A release with its work, through the change tracker first, so a release (or work) this same unit of work just
    /// ensured is found before it is saved. Null when the release or its work is unknown.
    /// </summary>
    public static async Task<ReleaseWithWork?> LoadReleaseWithWorkAsync(
        IAppDbContext db, Guid releaseId, CancellationToken cancellationToken)
    {
        if (await db.Releases.FindAsync([releaseId], cancellationToken) is not { } release)
        {
            return null;
        }

        if (await db.Works.FindAsync([release.WorkId], cancellationToken) is not { } work)
        {
            return null;
        }

        return new ReleaseWithWork(release, work);
    }

    /// <summary>Loads releases with their works, platforms and identifiers as a set (one query per table), in id order. Unknown ids are skipped.</summary>
    public static async Task<IReadOnlyList<LoadedRelease>> LoadReleasesAsync(
        IAppDbContext db, IEnumerable<Guid> releaseIds, CancellationToken cancellationToken)
    {
        var ordered = releaseIds.Order().ToList();
        if (ordered.Count == 0)
        {
            return [];
        }

        var releases = await db.Releases.AsNoTracking().Where(r => ordered.Contains(r.Id)).ToListAsync(cancellationToken);
        var workIds = releases.Select(r => r.WorkId).Distinct().ToList();
        var works = await db.Works.AsNoTracking().Where(w => workIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, cancellationToken);
        var platformIds = releases.Select(r => r.PlatformId).OfType<Guid>().Distinct().ToList();
        var platforms = platformIds.Count == 0
            ? new Dictionary<Guid, Platform>()
            : await db.Platforms.AsNoTracking().Where(p => platformIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
        var identifiers = await db.ReleaseIdentifiers.AsNoTracking()
            .Where(i => ordered.Contains(i.ReleaseId))
            .OrderBy(i => i.Type).ThenBy(i => i.Value)
            .Select(i => new { i.ReleaseId, Dto = new ReleaseIdentifierDto(i.Type, i.Value) })
            .ToListAsync(cancellationToken);
        var byRelease = identifiers.GroupBy(i => i.ReleaseId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ReleaseIdentifierDto>)g.Select(i => i.Dto).ToList());

        var byId = releases.ToDictionary(r => r.Id);
        var loaded = new List<LoadedRelease>(ordered.Count);
        foreach (var id in ordered)
        {
            if (byId.TryGetValue(id, out var release))
            {
                loaded.Add(new LoadedRelease(
                    release,
                    works[release.WorkId],
                    release.PlatformId is { } platformId ? platforms.GetValueOrDefault(platformId) : null,
                    byRelease.GetValueOrDefault(id) ?? []));
            }
        }

        return loaded;
    }

    public static WorkDto ToWorkDto(Work work) => new(
        work.Id, work.Kind, TitleCaser.ToTitleCase(work.Title), work.OriginalYear, Creators(work.CreatorsJson), work.Genres, work.Synopsis, CoverUrl(work.CoverAssetId));

    public static ReleaseDto ToReleaseDto(LoadedRelease loaded) => new(
        loaded.Release.Id,
        loaded.Work.Id,
        TitleCaser.ToTitleCase(loaded.Work.Title),
        loaded.Release.Format,
        loaded.Platform is null
            ? null
            : new PlatformDto(loaded.Platform.Id, loaded.Platform.Name, loaded.Platform.ShortName, loaded.Platform.Manufacturer),
        loaded.Release.Region,
        loaded.Release.Edition,
        loaded.Release.Publisher,
        loaded.Release.ReleaseDate,
        loaded.Release.ReleaseDate?.Year ?? loaded.Work.OriginalYear,
        Creators(loaded.Work.CreatorsJson),
        loaded.Release.PageCount,
        loaded.Release.RuntimeMinutes,
        CoverUrl(loaded.Release.CoverAssetId ?? loaded.Work.CoverAssetId),
        loaded.Release.Source,
        loaded.Identifiers);
}

internal sealed record LoadedRelease(Release Release, Work Work, Platform? Platform, IReadOnlyList<ReleaseIdentifierDto> Identifiers);

internal sealed record ReleaseWithWork(Release Release, Work Work);
