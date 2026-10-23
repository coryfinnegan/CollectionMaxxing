using CollectionMaxxing.Application.Accounts.Devices;
using CollectionMaxxing.Application.Audit;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Jobs;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Accounts.Deletion;

/// <summary>
/// Use case: a user deletes their own account (docs/07, "Sign out, devices and deletion"). They are soft-deleted at once: they can't sign in,
/// and every phone, refresh token, web session and open invite of theirs dies. The purge job then removes their data after 30 days
/// (<see cref="PurgeAccount"/>). The caller has signed in within the last five minutes (<c>RequireRecentSignIn</c>) before it gets here.
/// An owner whose library has other members is refused and nothing is touched.
/// </summary>
public static class DeleteAccount
{
    public sealed class Handler(
        IAppDbContext db,
        IAccountDeactivation deactivation,
        IDeviceAuthorizations devices,
        IWebSessions webSessions,
        IJobQueue jobs,
        IAuditLog audit,
        TimeProvider clock)
    {
        public async Task<Result> HandleAsync(Guid userId, CancellationToken cancellationToken)
        {
            // A library other people belong to can't go with its owner's account. Check before anything is changed.
            var hasMembers = await db.LibraryMembers.IgnoreQueryFilters().AsNoTracking()
                .Where(m => m.UserId == userId && m.Role == LibraryRole.Owner)
                .AnyAsync(
                    owned => db.LibraryMembers.IgnoreQueryFilters().Any(other => other.LibraryId == owned.LibraryId && other.UserId != userId),
                    cancellationToken);
            if (hasMembers)
            {
                return Result.Failure(AccountDeletionErrors.LibraryHasMembers);
            }

            var now = clock.GetUtcNow();

            // The purge is queued first: if anything below fails the user is still signed in and can try again, and the job
            // does nothing for an account that was never deleted. (The dedupe key makes a second try queue nothing new; if the job
            // that is already queued turns out to be early for the new date, it queues itself again, see PurgeAccount.)
            await jobs.EnqueueAsync(
                PurgeAccount.JobType,
                new PurgeAccountPayload(userId),
                new JobEnqueueOptions(now + PurgeAccount.Retention, PurgeAccount.DedupeKey(userId)),
                cancellationToken);

            // Deactivating is what shuts every way in at once: from the next request the web session and the phone's access token are
            // refused, and the Auth0 sign-in finds a deactivated account. It also checks the members again, in the same step: someone who
            // joined since the check above would otherwise be left in a library with no owner. The rest of the clean-up runs in that same
            // transaction: if any of it fails, nothing is deactivated, so the person can try again and no invite or audit row is lost.
            // If members turned up, nothing has changed except the queued purge, which does nothing for an account that was never deleted.
            var deactivated = await deactivation.DeactivateUnlessLibraryHasMembersAsync(
                userId,
                async token =>
                {
                    // Invites they sent die with them; so do their places in other people's libraries (their own library stays until the purge).
                    await db.LibraryInvites.IgnoreQueryFilters()
                        .Where(i => i.CreatedBy == userId && i.AcceptedAt == null && i.RevokedAt == null)
                        .ExecuteUpdateAsync(set => set.SetProperty(i => i.RevokedAt, now), token);
                    await db.LibraryMembers.IgnoreQueryFilters()
                        .Where(m => m.UserId == userId && m.Role != LibraryRole.Owner)
                        .ExecuteDeleteAsync(token);

                    await devices.RevokeAllAsync(userId, token);
                    await webSessions.RevokeAllAsync(userId, token);
                    audit.Record(AccountAuditActions.Deleted, "user", userId, null);
                    await db.SaveChangesAsync(token);
                },
                cancellationToken);
            if (!deactivated)
            {
                return Result.Failure(AccountDeletionErrors.LibraryHasMembers);
            }

            return Result.Success();
        }
    }
}
