using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Application.Jobs;
using CollectionMaxxing.Application.Scanning.Quota;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Domain.Operations;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CollectionMaxxing.Application.Scanning;

/// <summary>One code as the phone sent it.</summary>
public sealed record SubmittedCode(string? Value, string? Symbology, int? Copies);

/// <summary>
/// Use case: the phone posts a batch of barcodes into a scan session (docs/04, "Example: barcode batch"). Each code becomes a
/// candidate, resolved best-effort within a time budget; what is still waiting is left Pending and handed to a background
/// job. Safe to retry and safe to repeat: a code is one candidate per session whatever key it arrives under, and the same
/// <c>Idempotency-Key</c> with the same batch gets the same candidates back.
/// </summary>
public static class SubmitBarcodes
{
    public const int MaxKeyLength = 200;
    public const int MaxSymbologyLength = 32;
    public const int MaxRawCodeLength = 64;
    private static readonly TimeSpan KeyLifetime = TimeSpan.FromHours(24);

    public sealed record Command(Guid SessionId, string? IdempotencyKey, IReadOnlyList<SubmittedCode>? Codes);

    /// <summary>The job that resolves a code a request ran out of time for. Carries ids only; the code is read from the candidate.</summary>
    public const string ResolveJobType = "scan.resolve_candidate";

    private static readonly TimeSpan SafetyMargin = TimeSpan.FromSeconds(30);

    public sealed record ResolveJob(Guid LibraryId, Guid SessionId, Guid CandidateId, Guid UserId);

    public sealed class Handler(
        IAppDbContext db,
        IIdGenerator ids,
        ICurrentUser currentUser,
        TimeProvider clock,
        ISessionLock sessionLock,
        BarcodeResolver external,
        CandidateResolver resolver,
        IJobQueue jobs,
        ScanningOptions options,
        ILogger<Handler> logger)
    {
        public async Task<Result<CandidatesDto>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            var userId = currentUser.UserId
                ?? throw new InvalidOperationException("SubmitBarcodes needs a signed-in caller; the endpoint requires authorization.");

            var invalid = Validate(command);
            if (invalid is not null)
            {
                return invalid;
            }

            var session = await ScanSessions.FindMineAsync(db, currentUser, command.SessionId, cancellationToken);
            if (session is null)
            {
                return ScanningErrors.SessionNotFound;
            }

            var key = ScanSessions.IdempotencyKeyFor("barcodes", session.Id, command.IdempotencyKey!.Trim());
            var lines = Normalise(command.Codes!);
            var hash = RequestHash(session.Id, lines);
            var now = clock.GetUtcNow();

            // Claim the rows under the session's lock, so a retry or a twin of this request finds them instead of racing them.
            List<ScanCandidate> candidates;
            List<ScanCandidate> fresh;
            await using (await sessionLock.AcquireAsync(session.Id, cancellationToken))
            {
                var previous = await db.IdempotencyKeys.FirstOrDefaultAsync(k => k.UserId == userId && k.Key == key, cancellationToken);
                if (previous is not null)
                {
                    if (previous.ExpiresAt > now)
                    {
                        return await ReplayAsync(previous, hash, session, userId, cancellationToken);
                    }

                    db.IdempotencyKeys.Remove(previous); // expired: the key can be used again
                }

                // After the replay, so a retry that arrives once the session has closed still gets the original answer.
                if (session.Status != ScanSessionStatus.Open)
                {
                    return ScanningErrors.SessionClosed;
                }

                var lineKeys = lines.Select(l => l.Key).ToList();
                var existing = await db.ScanCandidates
                    .Where(c => c.SessionId == session.Id && c.IdempotencyKey != null && lineKeys.Contains(c.IdempotencyKey))
                    .ToDictionaryAsync(c => c.IdempotencyKey!, cancellationToken);
                var nextSeq = (await db.ScanCandidates.Where(c => c.SessionId == session.Id).MaxAsync(c => (int?)c.Seq, cancellationToken) ?? 0) + 1;

                fresh = [];
                candidates = [];
                foreach (var line in lines)
                {
                    if (!existing.TryGetValue(line.Key, out var candidate))
                    {
                        candidate = new ScanCandidate
                        {
                            Id = ids.NewId(),
                            LibraryId = session.LibraryId,
                            SessionId = session.Id,
                            Seq = nextSeq++,
                            Source = CandidateSource.Barcode,
                            RawCode = line.RawCode,
                            Symbology = line.Symbology,
                            Resolution = line.Identifier is null ? CandidateResolution.Error : CandidateResolution.Pending,
                            Decision = CandidateDecision.Undecided,
                            Copies = line.Copies,
                            IdempotencyKey = line.Key,
                            CreatedAt = now,
                            TraceJson = line.Identifier is null
                                ? CandidateJson.Serialize(new StoredTrace([new TraceStep("scanner", "invalid_code", 0)], []))
                                : null,
                        };
                        existing[line.Key] = candidate;
                        db.ScanCandidates.Add(candidate);
                        fresh.Add(candidate);
                    }

                    candidates.Add(candidate);
                }

                db.IdempotencyKeys.Add(new IdempotencyKey
                {
                    UserId = userId,
                    Key = key,
                    RequestHash = hash,
                    ResponseStatus = 200,
                    ResponseBodyJson = JsonSerializer.Serialize(candidates.Select(c => c.Id).Distinct().ToList()),
                    CreatedAt = now,
                    ExpiresAt = now + KeyLifetime,
                });
                await db.SaveChangesAsync(cancellationToken);

                // A safety net queued before the slow lookups, due once this request's budget has long passed: if the request is cut
                // off (a dropped connection, a crash) the rows still get resolved. It is a no-op for anything already resolved.
                await EnqueueAsync(
                    session, fresh.Where(c => c.Resolution == CandidateResolution.Pending), userId, ":safety",
                    clock.GetUtcNow() + options.ResolveBudget + SafetyMargin, cancellationToken);
            }

            await ResolveAsync(session, fresh, userId, cancellationToken);
            return new CandidatesDto(await CandidateProjection.ToDtosAsync(db, candidates.DistinctBy(c => c.Id).ToList(), cancellationToken));
        }

