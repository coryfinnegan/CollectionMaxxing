using CollectionMaxxing.Application.Audit;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Jobs;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Accounts.Deletion;

public sealed record PurgeAccountPayload(Guid UserId);

/// <summary>
/// Job: 30 days after an account was deleted, its data is removed for good. That is the library, if the user was its only member, with
/// everything in it and its image files; the user's place in any other library; their sign-in material; and the identity in the audit
/// trail. Items they added to a library that carries on stay, and say "Added by" a removed user.
/// The job is queued by <see cref="DeleteAccount"/> to run when the 30 days are over. Safe to repeat: every step finds what is left and
/// removes that, so a purge that crashed halfway finishes on the next run. A run for an account that was never deleted does nothing; a run
/// before the 30 days are over queues itself again for the day they are. A job that died is queued again by <see cref="PurgeSweep"/>.
/// </summary>
public static class PurgeAccount
{
    public const string JobType = "account.purge";

    /// <summary>How long a deleted account's data is kept (docs/03, "Data lifecycle").</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    /// <summary>What an invite says for a person who is gone (and what "Added by" says: <c>IdentityDeletedAccounts</c> uses the same words).</summary>
    public const string RemovedUserName = "Removed user";

    public static string DedupeKey(Guid userId) => $"{JobType}:{userId:N}";

