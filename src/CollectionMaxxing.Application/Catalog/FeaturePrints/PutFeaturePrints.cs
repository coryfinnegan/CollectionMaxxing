using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Items;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Catalog.FeaturePrints;

/// <summary>
/// Use case: the phone backs up its Apple feature prints (SCAN-2c). Up to 200 per call; <c>data</c> is opaque base64 of at most 16 KB and is
/// never parsed. The whole batch is checked before anything is written, and it is all or nothing. Every item must be a (not deleted) item of
/// this library and every release one this library may use (shared, or its own): anything else is a 404 and nothing is written, so a print
/// can't be filed under another library's item. One row is kept per item and revision: a print that repeats an item and revision replaces the
/// earlier row, which counts as new again (<c>?since=</c> syncs by when a row was written). The server sets the time; the client's
/// <c>createdAt</c> is ignored. The caller's role in the library was checked by the endpoint.
/// </summary>
public static class PutFeaturePrints
{
    public sealed record Command(Guid LibraryId, IReadOnlyList<FeaturePrintDto?>? Prints);

    private sealed record Checked(Guid ItemId, Guid ReleaseId, int Revision, byte[] Data);

    public sealed class Handler(IAppDbContext db, IIdGenerator ids, TimeProvider clock)
    {
        public async Task<Result> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            var parsed = Parse(command.Prints);
            if (!parsed.IsSuccess)
            {
                return Result.Failure(parsed.Error!);
            }

            // The last print for an item and revision wins, as a client that sends the same one twice would expect.
            var prints = parsed.Value
                .GroupBy(p => (p.ItemId, p.Revision))
                .Select(g => g.Last())
                .ToList();

            var itemIds = prints.Select(p => p.ItemId).Distinct().ToList();
            var foundItems = await db.Items.AsNoTracking()
                .Where(i => i.LibraryId == command.LibraryId && i.DeletedAt == null && itemIds.Contains(i.Id))
                .Select(i => i.Id)
                .ToListAsync(cancellationToken);
            if (foundItems.Count != itemIds.Count)
            {
                return Result.Failure(FeaturePrintErrors.ItemNotFound);
            }

            var releaseIds = prints.Select(p => p.ReleaseId).Distinct().ToList();
            var foundReleases = await db.Releases.AsNoTracking()
                .Where(r => releaseIds.Contains(r.Id) && (r.OwnerLibraryId == null || r.OwnerLibraryId == command.LibraryId))
                .Select(r => r.Id)
                .ToListAsync(cancellationToken);
            if (foundReleases.Count != releaseIds.Count)
            {
                return Result.Failure(FeaturePrintErrors.ReleaseNotFound);
            }

            var now = clock.GetUtcNow();
            var replaced = prints.Select(p => (p.ItemId, p.Revision)).ToHashSet();
            var existing = await db.ItemFeaturePrints
                .Where(p => p.LibraryId == command.LibraryId && itemIds.Contains(p.ItemId))
                .ToListAsync(cancellationToken);
            db.ItemFeaturePrints.RemoveRange(existing.Where(p => replaced.Contains((p.ItemId, p.Revision))));

            foreach (var print in prints)
            {
                db.ItemFeaturePrints.Add(new ItemFeaturePrint
                {
                    Id = ids.NewId(),
                    LibraryId = command.LibraryId,
                    ItemId = print.ItemId,
                    ReleaseId = print.ReleaseId,
                    Revision = print.Revision,
                    Data = print.Data,
                    CreatedAt = now,
                });
            }

            await db.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }

        private static Result<List<Checked>> Parse(IReadOnlyList<FeaturePrintDto?>? prints)
        {
            if (prints is null or { Count: 0 })
            {
                return FeaturePrintErrors.Invalid("featureprints.required", "Send at least one print.", "prints");
            }

            if (prints.Count > FeaturePrintErrors.MaxBatch)
            {
                return FeaturePrintErrors.Invalid(
                    "featureprints.too_many", $"Send at most {FeaturePrintErrors.MaxBatch} prints at a time.", "prints");
            }

            // Four base64 characters hold three bytes; anything longer than the limit allows is refused before it is decoded.
            var longestText = (FeaturePrintErrors.MaxDataBytes + 2) / 3 * 4;
            var result = new List<Checked>(prints.Count);
            foreach (var print in prints)
            {
                if (print is null || print.ItemId == Guid.Empty || print.ReleaseId == Guid.Empty)
                {
                    return FeaturePrintErrors.Invalid("featureprints.ids_required", "Every print needs an itemId and a releaseId.", "prints");
                }

                if (print.Revision < 1)
                {
                    return FeaturePrintErrors.Invalid("featureprints.revision_invalid", "The revision must be 1 or more.", "prints");
                }

                if (string.IsNullOrEmpty(print.Data))
                {
                    return FeaturePrintErrors.Invalid("featureprints.data_required", "Every print needs its data.", "prints");
                }

                if (print.Data.Length > longestText)
                {
                    return DataTooLarge();
                }

                // Room for everything a text that passed the length check can decode to, so false below means "not base64".
                var buffer = new byte[FeaturePrintErrors.MaxDataBytes + 3];
                if (!Convert.TryFromBase64String(print.Data, buffer, out var length))
                {
                    return FeaturePrintErrors.Invalid("featureprints.data_invalid", "The data must be base64.", "prints");
                }

                if (length == 0 || length > FeaturePrintErrors.MaxDataBytes)
                {
                    return length == 0
                        ? FeaturePrintErrors.Invalid("featureprints.data_required", "Every print needs its data.", "prints")
                        : DataTooLarge();
                }

                result.Add(new Checked(print.ItemId, print.ReleaseId, print.Revision, buffer.AsSpan(0, length).ToArray()));
            }

            return result;
        }

        private static DomainError DataTooLarge() => FeaturePrintErrors.Invalid(
            "featureprints.data_too_large", $"A print can be at most {FeaturePrintErrors.MaxDataBytes / 1024} KB.", "prints");
    }
}
