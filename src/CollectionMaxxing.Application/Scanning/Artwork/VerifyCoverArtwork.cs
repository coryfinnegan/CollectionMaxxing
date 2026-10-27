using System.Diagnostics;
using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Scanning.Photo;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Application.Scanning.Artwork;

/// <summary>
/// Use case: the phone posts a cover capture for an undecided close call, and the server compares it against each
/// alternative's cover image (docs/02, #438 stage 2). The alternatives are re-ranked by the combined text + visual
/// score; when the top cover is a clear visual match with a clear margin it is preselected for Review, which shows it
/// checked — the person can still pick another. The capture is verified and discarded, never stored.
/// Posting again re-verifies and replaces the earlier scores.
/// </summary>
/// <remarks>
/// The session lock is never held across the slow part: the candidate is snapshotted, the covers are fetched and
/// compared without the lock, and the answer lands on a row read fresh under the lock. Anything that changed meanwhile
/// (a re-resolve, a decision, a closed session) is refused instead of overwritten.
/// </remarks>
public static class VerifyCoverArtwork
{
    public sealed record Command(Guid SessionId, Guid CandidateId, ReadOnlyMemory<byte> Capture, CoverQuad? Quad);

    public sealed class Handler(
        IAppDbContext db,
        ICurrentUser currentUser,
        ISessionLock sessionLock,
        ICoverFetcher covers,
        ICoverVerifier verifier,
        IOptionsMonitor<ScoringOptions> scoring)
    {
        public async Task<Result<CandidateDto>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            if (CoverQuad.Validate(command.Quad) is { } invalid)
            {
                return invalid;
            }

            if (await ScanSessions.FindMineAsync(db, currentUser, command.SessionId, cancellationToken) is not { } session)
            {
                return ScanningErrors.SessionNotFound;
            }

            // Detached on purpose: the write below reads the row fresh under the lock, so this snapshot can be told from it.
            var snapshot = await db.ScanCandidates.AsNoTracking()
                .FirstOrDefaultAsync(c => c.SessionId == session.Id && c.Id == command.CandidateId, cancellationToken);
            if (snapshot is null)
            {
                return ScanningErrors.CandidateNotFound;
            }

            if (session.Status != ScanSessionStatus.Open)
            {
                return ScanningErrors.SessionClosed;
            }

            if (Refuse(snapshot) is { } refused)
            {
                return refused;
            }

            var cleaned = JpegSanitizer.Clean(command.Capture.Span);
            if (!cleaned.IsSuccess)
            {
                return cleaned.Error!;
            }

            var alternatives = CandidateJson.ReadAlternatives(snapshot.AlternativesJson);
            var withCovers = alternatives.Where(a => !string.IsNullOrWhiteSpace(a.CoverUrl)).ToList();
            if (withCovers.Count == 0)
            {
                return CoverVerifyErrors.NoCovers;
            }

            var started = Stopwatch.GetTimestamp();
            var attempts = await CompareAllAsync(command.Quad, cleaned.Value.Bytes, withCovers, cancellationToken);
            var compared = attempts.Where(a => a.Comparison is not null).ToList();
            if (compared.Count == 0)
            {
                return CoverVerifyErrors.Unavailable;
            }

            var options = scoring.CurrentValue.CoverVerify;
            var (ranked, preselected) = CoverVerification.Rank(
                compared.Select(c => (c.Alternative.ReleaseId, c.Alternative.Score, c.Comparison!.Inliers)).ToList(), options);
            var elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            await using (await sessionLock.AcquireAsync(session.Id, cancellationToken))
            {
                // The session may have been closed while the covers were compared.
                var status = await db.ScanSessions.AsNoTracking().Where(s => s.Id == session.Id).Select(s => s.Status).SingleAsync(cancellationToken);
                if (status != ScanSessionStatus.Open)
                {
                    return ScanningErrors.SessionClosed;
                }

                var candidate = await db.ScanCandidates
                    .FirstOrDefaultAsync(c => c.SessionId == session.Id && c.Id == command.CandidateId, cancellationToken);
                if (candidate is null)
                {
                    return ScanningErrors.CandidateNotFound;
                }

                if (Refuse(candidate) is { } changed)
                {
                    return changed;
                }

                if (candidate.AlternativesJson != snapshot.AlternativesJson)
                {
                    return CoverVerifyErrors.Changed;
                }

                WriteAnswer(candidate, alternatives, ranked, attempts, preselected, elapsed);
                await db.SaveChangesAsync(cancellationToken);
                return (await CandidateProjection.ToDtosAsync(db, [candidate], cancellationToken))[0];
            }
        }