    public sealed class Handler(
        IAppDbContext db,
        TimeProvider clock,
        IDeletedAccounts accounts,
        IAccountFiles files,
        IEnumerable<ILibraryPurgeStep> steps,
        AnonymiseAuditActor.Handler anonymiseAudit,
        IAuditLog audit,
        IEnumerable<IUserPurgeStep>? userSteps = null,
        IJobQueue? jobs = null) : IJobHandler<PurgeAccountPayload>
    {
        public async Task HandleAsync(PurgeAccountPayload payload, CancellationToken cancellationToken)
        {
            var userId = payload.UserId;
            if (await accounts.DeletedAtAsync(userId, cancellationToken) is not { } deletedAt)
            {
                return; // never deleted (or not there): nothing to do
            }

            var due = deletedAt + Retention;
            if (clock.GetUtcNow() < due)
            {
                // This job was queued by an earlier attempt to delete the account, so it fires before the date the account is actually due
                // (the dedupe key made the later attempt reuse it). Finishing quietly would lose the purge: queue it again for the right day.
                // The key names that day, so a job that runs twice queues it once, and it is not the key of the job that is running now.
                if (jobs is not null)
                {
                    await jobs.EnqueueAsync(
                        JobType,
                        new PurgeAccountPayload(userId),
                        new JobEnqueueOptions(due, $"{DedupeKey(userId)}:{due.ToUnixTimeSeconds()}"),
                        cancellationToken);
                }

                return;
            }

            // A library goes only with its last member. (Nobody can join once the owner is deleted: their invites are revoked.)
            // A background job has no signed-in caller, so the library filter is off on purpose.
            var libraries = await db.LibraryMembers.IgnoreQueryFilters().AsNoTracking()
                .Where(m => m.UserId == userId && m.Role == LibraryRole.Owner)
                .Where(m => !db.LibraryMembers.IgnoreQueryFilters().Any(other => other.LibraryId == m.LibraryId && other.UserId != userId))
                .Select(m => m.LibraryId)
                .ToListAsync(cancellationToken);
            foreach (var libraryId in libraries)
            {
                await PurgeLibraryAsync(libraryId, cancellationToken);
            }

            // In libraries that carry on, the invite they used says who it was for and who accepted it. Both go; that it was used stays.
            await db.LibraryInvites.IgnoreQueryFilters()
                .Where(i => i.AcceptedBy == userId)
                .ExecuteUpdateAsync(
                    set => set.SetProperty(i => i.Name, RemovedUserName).SetProperty(i => i.AcceptedBy, (Guid?)null),
                    cancellationToken);
            await db.LibraryMembers.IgnoreQueryFilters().Where(m => m.UserId == userId).ExecuteDeleteAsync(cancellationToken);

            foreach (var step in userSteps ?? [])
            {
                await step.PurgeAsync(userId, cancellationToken);
            }

            await accounts.ScrubAuditTrailAsync(userId, cancellationToken);
            await anonymiseAudit.HandleAsync(userId, cancellationToken);
            audit.Record(AccountAuditActions.Purged, "user", null, null, new Dictionary<string, object> { ["libraries"] = libraries.Count });
            await db.SaveChangesAsync(cancellationToken);

            // Last: it empties this job's own payload, which is where the user id is kept.
            await accounts.EraseAsync(userId, cancellationToken);
        }

        /// <summary>
        /// Removes a library, its rows in the order the foreign keys allow, and its image files. Files go before the rows that name them, so
        /// a crash can leave a row without a file (harmless, and found again) but never a file nothing points to.
        /// </summary>
        private async Task PurgeLibraryAsync(Guid libraryId, CancellationToken cancellationToken)
        {
            foreach (var step in steps)
            {
                await step.PurgeAsync(libraryId, cancellationToken);
            }

            var blobPaths = await db.Assets.IgnoreQueryFilters().AsNoTracking()
                .Where(a => a.OwnerLibraryId == libraryId)
                .Select(a => a.BlobPath)
                .ToListAsync(cancellationToken);
            foreach (var blobPath in blobPaths)
            {
                await files.DeleteAsync(blobPath, cancellationToken);
            }

            // Scanning first (candidates point at snaps and items), then the items and what hangs from them.
            await db.MatchFeedback.IgnoreQueryFilters().Where(f => f.LibraryId == libraryId).ExecuteDeleteAsync(cancellationToken);
            await db.ScanCandidates.IgnoreQueryFilters().Where(c => c.LibraryId == libraryId).ExecuteDeleteAsync(cancellationToken);
            await db.ScanSnaps.IgnoreQueryFilters().Where(s => s.LibraryId == libraryId).ExecuteDeleteAsync(cancellationToken);
            await db.ScanSessions.IgnoreQueryFilters().Where(s => s.LibraryId == libraryId).ExecuteDeleteAsync(cancellationToken);
            await db.ItemTags.IgnoreQueryFilters().Where(t => t.LibraryId == libraryId).ExecuteDeleteAsync(cancellationToken);
            await db.ItemFeaturePrints.IgnoreQueryFilters().Where(p => p.LibraryId == libraryId).ExecuteDeleteAsync(cancellationToken);
            await db.Items.IgnoreQueryFilters().Where(i => i.LibraryId == libraryId).ExecuteDeleteAsync(cancellationToken);
            await db.Locations.IgnoreQueryFilters().Where(l => l.LibraryId == libraryId).ExecuteDeleteAsync(cancellationToken);
            await db.Tags.IgnoreQueryFilters().Where(t => t.LibraryId == libraryId).ExecuteDeleteAsync(cancellationToken);
            await db.LibraryInvites.IgnoreQueryFilters().Where(i => i.LibraryId == libraryId).ExecuteDeleteAsync(cancellationToken);

            // Anything shared that still shows one of the library's images forgets it before the image goes.
            await db.Works.Where(w => w.CoverAssetId != null && db.Assets.IgnoreQueryFilters().Any(a => a.Id == w.CoverAssetId && a.OwnerLibraryId == libraryId))
                .ExecuteUpdateAsync(set => set.SetProperty(w => w.CoverAssetId, (Guid?)null), cancellationToken);
            await db.Releases.IgnoreQueryFilters().Where(r => r.CoverAssetId != null && db.Assets.IgnoreQueryFilters().Any(a => a.Id == r.CoverAssetId && a.OwnerLibraryId == libraryId))
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.CoverAssetId, (Guid?)null), cancellationToken);

            await db.Releases.IgnoreQueryFilters().Where(r => r.OwnerLibraryId == libraryId).ExecuteDeleteAsync(cancellationToken);
            await db.Assets.IgnoreQueryFilters().Where(a => a.OwnerLibraryId == libraryId).ExecuteDeleteAsync(cancellationToken);

            // The library row last: its members go with it (cascade), so until now a crashed purge still knows which library it was on.
            await db.Libraries.IgnoreQueryFilters().Where(l => l.Id == libraryId).ExecuteDeleteAsync(cancellationToken);
        }
    }
}
