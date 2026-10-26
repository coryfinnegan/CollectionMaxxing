using System.Text;
using System.Text.Json;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Items;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Trash;

/// <summary>An item in "Recently deleted": when it was deleted and when it will be gone for good.</summary>
public sealed record DeletedItemDto(
    Guid Id,
    Guid ReleaseId,
    Guid WorkId,
    string Title,
    WorkKind Kind,
    ReleaseFormat Format,
    string? PlatformName,
    int? Year,
    string? CoverUrl,
    DateTimeOffset DeletedAt,
    DateTimeOffset PurgesAt);

/// <summary>Use case: the "Recently deleted" bin. Newest deletions first, only items that can still be restored.</summary>
public static class ListDeletedItems
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public sealed record Query(Guid LibraryId, int? Limit, string? Cursor);

    private sealed record CursorValue(DateTimeOffset At, Guid Id);

    public sealed class Handler(IAppDbContext db, TimeProvider clock)
    {
        public async Task<Result<Page<DeletedItemDto>>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            var limit = query.Limit is null or < 1 ? DefaultLimit : Math.Min(query.Limit.Value, MaxLimit);
            CursorValue? cursor = null;
            if (!string.IsNullOrEmpty(query.Cursor) && !TryDecode(query.Cursor, out cursor))
            {
                return ItemErrors.Invalid("items.cursor_invalid", "That cursor isn't valid.", "cursor");
            }

            var libraryId = query.LibraryId;
            var oldest = clock.GetUtcNow() - RestoreItem.Window;
            var items = db.Items.AsNoTracking().Where(i => i.LibraryId == libraryId && i.DeletedAt != null && i.DeletedAt >= oldest);
            if (cursor is not null)
            {
                var at = cursor.At;
                var id = cursor.Id;
                items = items.Where(i => i.DeletedAt < at || (i.DeletedAt == at && i.Id.CompareTo(id) < 0));
            }

            var rows = await (
                from i in items
                join r in db.Releases on i.ReleaseId equals r.Id
                join w in db.Works on r.WorkId equals w.Id
                join p in db.Platforms on r.PlatformId equals p.Id into platforms
                from p in platforms.DefaultIfEmpty()
                orderby i.DeletedAt descending, i.Id descending
                select new
                {
                    i.Id,
                    i.ReleaseId,
                    WorkId = w.Id,
                    Title = i.CustomTitle ?? w.Title,
                    HasCustomTitle = i.CustomTitle != null,
                    w.Kind,
                    r.Format,
                    PlatformName = p == null ? null : p.Name,
                    Year = r.ReleaseDate != null ? r.ReleaseDate!.Value.Year : w.OriginalYear,
                    Cover = i.CoverChoice == CoverChoice.MyPhoto && i.CustomCoverAssetId != null
                        ? i.CustomCoverAssetId
                        : r.CoverAssetId ?? w.CoverAssetId,
                    DeletedAt = i.DeletedAt!.Value,
                })
                .Take(limit + 1)
                .ToListAsync(cancellationToken);

            var more = rows.Count > limit;
            if (more)
            {
                rows.RemoveAt(rows.Count - 1);
            }

            var dtos = rows.Select(r => new DeletedItemDto(
                r.Id, r.ReleaseId, r.WorkId, r.HasCustomTitle ? r.Title : TitleCaser.ToTitleCase(r.Title), r.Kind, r.Format, r.PlatformName, r.Year,
                ItemProjection.CoverUrl(r.Cover), r.DeletedAt, r.DeletedAt + RestoreItem.Window)).ToList();
            return new Page<DeletedItemDto>(dtos, more ? Encode(rows[^1].DeletedAt, rows[^1].Id) : null);
        }

        private static string Encode(DateTimeOffset at, Guid id) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new CursorValue(at, id))))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static bool TryDecode(string text, out CursorValue? cursor)
        {
            cursor = null;
            try
            {
                var base64 = text.Replace('-', '+').Replace('_', '/');
                base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
                cursor = JsonSerializer.Deserialize<CursorValue>(Encoding.UTF8.GetString(Convert.FromBase64String(base64)));
                return cursor is not null;
            }
            catch (Exception e) when (e is FormatException or JsonException or ArgumentException)
            {
                return false;
            }
        }
    }
}
