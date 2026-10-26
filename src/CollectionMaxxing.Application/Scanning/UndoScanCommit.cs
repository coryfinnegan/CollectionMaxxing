using System.Text.Json;
using CollectionMaxxing.Application.Audit;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Operations;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Scanning;

/// <summary>What undoing a commit did: how many items it removed.</summary>
public sealed record UndoResultDto(int Removed);

/// <summary>
/// Use case: take back a scan commit (SCAN-1e). The items that commit created are removed <em>for good</em> (not into Recently deleted,
/// with the rows that hang from them), the candidates forget them, and the session is open again, so Review has its results back with
/// every decision, edit and copy count as they were, ready to fix and add again. One transaction: all of it or none of it.
/// <para>
/// Undo also takes back what the commit taught the catalogue, so a wrong match can be fixed and added again: the barcode links it
/// learned (<see cref="ReleaseIdentifier.LearnedBySessionId"/>) and the private release it made, if nothing else uses that release. Links
/// from providers or from earlier commits are never touched. Each removed item leaves an <see cref="ItemTombstone"/>, so a phone that
/// already synced the commit is told by its next ownership sync that the items are gone.
/// </para>
/// <para>
/// Refused with a 409, and nothing changes, when the session isn't committed, when the commit is older than
/// <see cref="ScanningOptions.UndoWindow"/>, or when any item it created has been touched since (edited, moved, tagged, deleted, given
/// another cover): permanent deletion must never lose the person's own work. A photo that became a cover at commit stays on its snap (commit
/// keeps the snap of every accepted candidate), so removing the item leaves the photo where Review expects it, and the daily clean-up,
/// which only takes photos nothing uses, leaves it alone.
/// </para>
/// <para>
/// The items are the ids the commit recorded on the session (<see cref="ScanSession.CommittedItemIds"/>), never found by when or by whom they
/// were made, so a lookalike item can't be deleted by mistake. Every one of those ids must still be there and untouched; if any is not,
/// something else is going on and nothing is deleted. Undoing twice with the same <c>Idempotency-Key</c> replays the first answer, even after
/// the person added again (the key undid one commit and never undoes another); a new key on a session that is not committed is a 409.
/// </para>
/// </summary>
public static class UndoScanCommit
{
    /// <summary>The audit action recorded when a commit is undone (stored, so stable).</summary>
    public const string AuditAction = "scan.commit_undone";

    private static readonly TimeSpan KeyLifetime = TimeSpan.FromHours(24);

    public sealed record Command(Guid SessionId, string? IdempotencyKey);

    public sealed class Handler(IAppDbContext db, ICurrentUser currentUser, TimeProvider clock, ScanningOptions options, IAuditLog audit)
    {
        public async Task<Result<UndoResultDto>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            var userId = currentUser.UserId
                ?? throw new InvalidOperationException("UndoScanCommit needs a signed-in caller; the endpoint requires authorization.");

            var key = command.IdempotencyKey?.Trim();
            if (string.IsNullOrEmpty(key) || key.Length > SubmitBarcodes.MaxKeyLength)
            {
                return ItemErrors.Invalid("idempotency.key_required", "Send an Idempotency-Key header of 1 to 200 characters.", "Idempotency-Key");
            }

            if (await ScanSessions.FindMineAsync(db, currentUser, command.SessionId, cancellationToken) is not { } session)
            {
                return ScanningErrors.SessionNotFound;
            }

            var now = clock.GetUtcNow();
            var storedKey = ScanSessions.IdempotencyKeyFor("undo", session.Id, key);
            var hash = session.Id.ToString();
            var previous = await db.IdempotencyKeys.AsNoTracking().FirstOrDefaultAsync(k => k.UserId == userId && k.Key == storedKey, cancellationToken);
            if (previous is not null)
            {
                if (previous.ExpiresAt > now && previous.ResponseBodyJson is { } stored)
                {
                    return previous.RequestHash != hash
                        ? ItemErrors.KeyReused
                        : JsonSerializer.Deserialize<UndoResultDto>(stored, CandidateJson.Options) is { } again ? again : ScanningErrors.NotCommitted;
                }

                // The key expired: forget it and look at the request afresh.
                await db.IdempotencyKeys.Where(k => k.UserId == userId && k.Key == storedKey).ExecuteDeleteAsync(cancellationToken);
            }

            if (session.Status != ScanSessionStatus.Committed || session.CommittedAt is not { } committedAt)
            {
                return ScanningErrors.NotCommitted;
            }

            if (now - committedAt > options.UndoWindow)
            {
                return ScanningErrors.UndoWindowPassed;
            }

