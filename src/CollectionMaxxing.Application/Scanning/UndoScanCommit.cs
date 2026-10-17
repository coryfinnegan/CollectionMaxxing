using System.Text.Json;
using CollectionMaxxing.Application.Audit;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Domain.Common;
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
/// Refused with a 409, and nothing changes, when the session isn't committed, when the commit is older than
/// <see cref="ScanningOptions.UndoWindow"/>, or when any item it created has been touched since (edited, moved, tagged, deleted, given
/// another cover): permanent deletion must never lose the person's own work. A photo that became a cover at commit stays on its snap (commit
/// keeps the snap of every accepted candidate), so removing the item leaves the photo where Review expects it, and the daily clean-up,
/// which only takes photos nothing uses, leaves it alone.
/// </para>
/// <para>
/// The items are found by the commit itself: every one was created at the session's <c>committed_at</c>, in its library, by its starter,
/// and the number must equal the copies the candidates say they made. Any difference means something else is going on, so nothing is deleted.
/// Undoing twice with the same <c>Idempotency-Key</c> replays the first answer; a new key on a session that is no longer committed is a 409.
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
                if (previous.ExpiresAt > now && previous.ResponseBodyJson is { } stored && session.Status == ScanSessionStatus.Open)
                {
                    return previous.RequestHash != hash
                        ? ItemErrors.KeyReused
                        : JsonSerializer.Deserialize<UndoResultDto>(stored, CandidateJson.Options) is { } again ? again : ScanningErrors.NotCommitted;
                }

                // An answer that no longer fits (the key expired, or the session was added again since): forget it and look at the request afresh.
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
            var made = candidates.Where(c => c.CreatedItemId is not null).ToList();

            // Everything this commit created, and only that: the session's library, its starter, the moment of the commit.
            var items = await db.Items.IgnoreQueryFilters()
                .Where(i => i.LibraryId == session.LibraryId && i.CreatedBy == userId && i.CreatedAt == committedAt)
                .ToListAsync(cancellationToken);
            var ids = items.Select(i => i.Id).ToHashSet();
            var untouched = items.All(i => i.DeletedAt == null && i.UpdatedAt == i.CreatedAt);
            if (!untouched || items.Count != made.Sum(c => c.Copies) || made.Any(c => !ids.Contains(c.CreatedItemId!.Value)))
            {
                return ScanningErrors.UndoItemsChanged;
            }

            foreach (var candidate in made)
            {
                candidate.CreatedItemId = null; // the change counter's trigger sends each one again
            }

            db.Items.RemoveRange(items); // the DELETE checks each row's version: an edit that lands after the check above wins and nothing is deleted
            session.Status = ScanSessionStatus.Open;
            session.CommittedAt = null;
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
            catch (DbUpdateConcurrencyException)
            {
                return ScanningErrors.UndoItemsChanged; // an item (or the session) changed while this ran: nothing was deleted
            }

            return result;
        }
    }
}
