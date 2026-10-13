using System.Text.Json;
using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Catalog;
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
                    JsonValueKind.Object when element.TryGetProperty("name", out var n) => n.GetString(),
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
        item.CreatedAt, item.CreatedBy, item.UpdatedAt, item.DeletedAt);

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

    public static async Task<LoadedRelease?> LoadReleaseAsync(IAppDbContext db, Guid releaseId, CancellationToken cancellationToken)
    {
        var release = await db.Releases.AsNoTracking().FirstOrDefaultAsync(r => r.Id == releaseId, cancellationToken);
        if (release is null)
        {
            return null;
        }

        var work = await db.Works.AsNoTracking().FirstAsync(w => w.Id == release.WorkId, cancellationToken);
        var platform = release.PlatformId is { } platformId
            ? await db.Platforms.AsNoTracking().FirstOrDefaultAsync(p => p.Id == platformId, cancellationToken)
            : null;
        var identifiers = await db.ReleaseIdentifiers.AsNoTracking()
            .Where(i => i.ReleaseId == releaseId)
            .OrderBy(i => i.Type).ThenBy(i => i.Value)
            .Select(i => new ReleaseIdentifierDto(i.Type, i.Value))
            .ToListAsync(cancellationToken);
        return new LoadedRelease(release, work, platform, identifiers);
    }

    public static WorkDto ToWorkDto(Work work) => new(
        work.Id, work.Kind, work.Title, work.OriginalYear, Creators(work.CreatorsJson), work.Genres, work.Synopsis, CoverUrl(work.CoverAssetId));

    public static ReleaseDto ToReleaseDto(LoadedRelease loaded) => new(
        loaded.Release.Id,
        loaded.Work.Id,
        loaded.Work.Title,
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