        private async Task ResolveAsync(ScanSession session, List<ScanCandidate> fresh, Guid userId, CancellationToken cancellationToken)
        {
            var toResolve = fresh.Where(c => c.Resolution == CandidateResolution.Pending).ToList();
            if (toResolve.Count == 0)
            {
                return;
            }

            var identifiers = toResolve.ToDictionary(c => c.Id, c => Identifier.TryParse(c.RawCode, out var id) ? id : null);
            var local = await resolver.FindLocalAsync(identifiers.Values.OfType<Identifier>().ToList(), cancellationToken);

            var answers = new Dictionary<Guid, BarcodeResolution>();
            var outside = new List<ScanCandidate>();
            foreach (var candidate in toResolve)
            {
                if (local.TryGetValue(identifiers[candidate.Id]!.Value, out var releaseIds))
                {
                    answers[candidate.Id] = CandidateResolver.LocalResolution(releaseIds);
                }
                else
                {
                    outside.Add(candidate);
                }
            }

            foreach (var (id, answer) in await ResolveOutsideAsync(outside, identifiers, options.ResolveBudget, cancellationToken))
            {
                answers[id] = answer;
            }

            foreach (var candidate in toResolve)
            {
                await resolver.ApplyAsync(session.LibraryId, candidate, answers[candidate.Id], cancellationToken);
            }

            await db.SaveChangesAsync(cancellationToken);

            foreach (var candidate in toResolve.Where(c => c.Resolution == CandidateResolution.Pending))
            {
                // A code that found the UPC allowance spent waits for the next reset instead of being tried (and given up on) now.
                var waitForQuota = UpcQuotaWindow.IsSpent(answers[candidate.Id]);
                var runAfter = waitForQuota ? UpcQuotaWindow.NextResetAfter(clock.GetUtcNow()) : (DateTimeOffset?)null;
                await jobs.EnqueueAsync(
                    ResolveJobType,
                    new ResolveJob(session.LibraryId, session.Id, candidate.Id, userId),
                    new JobEnqueueOptions(runAfter, waitForQuota ? UpcQuotaWindow.DedupeKey(candidate.Id, runAfter!.Value) : $"{ResolveJobType}:{candidate.Id}"),
                    cancellationToken);
            }
        }

        /// <summary>Asks the providers for several codes at once, within the budget. A code the budget runs out on comes back Pending.</summary>
        private async Task<Dictionary<Guid, BarcodeResolution>> ResolveOutsideAsync(
            List<ScanCandidate> candidates, Dictionary<Guid, Identifier?> identifiers, TimeSpan budget, CancellationToken cancellationToken)
        {
            var results = new Dictionary<Guid, BarcodeResolution>();
            if (candidates.Count == 0)
            {
                return results;
            }

            using var failFast = CollectionMaxxing.Application.Catalog.Providers.ProviderCalls.FailFast(); // a person is waiting
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(budget);
            using var gate = new SemaphoreSlim(Math.Max(1, options.Parallelism));

            var tasks = candidates.Select(async candidate =>
            {
                try
                {
                    await gate.WaitAsync(timeout.Token);
                    try
                    {
                        return (candidate.Id, Answer: await external.ResolveAsync(identifiers[candidate.Id]!, timeout.Token));
                    }
                    finally
                    {
                        gate.Release();
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return (candidate.Id, Answer: BarcodeResolution.Pending([new TraceStep("budget", "timeout", (long)budget.TotalMilliseconds)]));
                }
#pragma warning disable CA1031 // Whatever goes wrong with one code must not fail the batch.
                catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    logger.LogWarning("Resolving a code failed ({Failure}); leaving it pending.", exception.GetType().Name);
                    return (candidate.Id, Answer: BarcodeResolution.Pending([new TraceStep("resolver", "failed", 0)]));
                }
            }).ToList();

            foreach (var (id, answer) in await Task.WhenAll(tasks))
            {
                results[id] = answer;
            }

            return results;
        }

