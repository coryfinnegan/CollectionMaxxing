using System.Text.Json;
using CollectionMaxxing.Application.Audit;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Operations;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Scanning;

/// <summary>
/// Use case: take back one auto-add, T0 or T1 (docs/16 §6). The event's items are removed <em>for good</em> (not into Recently
/// deleted), the barcode link it taught is taken back unless a standing auto-add still uses it, and the candidate goes back
/// to needing eyes with its evidence (match, score, alternatives) intact. One transaction: all of it or none of it.
/// <para>
/// The items are the ids the event recorded on the candidate (<see cref="ScanCandidate.AutoCommittedItemIds"/>), never found
/// by when or by whom they were made. Every one of those ids must still be there and untouched; if any is not, something
/// else is going on and nothing is deleted. Each removed item leaves an <see cref="ItemTombstone"/>, so a phone that already
/// synced the add is told by its next ownership sync that the items are gone.
/// </para>
/// <para>
/// Without a candidate the most recent standing auto-add goes. A session that already committed keeps its auto-adds (the
/// commit ratified them); undo the commit first to reopen the session, then the auto-adds can go one by one.
/// </para>
/// </summary>
public static class UndoAutoCommit
{
    /// <summary>The audit action recorded when an auto-add is undone (stored, so stable).</summary>
    public const string AuditAction = "scan.auto_commit_undone";

    private static readonly TimeSpan KeyLifetime = TimeSpan.FromHours(24);

    public sealed record Command(Guid SessionId, Guid? CandidateId, string? IdempotencyKey);

    public sealed class Handler(
        IAppDbContext db, ICurrentUser currentUser, TimeProvider clock, ISessionLock sessionLock, IAuditLog audit)
    {
        public async Task<Result<UndoAutoCommitResultDto>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            var userId = currentUser.UserId
                ?? throw new InvalidOperationException("UndoAutoCommit needs a signed-in caller; the endpoint requires authorization.");

            var key = command.IdempotencyKey?.Trim();
            if (string.IsNullOrEmpty(key) || key.Length > SubmitBarcodes.MaxKeyLength)
            {
                return ItemErrors.Invalid("idempotency.key_required", "Send an Idempotency-Key header of 1 to 200 characters.", "Idempotency-Key");
            }

            var now = clock.GetUtcNow();
            var storedKey = ScanSessions.IdempotencyKeyFor("undo-auto", command.SessionId, key);
            var hash = command.CandidateId?.ToString() ?? command.SessionId.ToString();
            var previous = await db.IdempotencyKeys.AsNoTracking().FirstOrDefaultAsync(k => k.UserId == userId && k.Key == storedKey, cancellationToken);
            if (previous is not null)
            {
                if (previous.ExpiresAt > now && previous.ResponseBodyJson is { } stored)
                {
                    return previous.RequestHash != hash
                        ? ItemErrors.KeyReused
                        : JsonSerializer.Deserialize<UndoAutoCommitResultDto>(stored, CandidateJson.Options) is { } again ? again : ScanningErrors.NothingToUndo;
                }

                // The key expired: forget it and look at the request afresh.
                await db.IdempotencyKeys.Where(k => k.UserId == userId && k.Key == storedKey).ExecuteDeleteAsync(cancellationToken);
            }

            await using (await sessionLock.AcquireAsync(command.SessionId, cancellationToken))
            {
                if (await ScanSessions.FindMineAsync(db, currentUser, command.SessionId, cancellationToken) is not { } session)
                {
                    return ScanningErrors.SessionNotFound;
                }

                if (session.Status != ScanSessionStatus.Open)
                {
                    return ScanningErrors.SessionClosed;
                }

                var target = command.CandidateId is { } chosen
                    ? await db.ScanCandidates.FirstOrDefaultAsync(c => c.SessionId == session.Id && c.Id == chosen, cancellationToken)
                    : await db.ScanCandidates.Where(c => c.SessionId == session.Id && c.AutoCommittedItemIds != null)
                        .OrderByDescending(c => c.Seq).FirstOrDefaultAsync(cancellationToken);
                if (target is null || target.AutoCommittedItemIds is not { Length: > 0 } expected)
                {
                    return ScanningErrors.NothingToUndo;
                }

                // Everything this event created, and only that: the ids it recorded. All of them must be here, in this library, untouched.
                var wanted = expected.ToHashSet();
                var items = await db.Items.IgnoreQueryFilters()
                    .Where(i => i.LibraryId == session.LibraryId && wanted.Contains(i.Id))
                    .ToListAsync(cancellationToken);
                if (items.Count != wanted.Count || items.Any(i => i.DeletedAt != null || i.UpdatedAt != i.CreatedAt))
                {
                    return ScanningErrors.UndoItemsChanged;
                }

                target.CreatedItemId = null; // the change counter's trigger sends the candidate again
                target.AutoCommittedItemIds = null;
                target.Decision = CandidateDecision.Undecided;
                var trace = CandidateJson.ReadTrace(target.TraceJson);
                if (trace.AutoDecision is not null)
                {
                    target.TraceJson = CandidateJson.Serialize(trace with { AutoDecision = null });
                }

                db.Items.RemoveRange(items); // the DELETE checks each row's version: an edit that lands after the check above wins and nothing is deleted
                db.ItemTombstones.AddRange(items.Select(i => new ItemTombstone { ItemId = i.Id, LibraryId = i.LibraryId, DeletedAt = now }));
                await TakeBackWhatTheEventTaughtAsync(session.Id, target, cancellationToken);
                var rest = (session.AutoCommittedItemIds ?? []).Except(wanted).ToArray();
                session.AutoCommittedItemIds = rest.Length == 0 ? null : rest;
                audit.Record(AuditAction, "scan_session", session.Id, session.LibraryId, new Dictionary<string, object> { ["items"] = items.Count });

                var result = new UndoAutoCommitResultDto(items.Count, (await CandidateProjection.ToDtosAsync(db, [target], cancellationToken))[0]);
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
                    // An item (or the session) changed while this ran: nothing was deleted.
                    return ScanningErrors.UndoItemsChanged;
                }

                return result;
            }
        }

        /// <summary>
        /// Removes the barcode link this event taught, unless another standing auto-add of the session still uses the same
        /// link (a second copy scanned in copies mode). Links from providers or from other sessions are never touched.
        /// Marks the row; the caller saves everything in one transaction.
        /// </summary>
        private async Task TakeBackWhatTheEventTaughtAsync(Guid sessionId, ScanCandidate target, CancellationToken cancellationToken)
        {
            if (target.MatchedReleaseId is not { } releaseId || !Identifier.TryParse(target.RawCode, out var code))
            {
                return;
            }

            var shared = await db.ScanCandidates.AnyAsync(
                c => c.SessionId == sessionId && c.Id != target.Id && c.AutoCommittedItemIds != null
                    && c.MatchedReleaseId == releaseId && c.RawCode == target.RawCode,
                cancellationToken);
            if (shared)
            {
                return;
            }

            db.ReleaseIdentifiers.RemoveRange(await db.ReleaseIdentifiers
                .Where(i => i.LearnedBySessionId == sessionId && i.ReleaseId == releaseId && i.Value == code.Value)
                .ToListAsync(cancellationToken));
        }
    }
}
