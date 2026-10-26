using System.Globalization;
using System.Text;
using System.Text.Json;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Items;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Items;

/// <summary>
/// Use case: list a library's items with filters, a sort and cursor pagination. Every sort is "a key, then the id", so the
/// order is total and a page boundary never repeats or skips an item. Deleted items are left out.
/// </summary>
public static class ListItems
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public sealed record Query(
        Guid LibraryId,
        string? Q,
        string? Kind,
        string? Format,
        string? Platform,
        string? Status,
        Guid? LocationId,
        string? Tag,
        string? Sort,
        int? Limit,
        string? Cursor,
        bool? EditionVerified = null);

    private sealed class Row
    {
        public Guid Id { get; init; }

        public Guid ReleaseId { get; init; }

        public Guid WorkId { get; init; }

        public string Title { get; init; } = string.Empty;

        /// <summary>Whether <see cref="Title"/> is the person's own edit: those display verbatim, never re-cased.</summary>
        public bool HasCustomTitle { get; init; }

        public string TitleKey { get; init; } = string.Empty;

        public WorkKind Kind { get; init; }

        public ReleaseFormat Format { get; init; }

        public string? PlatformName { get; init; }

        public string PlatformKey { get; init; } = string.Empty;

        public int? Year { get; init; }

        public int YearKey { get; init; }

        public ItemStatus Status { get; init; }

        public ItemCondition? Condition { get; init; }

        public Guid? LocationId { get; init; }

        public Guid? CoverAssetId { get; init; }

        public bool EditionVerified { get; init; }

        public DateTimeOffset CreatedAt { get; init; }
    }

    private sealed record CursorValue(string K, Guid I);

    public sealed class Handler(IAppDbContext db)
    {
        public async Task<Result<Page<ItemSummaryDto>>> HandleAsync(Query query, CancellationToken cancellationToken)
        {
            var sort = query.Sort?.Trim() switch
            {
                null or "" or "title" => "title",
                "-addedAt" => "-addedAt",
                "year" => "year",
                "platform" => "platform",
                _ => null,
            };
            if (sort is null)
            {
                return ItemErrors.Invalid("items.sort_invalid", "Sort by title, -addedAt, year or platform.", "sort");
            }

            if (!TryParseEnum<ReleaseFormat>(query.Format, out var format))
            {
                return ItemErrors.Invalid("items.format_invalid", "That isn't a known format.", "format");
            }

            if (!TryParseEnum<WorkKind>(query.Kind, out var kind))
            {
                return ItemErrors.Invalid("items.kind_invalid", "That isn't a known kind.", "kind");
            }

            if (!TryParseEnum<ItemStatus>(query.Status, out var status))
            {
                return ItemErrors.Invalid("items.status_invalid", "That isn't a known status.", "status");
            }

            var limit = query.Limit is null or < 1 ? DefaultLimit : Math.Min(query.Limit.Value, MaxLimit);

            CursorValue? cursor = null;
            if (!string.IsNullOrEmpty(query.Cursor) && !TryDecode(query.Cursor, out cursor))
            {
                return ItemErrors.Invalid("items.cursor_invalid", "That cursor isn't valid.", "cursor");
            }

            var rows = BaseQuery(query, kind, format, status);
            IQueryable<Row> ordered;
            try
            {
                ordered = Order(rows, sort, cursor);
            }
            catch (Exception e) when (e is FormatException or OverflowException) // an out-of-range year overflows int.Parse
            {
                return ItemErrors.Invalid("items.cursor_invalid", "That cursor isn't valid.", "cursor");
            }

            var page = await ordered.Take(limit + 1).ToListAsync(cancellationToken);
            var more = page.Count > limit;
            if (more)
            {
                page.RemoveAt(page.Count - 1);
            }

            var items = page.Select(r => new ItemSummaryDto(
                r.Id, r.ReleaseId, r.WorkId, r.HasCustomTitle ? r.Title : TitleCaser.ToTitleCase(r.Title), r.Kind, r.Format,
                r.PlatformName, r.Year, r.Status, r.Condition, r.LocationId, ItemProjection.CoverUrl(r.CoverAssetId), r.CreatedAt,
                r.EditionVerified)).ToList();
            return new Page<ItemSummaryDto>(items, more ? Encode(sort, page[^1]) : null);
        }

        /// <summary>Empty text means "no filter"; otherwise it must be one of the enum's lower snake_case spellings.</summary>
        private static bool TryParseEnum<TEnum>(string? text, out TEnum? value)
            where TEnum : struct, Enum
        {
            value = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                return true;
            }

            var wanted = text.Trim();
            if (!EnumText.All<TEnum>().Contains(wanted))
            {
                return false;
            }

            value = EnumText.Parse<TEnum>(wanted);
            return true;
        }

        private IQueryable<Row> BaseQuery(Query query, WorkKind? kind, ReleaseFormat? format, ItemStatus? status)
        {
            var libraryId = query.LibraryId;
            var items = db.Items.AsNoTracking().Where(i => i.LibraryId == libraryId && i.DeletedAt == null);

            if (status is { } wantedStatus)
            {
                items = items.Where(i => i.Status == wantedStatus);
            }

            if (query.EditionVerified is { } verified)
            {
                items = items.Where(i => i.EditionVerified == verified);
            }

            if (query.LocationId is { } locationId)
            {
                items = items.Where(i => i.LocationId == locationId);
            }

            if (!string.IsNullOrWhiteSpace(query.Tag))
            {
                var tag = query.Tag.Trim();
                items = items.Where(i => db.ItemTags.Any(it => it.ItemId == i.Id && db.Tags.Any(t => t.Id == it.TagId && t.Name == tag)));
            }

            var rows =
                from i in items
                join r in db.Releases on i.ReleaseId equals r.Id
                join w in db.Works on r.WorkId equals w.Id
                join p in db.Platforms on r.PlatformId equals p.Id into platforms
                from p in platforms.DefaultIfEmpty()
                select new
                {
                    Item = i,
                    Release = r,
                    Work = w,
                    Platform = p,
                };

            if (kind is { } wantedKind)
            {
                rows = rows.Where(x => x.Work.Kind == wantedKind);
            }

            if (format is { } wanted)
            {
                rows = rows.Where(x => x.Release.Format == wanted);
            }

            // ToLower() is what Postgres sees (lower(x) = lower(y)); EF can't translate the StringComparison overloads.
#pragma warning disable CA1862
            if (!string.IsNullOrWhiteSpace(query.Platform))
            {
                var platform = query.Platform.Trim().ToLower();
                rows = rows.Where(x => x.Platform != null && (x.Platform.Name.ToLower() == platform || x.Platform.ShortName.ToLower() == platform));
            }

            if (!string.IsNullOrWhiteSpace(query.Q))
            {
                var text = query.Q.Trim().ToLower();
                rows = rows.Where(x =>
                    x.Work.Title.ToLower().Contains(text) || (x.Item.CustomTitle != null && x.Item.CustomTitle.ToLower().Contains(text)));
            }
#pragma warning restore CA1862

            return rows.Select(x => new Row
            {
                Id = x.Item.Id,
                ReleaseId = x.Item.ReleaseId,
                WorkId = x.Work.Id,
                Title = x.Item.CustomTitle ?? x.Work.Title,
                HasCustomTitle = x.Item.CustomTitle != null,
                TitleKey = x.Item.CustomTitle != null ? x.Item.CustomTitle.ToLower() : x.Work.SortTitle.ToLower(),
                Kind = x.Work.Kind,
                Format = x.Release.Format,
                PlatformName = x.Platform == null ? null : x.Platform.Name,
                PlatformKey = x.Platform == null ? string.Empty : x.Platform.Name.ToLower(),
                Year = x.Release.ReleaseDate != null ? x.Release.ReleaseDate.Value.Year : x.Work.OriginalYear,
                YearKey = (x.Release.ReleaseDate != null ? x.Release.ReleaseDate.Value.Year : x.Work.OriginalYear) ?? int.MaxValue,
                Status = x.Item.Status,
                Condition = x.Item.Condition,
                LocationId = x.Item.LocationId,
                CoverAssetId = x.Item.CoverChoice == CoverChoice.MyPhoto && x.Item.CustomCoverAssetId != null
                    ? x.Item.CustomCoverAssetId
                    : x.Release.CoverAssetId ?? x.Work.CoverAssetId,
                EditionVerified = x.Item.EditionVerified,
                CreatedAt = x.Item.CreatedAt,
            });
        }

        private static IQueryable<Row> Order(IQueryable<Row> rows, string sort, CursorValue? cursor)
        {
            switch (sort)
            {
                case "-addedAt":
                    {
                        if (cursor is not null)
                        {
                            var after = DateTimeOffset.Parse(cursor.K, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                            var id = cursor.I;
                            rows = rows.Where(r => r.CreatedAt < after || (r.CreatedAt == after && r.Id.CompareTo(id) < 0));
                        }

                        return rows.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id);
                    }

                case "year":
                    {
                        if (cursor is not null)
                        {
                            var after = int.Parse(cursor.K, CultureInfo.InvariantCulture);
                            var id = cursor.I;
                            rows = rows.Where(r => r.YearKey > after || (r.YearKey == after && r.Id.CompareTo(id) > 0));
                        }

                        return rows.OrderBy(r => r.YearKey).ThenBy(r => r.Id);
                    }

                case "platform":
                    {
                        if (cursor is not null)
                        {
                            var after = cursor.K;
                            var id = cursor.I;
                            rows = rows.Where(r => string.Compare(r.PlatformKey, after) > 0 || (r.PlatformKey == after && r.Id.CompareTo(id) > 0));
                        }

                        return rows.OrderBy(r => r.PlatformKey).ThenBy(r => r.Id);
                    }

                default:
                    {
                        if (cursor is not null)
                        {
                            var after = cursor.K;
                            var id = cursor.I;
                            rows = rows.Where(r => string.Compare(r.TitleKey, after) > 0 || (r.TitleKey == after && r.Id.CompareTo(id) > 0));
                        }

                        return rows.OrderBy(r => r.TitleKey).ThenBy(r => r.Id);
                    }
            }
        }

        private static string Encode(string sort, Row last)
        {
            var key = sort switch
            {
                "-addedAt" => last.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                "year" => last.YearKey.ToString(CultureInfo.InvariantCulture),
                "platform" => last.PlatformKey,
                _ => last.TitleKey,
            };
            var json = JsonSerializer.Serialize(new CursorValue(key, last.Id));
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
                return cursor is { K: not null };
            }
            catch (Exception e) when (e is FormatException or JsonException or ArgumentException)
            {
                return false;
            }
        }
    }
}
