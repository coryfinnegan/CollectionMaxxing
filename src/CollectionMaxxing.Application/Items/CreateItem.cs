using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Libraries.Members;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Operations;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Items;

/// <summary>What to create when an item isn't being added from a known release.</summary>
public sealed record ManualRelease(
    WorkKind? Kind, string? Title, int? Year, ReleaseFormat? Format, Guid? PlatformId, IReadOnlyList<string>? Creators, string? Barcode);

/// <summary>
/// Use case: add an item, from a release or from manual details (which creates a release private to this library).
/// Safe to retry: the same <c>Idempotency-Key</c> and request returns the same item.
/// </summary>
public static class CreateItem
{
    public const int MaxKeyLength = 200;
    public const int MaxTitleLength = 500;
    private static readonly TimeSpan KeyLifetime = TimeSpan.FromHours(24);

    public sealed record Command(
        Guid LibraryId,
        string? IdempotencyKey,
        Guid? ReleaseId,
        ManualRelease? Manual,
        ItemStatus? Status,
        ItemCondition? Condition,
        ItemCompleteness? Completeness,
        Guid? LocationId,
        string? Notes,
        DateOnly? AcquiredOn,
        AddedVia? AddedVia,
        IReadOnlyList<Guid>? TagIds);

    public sealed class Handler(
        IAppDbContext db, IIdGenerator ids, ICurrentUser currentUser, TimeProvider clock, IUserDirectory? users = null, ReleaseCovers? covers = null)
    {
        public async Task<Result<Versioned<ItemDto>>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            var userId = currentUser.UserId
                ?? throw new InvalidOperationException("CreateItem needs a signed-in caller; the endpoint requires authorization.");
            var libraryId = command.LibraryId;

            var key = command.IdempotencyKey?.Trim();
            if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength)
            {
                return ItemErrors.Invalid("idempotency.key_required", "Send an Idempotency-Key header of 1 to 200 characters.", "Idempotency-Key");
            }

            if (!await db.Libraries.AnyAsync(l => l.Id == libraryId, cancellationToken))
            {
                return Errors.LibraryNotFound;
            }

            var now = clock.GetUtcNow();
            var hash = RequestHash(command);
            var previous = await db.IdempotencyKeys.FirstOrDefaultAsync(k => k.UserId == userId && k.Key == key, cancellationToken);
            if (previous is not null)
            {
                if (previous.ExpiresAt > now)
                {
                    return await ReplayAsync(previous, hash, libraryId, cancellationToken);
                }

                db.IdempotencyKeys.Remove(previous); // expired: the key can be used again
            }

            var invalid = Validate(command);
            if (invalid is not null)
            {
                return invalid;
            }

            var tagIds = command.TagIds?.Distinct().ToList() ?? [];
            if (command.LocationId is { } locationId
                && !await ItemProjection.LocationInLibraryAsync(db, libraryId, locationId, cancellationToken))
            {
                return ItemErrors.LocationNotFound;
            }

            if (!await ItemProjection.TagsInLibraryAsync(db, libraryId, tagIds, cancellationToken))
            {
                return ItemErrors.TagNotFound;
            }

            Guid releaseId;
            if (command.ReleaseId is { } existingRelease)
            {
                if (!await ItemProjection.CanUseReleaseAsync(db, libraryId, existingRelease, cancellationToken))
                {
                    return ItemErrors.ReleaseNotFound;
                }

                releaseId = existingRelease;
            }
            else
            {
                var manual = command.Manual!;
                if (manual.PlatformId is { } platformId && !await db.Platforms.AnyAsync(p => p.Id == platformId, cancellationToken))
                {
                    return ItemErrors.PlatformNotFound;
                }

                releaseId = AddManualRelease(manual, libraryId, now);
            }

            var item = new Item
            {
                Id = ids.NewId(),
                LibraryId = libraryId,
                ReleaseId = releaseId,
                Status = command.Status ?? ItemStatus.Owned,
                Condition = command.Condition,
                Completeness = command.Completeness,
                LocationId = command.LocationId,
                Notes = command.Notes,
                AcquiredOn = command.AcquiredOn ?? DateOnly.FromDateTime(now.UtcDateTime),
                CoverChoice = CoverChoice.Release,
                AddedVia = command.AddedVia ?? (command.Manual is null ? AddedVia.Search : AddedVia.Manual),
                CreatedAt = now,
                CreatedBy = userId, // always the caller, never the body
                UpdatedAt = now,
            };
            db.Items.Add(item);
            foreach (var tagId in tagIds)
            {
                db.ItemTags.Add(new ItemTag { LibraryId = libraryId, ItemId = item.Id, TagId = tagId });
            }

            db.IdempotencyKeys.Add(new IdempotencyKey
            {
                UserId = userId,
                Key = key,
                RequestHash = hash,
                ResponseStatus = 201,
                ResponseBodyJson = JsonSerializer.Serialize(new StoredResponse(item.Id)),
                CreatedAt = now,
                ExpiresAt = now + KeyLifetime,
            });

