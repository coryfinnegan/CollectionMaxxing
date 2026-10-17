using System.Globalization;
using System.Text;
using System.Text.Json;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Catalog.FeaturePrints;

/// <summary>
/// Use case: the phone restores its feature prints (or catches up on the ones another phone backed up). Pages of up to 200, oldest first, by
/// when they were written. <c>since</c> keeps only rows written after that moment; <c>nextCursor</c> continues a page, and is exact even when
/// many rows share one timestamp (a batch does): the order is the timestamp, then the id. Only this library's rows are read, whatever the
/// caller sends. The data is returned as stored, base64.
/// </summary>
public static class ListFeaturePrints
{
    public const int DefaultLimit = FeaturePrintErrors.MaxPage;

    public sealed record Query(Guid LibraryId, DateTimeOffset? Since, int? Limit, string? Cursor);

    private sealed record CursorValue(string T, Guid I);

    public sealed class Handler(IAppDbContext db)
    {
        public async Task<Result<Page<FeaturePrintDto>>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            CursorValue? cursor = null;
            if (!string.IsNullOrEmpty(query.Cursor) && !TryDecode(query.Cursor, out cursor))
            {
                return FeaturePrintErrors.BadCursor;
            }

            var limit = query.Limit is null or < 1 ? DefaultLimit : Math.Min(query.Limit.Value, FeaturePrintErrors.MaxPage);
            var rows = db.ItemFeaturePrints.AsNoTracking().Where(p => p.LibraryId == query.LibraryId);
            if (query.Since is { } since)
            {
                rows = rows.Where(p => p.CreatedAt > since);
            }

            if (cursor is not null)
            {
                var after = DateTimeOffset.Parse(cursor.T, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                var id = cursor.I;
                rows = rows.Where(p => p.CreatedAt > after || (p.CreatedAt == after && p.Id.CompareTo(id) > 0));
            }

            var page = await rows.OrderBy(p => p.CreatedAt).ThenBy(p => p.Id).Take(limit + 1).ToListAsync(cancellationToken);
            var more = page.Count > limit;
            if (more)
            {
                page.RemoveAt(page.Count - 1);
            }

            var items = page
                .Select(p => new FeaturePrintDto(p.ItemId, p.ReleaseId, p.Revision, Convert.ToBase64String(p.Data), p.CreatedAt))
                .ToList();
            return new Page<FeaturePrintDto>(items, more ? Encode(page[^1].CreatedAt, page[^1].Id) : null);
        }

        private static string Encode(DateTimeOffset createdAt, Guid id)
        {
            var json = JsonSerializer.Serialize(new CursorValue(createdAt.ToString("O", CultureInfo.InvariantCulture), id));
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static bool TryDecode(string text, out CursorValue? cursor)
        {
            cursor = null;
            try
            {
                var base64 = text.Replace('-', '+').Replace('_', '/');
                base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
                cursor = JsonSerializer.Deserialize<CursorValue>(Encoding.UTF8.GetString(Convert.FromBase64String(base64)));
                return cursor is { T: not null } && DateTimeOffset.TryParse(cursor.T, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);
            }
            catch (Exception e) when (e is FormatException or JsonException or ArgumentException)
            {
                return false;
            }
        }
    }
}