            var candidates = await db.ScanCandidates.Where(c => c.SessionId == session.Id).ToListAsync(cancellationToken);

            // Standing auto-adds are not this commit's: each is undone on its own, and this undo leaves them (and their items) alone.
            var made = candidates.Where(c => c.CreatedItemId is not null && c.AutoCommittedItemIds is null).ToList();

            // Everything this commit created, and only that: the ids it recorded. All of them must be here, in this library, untouched.
            var expected = session.CommittedItemIds;
            if (expected is not { Length: > 0 })
            {
                return ScanningErrors.UndoItemsChanged; // nothing recorded what this commit made, so nothing can be told apart from the person's own
            }

            var wanted = expected.ToHashSet();
            var items = await db.Items.IgnoreQueryFilters()
                .Where(i => i.LibraryId == session.LibraryId && wanted.Contains(i.Id))
                .ToListAsync(cancellationToken);
            var untouched = items.All(i => i.DeletedAt == null && i.UpdatedAt == i.CreatedAt);
            if (!untouched || items.Count != wanted.Count || made.Any(c => !wanted.Contains(c.CreatedItemId!.Value)))
            {
                return ScanningErrors.UndoItemsChanged;
            }

            foreach (var candidate in made)
            {
                candidate.CreatedItemId = null; // the change counter's trigger sends each one again
            }

            db.Items.RemoveRange(items); // the DELETE checks each row's version: an edit that lands after the check above wins and nothing is deleted
            db.ItemTombstones.AddRange(items.Select(i => new ItemTombstone { ItemId = i.Id, LibraryId = i.LibraryId, DeletedAt = now }));
            await TakeBackWhatTheCommitTaughtAsync(session.Id, wanted, candidates, cancellationToken);
            session.Status = ScanSessionStatus.Open;
            session.CommittedAt = null;
            session.CommittedItemIds = null;
            audit.Record(AuditAction, "scan_session", session.Id, session.LibraryId, new Dictionary<string, object> { ["items"] = items.Count });

            var result = new UndoResultDto(items.Count);
            db.IdempotencyKeys.Add(new IdempotencyKey
            {
                UserId = userId,
                Key = storedKey,
                RequestHash = hash,
                ResponseStatus = 200,
                ResponseBodyJson = JsonSerializer.Serialize(result, CandidateJson.Options),
                CreatedAt = now,
                ExpiresAt = now + KeyLifetime,
            });

            try
            {
                await db.SaveChangesAsync(cancellationToken); // one transaction
            }
            catch (DbUpdateException)
            {
                // An item (or the session) changed while this ran, or something started using the release being removed: nothing was deleted.
                return ScanningErrors.UndoItemsChanged;
            }

            return result;
        }

        /// <summary>
        /// Removes the barcode links this session's commit learned (but never one a standing auto-add still uses), and the
        /// private releases it made that nothing else uses (no other item, feature print or scanned candidate). Marks the
        /// rows; the caller saves everything in one transaction.
        /// </summary>
        private async Task TakeBackWhatTheCommitTaughtAsync(
            Guid sessionId, HashSet<Guid> removedItemIds, List<ScanCandidate> candidates, CancellationToken cancellationToken)
        {
            var standing = candidates
                .Where(c => c.AutoCommittedItemIds is { Length: > 0 } && c.MatchedReleaseId is not null && c.RawCode is not null)
                .Select(c => (c.MatchedReleaseId!.Value, c.RawCode!))
                .ToHashSet();
            var links = await db.ReleaseIdentifiers.Where(i => i.LearnedBySessionId == sessionId).ToListAsync(cancellationToken);
            db.ReleaseIdentifiers.RemoveRange(links.Where(i => !standing.Contains((i.ReleaseId, i.Value))));

            var made = await db.Releases.IgnoreQueryFilters().Where(r => r.CreatedBySessionId == sessionId).ToListAsync(cancellationToken);
            foreach (var release in made)
            {
                var id = release.Id;
                var inUse = await db.Items.IgnoreQueryFilters().AnyAsync(i => i.ReleaseId == id && !removedItemIds.Contains(i.Id), cancellationToken)
                    || await db.ItemFeaturePrints.IgnoreQueryFilters().AnyAsync(p => p.ReleaseId == id && !removedItemIds.Contains(p.ItemId), cancellationToken)
                    || await db.ScanCandidates.IgnoreQueryFilters().AnyAsync(c => c.MatchedReleaseId == id, cancellationToken);
                if (!inUse)
                {
                    db.Releases.Remove(release);
                }
            }
        }
    }
}