            // An add from the catalogue takes the same cover step as a scan commit, so a picked release shows its picture at once.
            IReadOnlyList<string> coverFiles = covers is null ? [] : await covers.FetchMissingAsync([releaseId], cancellationToken);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch
            {
                if (covers is not null)
                {
                    await covers.DeleteFilesAsync(coverFiles); // nothing points at the covers just downloaded
                }

                throw;
            }

            return new Versioned<ItemDto>(await ItemProjection.ToDtoAsync(item, tagIds.Order().ToList(), users, cancellationToken), item.Version);
        }

        private async Task<Result<Versioned<ItemDto>>> ReplayAsync(
            IdempotencyKey previous, string hash, Guid libraryId, CancellationToken cancellationToken)
        {
            if (previous.RequestHash != hash)
            {
                return ItemErrors.KeyReused;
            }

            var stored = JsonSerializer.Deserialize<StoredResponse>(previous.ResponseBodyJson ?? "{}");
            var item = stored is null
                ? null
                : await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.LibraryId == libraryId && i.Id == stored.ItemId, cancellationToken);
            if (item is null)
            {
                return ItemErrors.NotFound;
            }

            var tagIds = await ItemProjection.TagIdsAsync(db, libraryId, item.Id, cancellationToken);
            return new Versioned<ItemDto>(await ItemProjection.ToDtoAsync(item, tagIds, users, cancellationToken), item.Version);
        }

        private static DomainError? Validate(Command command)
        {
            if ((command.ReleaseId is null) == (command.Manual is null))
            {
                return ItemErrors.Invalid("item.source_required", "Give either a releaseId or manual details, not both.", "releaseId");
            }

            if (command.Notes is { Length: > Item.MaxNotesLength })
            {
                return ItemErrors.Invalid("item.notes_too_long", $"Notes can be at most {Item.MaxNotesLength} characters.", "notes");
            }

            if (command.Manual is { } manual)
            {
                if (string.IsNullOrWhiteSpace(manual.Title))
                {
                    return ItemErrors.Invalid("item.title_required", "Give the item a title.", "manual.title");
                }

                if (manual.Title.Trim().Length > MaxTitleLength)
                {
                    return ItemErrors.Invalid("item.title_too_long", $"A title can be at most {MaxTitleLength} characters.", "manual.title");
                }

                if (manual.Year is { } year && (year < 1 || year > 3000))
                {
                    return ItemErrors.Invalid("item.year_invalid", "That year doesn't look right.", "manual.year");
                }

                if (!string.IsNullOrWhiteSpace(manual.Barcode) && IdentifierFor(manual.Barcode) is null)
                {
                    return ItemErrors.Invalid("item.barcode_invalid", "A barcode is 8 or 13 digits.", "manual.barcode");
                }
            }

            return null;
        }

        private Guid AddManualRelease(ManualRelease manual, Guid libraryId, DateTimeOffset now)
        {
            var title = manual.Title!.Trim();
            var work = new Work
            {
                Id = ids.NewId(),
                Kind = manual.Kind ?? WorkKind.Other,
                Title = title,
                SortTitle = SortTitle(title),
                NormalizedTitle = Normalize(title),
                OriginalYear = manual.Year,
                CreatorsJson = JsonSerializer.Serialize((manual.Creators ?? []).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim())),
                CreatedAt = now,
            };
            var release = new Release
            {
                Id = ids.NewId(),
                WorkId = work.Id,
                Format = manual.Format ?? ReleaseFormat.Unknown,
                PlatformId = manual.PlatformId,
                Source = ReleaseSource.User,
                OwnerLibraryId = libraryId,
                CreatedAt = now,
            };
            db.Works.Add(work);
            db.Releases.Add(release);

            if (!string.IsNullOrWhiteSpace(manual.Barcode) && IdentifierFor(manual.Barcode) is { } type)
            {
                db.ReleaseIdentifiers.Add(new ReleaseIdentifier { ReleaseId = release.Id, Type = type, Value = manual.Barcode.Trim() });
            }

            return release.Id;
        }

        private static IdentifierType? IdentifierFor(string barcode)
        {
            var digits = barcode.Trim();
            if (digits.Length == 0 || !digits.All(char.IsAsciiDigit))
            {
                return null;
            }

            return digits.Length switch
            {
                13 when digits.StartsWith("978", StringComparison.Ordinal) || digits.StartsWith("979", StringComparison.Ordinal) => IdentifierType.Isbn13,
                13 => IdentifierType.Gtin13,
                8 => IdentifierType.Gtin8,
                _ => null,
            };
        }

        private static string RequestHash(Command command)
        {
            var json = JsonSerializer.Serialize(command with { IdempotencyKey = null });
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        }
    }

    private sealed record StoredResponse(Guid ItemId);

    /// <summary>"The Matrix" sorts as "matrix".</summary>
    internal static string SortTitle(string title)
    {
        var lower = title.ToLowerInvariant();
        foreach (var article in new[] { "the ", "a ", "an " })
        {
            if (lower.StartsWith(article, StringComparison.Ordinal) && lower.Length > article.Length)
            {
                return lower[article.Length..];
            }
        }

        return lower;
    }

    /// <summary>Lower case, accents and punctuation removed: what the search matches against.</summary>
    internal static string Normalize(string title)
    {
        var builder = new StringBuilder();
        foreach (var c in title.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