        /// <summary>Why the row cannot be verified, or null when it can: decided, auto-added, settled, or no close call.</summary>
        private static DomainError? Refuse(ScanCandidate candidate)
        {
            if (candidate.AutoCommittedItemIds is { Length: > 0 })
            {
                return ScanningErrors.AutoCommitted;
            }

            var trace = CandidateJson.ReadTrace(candidate.TraceJson);
            var alternatives = CandidateJson.ReadAlternatives(candidate.AlternativesJson);
            if (ArtworkLinks.IsDecidedByPerson(candidate, alternatives, trace))
            {
                return ArtworkErrors.Decided;
            }

            if (candidate.Resolution != CandidateResolution.Ambiguous)
            {
                return CoverVerifyErrors.NotNeeded;
            }

            // A close call the server flagged for artwork (a spine never is: it has no cover to compare), or one
            // already cover-verified and being corrected. Anything else was settled without pictures.
            var alreadyCompared = trace.Steps.Any(s => s.Provider == CoverVerification.StepName);
            return trace.NeedsArt || alreadyCompared ? null : CoverVerifyErrors.NotNeeded;
        }

        private sealed record Attempt(StoredAlternative Alternative, CoverComparison? Comparison, string? Failure);

        private async Task<List<Attempt>> CompareAllAsync(
            CoverQuad? quad, byte[] capture, List<StoredAlternative> withCovers, CancellationToken cancellationToken)
        {
            var tasks = withCovers.Select(async alternative =>
            {
                var fetched = await covers.FetchAsync(alternative.CoverUrl!, cancellationToken);
                if (fetched.Cover is null)
                {
                    return new Attempt(alternative, null, fetched.FailureReason ?? CoverFailure.FetchFailed);
                }

                try
                {
                    var comparison = await verifier.CompareAsync(capture, fetched.Cover.Bytes, quad, cancellationToken);
                    return new Attempt(alternative, comparison, null);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
#pragma warning disable CA1031 // One unreadable cover must not fail the verification of the rest.
                catch (Exception)
#pragma warning restore CA1031
                {
                    return new Attempt(alternative, null, CoverVerification.CompareFailed);
                }
            });
            return (await Task.WhenAll(tasks)).ToList();
        }

        private static void WriteAnswer(
            ScanCandidate candidate,
            IReadOnlyList<StoredAlternative> alternatives,
            IReadOnlyList<CoverVerification.RankedCover> ranked,
            IReadOnlyList<Attempt> attempts,
            Guid? preselected,
            long elapsedMs)
        {
            var failures = attempts.Where(a => a.Comparison is null).ToDictionary(a => a.Alternative.ReleaseId, a => a.Failure!);
            var scores = ranked.ToDictionary(r => r.ReleaseId);

            // Compared covers best first, then whatever could not be compared in its old order; every entry keeps its
            // text score, and every alternative gets a score row so a wrong pick can be diagnosed later.
            var reordered = ranked.Select(r => alternatives.First(a => a.ReleaseId == r.ReleaseId))
                .Concat(alternatives.Where(a => !scores.ContainsKey(a.ReleaseId)))
                .ToList();
            candidate.AlternativesJson = CandidateJson.Serialize(reordered);

            var coverScores = reordered.Select(a =>
                scores.TryGetValue(a.ReleaseId, out var r)
                    ? new CoverScore(a.ReleaseId, r.Inliers, r.Visual, r.Combined, null)
                    : new CoverScore(a.ReleaseId, null, null, null, failures.GetValueOrDefault(a.ReleaseId, CoverVerification.NoCover))).ToList();

            var trace = CandidateJson.ReadTrace(candidate.TraceJson);
            var steps = trace.Steps.Where(s => s.Provider != CoverVerification.StepName).ToList();
            steps.Add(new TraceStep(
                CoverVerification.StepName, preselected is null ? "ranked" : "preselected", elapsedMs, ranked[0].Combined));
            candidate.TraceJson = CandidateJson.Serialize(trace with
            {
                Steps = steps,
                CoverScores = coverScores,
                CoverPick = preselected is { } pick ? new StoredCoverPick(pick, CoverVerification.PreselectReason) : null,
            });
        }
    }
}
