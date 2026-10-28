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
        ICoverStore coverStore,
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
            if (alternatives.Count == 0)
            {
                return CoverVerifyErrors.NoCovers;
            }

            var started = Stopwatch.GetTimestamp();
            var attempts = await CompareAllAsync(command.Quad, cleaned.Value.Bytes, alternatives, cancellationToken);
            var compared = attempts.Where(a => a.Comparison is not null).ToList();
            if (compared.Count == 0)
            {
                // Nothing had a cover at all, or everything that did failed transiently: only the second is worth retrying.
                return attempts.All(a => a.Failure == CoverVerification.NoCover) ? CoverVerifyErrors.NoCovers : CoverVerifyErrors.Unavailable;
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

        /// <summary>Where an alternative's cover comes from: a provider download, a stored blob, or nowhere.</summary>
        private sealed record CoverSource(string? FetchUrl, string? BlobPath, string? Missing);

        private async Task<List<Attempt>> CompareAllAsync(
            CoverQuad? quad, byte[] capture, IReadOnlyList<StoredAlternative> alternatives, CancellationToken cancellationToken)
        {
            // The sources resolve one at a time: they touch the database, which answers one caller at a time. Only the
            // downloads, reads and comparisons below run side by side.
            var sources = new List<(StoredAlternative Alternative, CoverSource Source)>(alternatives.Count);
            foreach (var alternative in alternatives)
            {
                sources.Add((alternative, await ResolveSourceAsync(alternative, cancellationToken)));
            }

            var tasks = sources.Select(s => AttemptAsync(quad, capture, s.Alternative, s.Source, cancellationToken));
            return (await Task.WhenAll(tasks)).ToList();
        }

        /// <summary>
        /// Where the alternative's cover lives: a provider cover by its URL, a catalogue release's by release id (its
        /// <c>/media</c> URL is for serving, not fetching), or nowhere.
        /// </summary>
        private async Task<CoverSource> ResolveSourceAsync(StoredAlternative alternative, CancellationToken cancellationToken)
        {
            if (IsAbsoluteHttp(alternative.CoverUrl))
            {
                return new CoverSource(alternative.CoverUrl, null, null);
            }

            var assetId = await db.Releases.Where(r => r.Id == alternative.ReleaseId).Select(r => r.CoverAssetId).SingleOrDefaultAsync(cancellationToken);
            var blobPath = assetId is null
                ? null
                : await db.Assets.Where(a => a.Id == assetId).Select(a => a.BlobPath).SingleOrDefaultAsync(cancellationToken);
            return blobPath is null ? new CoverSource(null, null, CoverVerification.NoCover) : new CoverSource(null, blobPath, null);
        }

        private async Task<Attempt> AttemptAsync(
            CoverQuad? quad, byte[] capture, StoredAlternative alternative, CoverSource source, CancellationToken cancellationToken)
        {
            var (bytes, failure) = await CoverBytesAsync(source, cancellationToken);
            if (bytes is null)
            {
                return new Attempt(alternative, null, failure!);
            }

            try
            {
                var comparison = await verifier.CompareAsync(capture, bytes, quad, cancellationToken);
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
        }

        /// <summary>
        /// The cover bytes from a resolved source. Blobs are server-written and were size-bounded when stored, so they
        /// are read whole.
        /// </summary>
        private async Task<(byte[]? Bytes, string? Failure)> CoverBytesAsync(CoverSource source, CancellationToken cancellationToken)
        {
            if (source.Missing is not null)
            {
                return (null, source.Missing);
            }

            if (source.FetchUrl is not null)
            {
                var fetched = await covers.FetchAsync(source.FetchUrl, cancellationToken);
                return fetched.Cover is null
                    ? (null, fetched.FailureReason ?? CoverFailure.FetchFailed)
                    : (fetched.Cover.Bytes.ToArray(), null);
            }

            await using var stream = await coverStore.OpenReadAsync(source.BlobPath!, cancellationToken);
            if (stream is null)
            {
                return (null, CoverFailure.FetchFailed);
            }

            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            return (buffer.ToArray(), null);
        }

        private static bool IsAbsoluteHttp(string? url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps);

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