        /// <summary>Queues the resolve job for each candidate given. Safe to repeat: one job per candidate and key.</summary>
        private async Task EnqueueAsync(
            ScanSession session, IEnumerable<ScanCandidate> candidates, Guid userId, string keySuffix, DateTimeOffset? runAfter, CancellationToken cancellationToken)
        {
            foreach (var candidate in candidates)
            {
                await jobs.EnqueueAsync(
                    ResolveJobType,
                    new ResolveJob(session.LibraryId, session.Id, candidate.Id, userId),
                    new JobEnqueueOptions(runAfter, $"{ResolveJobType}:{candidate.Id}{keySuffix}"),
                    cancellationToken);
            }
        }

        private async Task<Result<CandidatesDto>> ReplayAsync(IdempotencyKey previous, string hash, ScanSession session, Guid userId, CancellationToken cancellationToken)
        {
            if (previous.RequestHash != hash)
            {
                return ItemErrors.KeyReused;
            }

            var sessionId = session.Id;
            var candidateIds = JsonSerializer.Deserialize<List<Guid>>(previous.ResponseBodyJson ?? "[]") ?? [];
            var found = await db.ScanCandidates.AsNoTracking()
                .Where(c => c.SessionId == sessionId && candidateIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, cancellationToken);
            var ordered = candidateIds.Where(found.ContainsKey).Select(id => found[id]).ToList();

            // A first attempt that was cut off may have left rows with no job; queueing again is safe (deduped per candidate).
            await EnqueueAsync(session, ordered.Where(c => c.Resolution == CandidateResolution.Pending), userId, "", null, cancellationToken);
            return new CandidatesDto(await CandidateProjection.ToDtosAsync(db, ordered, cancellationToken));
        }

        private static DomainError? Validate(Command command)
        {
            var key = command.IdempotencyKey?.Trim();
            if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength)
            {
                return ItemErrors.Invalid("idempotency.key_required", "Send an Idempotency-Key header of 1 to 200 characters.", "Idempotency-Key");
            }

            if (command.Codes is not { Count: > 0 })
            {
                return ScanningErrors.Invalid("scan.codes_required", "Send at least one code.", "codes");
            }

            if (command.Codes.Count > ScanningOptions.MaxCodesPerBatch)
            {
                return ScanningErrors.Invalid(
                    "scan.too_many_codes", $"Send at most {ScanningOptions.MaxCodesPerBatch} codes at a time.", "codes");
            }

            return command.Codes.Any(c => c is { Copies: { } copies } && (copies < 1 || copies > ScanningOptions.MaxCopies))
                ? ScanningErrors.Invalid("scan.copies_invalid", $"Copies must be from 1 to {ScanningOptions.MaxCopies}.", "codes")
                : null;
        }

        /// <summary>
        /// Reads each code: a valid barcode is keyed by its normalised digits, so a UPC-A and its EAN-13 are one product; anything
        /// else is kept as an error line, keyed by a hash so repeating it is still one candidate. The same product twice in a
        /// batch becomes one line with the copies added up.
        /// </summary>
        private static List<Line> Normalise(IReadOnlyList<SubmittedCode> codes)
        {
            var lines = new List<Line>();
            foreach (var code in codes)
            {
                var copies = code?.Copies ?? 1;
                var symbology = code?.Symbology is { } s ? Truncate(s.Trim(), MaxSymbologyLength) : null;
                var text = code?.Value?.Trim() ?? string.Empty;

                Line line;
                if (Identifier.TryParse(text, out var id))
                {
                    line = new Line($"barcode:{id.Value}", id.Value, id, symbology, copies);
                }
                else
                {
                    var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
                    line = new Line($"invalid:{digest}", Truncate(text, MaxRawCodeLength), null, symbology, copies);
                }

                var index = lines.FindIndex(l => l.Key == line.Key);
                if (index < 0)
                {
                    lines.Add(line);
                }
                else
                {
                    lines[index] = lines[index] with { Copies = Math.Min(ScanningOptions.MaxCopies, lines[index].Copies + line.Copies) };
                }
            }

            return lines;
        }

        private static string RequestHash(Guid sessionId, List<Line> lines)
        {
            var text = sessionId + "|" + string.Join(';', lines.Select(l => $"{l.Key}*{l.Copies}"));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        }

        private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];

        private sealed record Line(string Key, string RawCode, Identifier? Identifier, string? Symbology, int Copies);
    }
}
