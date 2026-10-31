using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Locations;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Locations;

/// <summary>
/// Use case: delete a location that has nothing inside it. "Nothing inside" means no child locations and no live items; a
/// live item still in the place keeps it (the database refuses, and the caller sees a 409).
/// <para>
/// Three things only remember the place and don't keep it, so deleting forgets it for them, in the same save: a scan
/// session's default location (the session carries on without one), a kept row's location (the row stays, unplaced) and a
/// trashed item's location (Recently deleted keeps the item for 30 days; if it is restored it simply has no location, as an item
/// added without one does). Without this a place once used for scanning, or one an item was deleted from, could never be deleted.
/// </para>
/// </summary>
public static class DeleteLocation
{
    public sealed record Command(Guid LibraryId, Guid Id);

    public sealed class Handler(IAppDbContext db)
    {
        public async Task<Result> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            var existing = await db.Locations.Where(l => l.LibraryId == command.LibraryId).ToListAsync(cancellationToken);
            var removed = new LocationTree(command.LibraryId, existing).Remove(command.Id);
            if (!removed.IsSuccess)
            {
                return removed;
            }

            await ForgetThePlaceAsync(command, cancellationToken);
            db.Locations.Remove(existing.Single(l => l.Id == command.Id));
            await db.SaveChangesAsync(cancellationToken); // one transaction: the delete and the forgetting happen together or not at all
            return Result.Success();
        }

        /// <summary>Clears the location from this library's sessions, kept rows and trashed items. Saves nothing; the caller saves.</summary>
        private async Task ForgetThePlaceAsync(Command command, CancellationToken cancellationToken)
        {
            var sessions = await db.ScanSessions
                .Where(s => s.LibraryId == command.LibraryId && s.DefaultLocationId == command.Id)
                .ToListAsync(cancellationToken);
            foreach (var session in sessions)
            {
                session.DefaultLocationId = null;
            }

            var kept = await db.ProvisionalRecords
                .Where(p => p.LibraryId == command.LibraryId && p.LocationId == command.Id)
                .ToListAsync(cancellationToken);
            foreach (var record in kept)
            {
                // Init-only (where it was kept is history), so it is written through the entry.
                db.ProvisionalRecords.Attach(record).Property(p => p.LocationId).CurrentValue = null;
            }

            var trashed = await db.Items
                .Where(i => i.LibraryId == command.LibraryId && i.LocationId == command.Id && i.DeletedAt != null)
                .ToListAsync(cancellationToken);
            foreach (var item in trashed)
            {
                item.LocationId = null;
            }
        }
    }
}
