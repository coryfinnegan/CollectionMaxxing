using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Application.Scanning.Artwork;

/// <summary>Errors of the artwork re-scoring that the endpoint maps to their own status.</summary>
public static class ArtworkErrors
{
    /// <summary>A distance names a release that is not one of the candidate's own alternatives (or one the caller cannot see). The endpoint answers 422.</summary>
    public const string ReleaseNotAnOptionCode = "scan.art_release_not_an_option";

    public static readonly DomainError ReleaseNotAnOption = ScanningErrors.Invalid(
        ReleaseNotAnOptionCode, "Distances can only be reported for this item's own alternatives.", "distances");

    public static readonly DomainError Decided = new(
        ErrorKind.Conflict, "scan.art_candidate_decided", "This item has been decided already, so its artwork is not compared any more.");

    public static readonly DomainError NotNeeded = new(
        ErrorKind.Conflict, "scan.art_not_needed", "This item is not a close call, so there is no artwork to compare.");
}

/// <summary>A distance as the phone reported it. Every field is a claim until <see cref="ScoreCandidateArtwork"/> has checked it.</summary>
public sealed record ReportedDistance(Guid? ReleaseId, double? Distance);

/// <summary>
/// Use case: the phone compared the artwork of a close call's alternatives with the item and reports the feature-print
/// distances (docs/02, "Artwork comparison with Apple feature prints"; docs/04 <c>:art</c>). The server re-scores the item
/// with the art term and answers with the updated candidate.
/// </summary>
/// <remarks>
/// <para>
/// The numbers come from a client, so they can only <em>refine</em> a choice the catalogue already offered: they are accepted
/// for the candidate's own alternatives only (never a release the phone names), each must be a finite number within the
/// configured range, and the scorer's own guards still apply (a platform or title-year conflict caps a score below Matched,
/// and Matched needs two compared signals). Posting again replaces the earlier distances; they are not added up.
/// A person's own decision, a closed session and an item that is not a close call are all left alone (409).
/// </para>
/// <para>
/// The art term can lower scores as well as raise them. If it would leave nothing above the Ambiguous threshold, the item stays
/// Ambiguous with the same alternatives, so a poor artwork match never takes the choice away from the person.
/// </para>
/// </remarks>
public static class ScoreCandidateArtwork
{
    /// <summary>The trace step that marks a candidate as compared by artwork.</summary>
    public const string StepName = "artwork";

    /// <summary>The most distances one request may carry: the candidate has at most three alternatives, with room to spare.</summary>
    public const int MaxDistances = 5;

    public sealed record Command(Guid SessionId, Guid CandidateId, IReadOnlyList<ReportedDistance?>? Distances);

    public sealed class Handler(
        IAppDbContext db,
        ICurrentUser currentUser,
        ISessionLock sessionLock,
        CandidateResolver resolver,
        IOptionsMonitor<ScoringOptions> scoring)
    {
        public async Task<Result<CandidateDto>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            var options = scoring.CurrentValue;
            if (Validate(command.Distances, options.Art) is { } invalid)
            {
                return invalid;
            }

            if (await ScanSessions.FindMineAsync(db, currentUser, command.SessionId, cancellationToken) is not { } session)
            {
                return ScanningErrors.SessionNotFound;
            }

            // One writer per session: the load, the re-score and the sequence number below must not interleave with another request.
            await using (await sessionLock.AcquireAsync(session.Id, cancellationToken))
            {
                var candidate = await db.ScanCandidates.FirstOrDefaultAsync(
                    c => c.SessionId == session.Id && c.Id == command.CandidateId, cancellationToken);
                if (candidate is null)
                {
                    return ScanningErrors.CandidateNotFound;
                }

                // The session may have been closed while this request waited for the lock.
                var status = await db.ScanSessions.AsNoTracking().Where(s => s.Id == session.Id).Select(s => s.Status).SingleAsync(cancellationToken);
                if (status != ScanSessionStatus.Open)
                {
                    return ScanningErrors.SessionClosed;
                }

                var alternatives = CandidateJson.ReadAlternatives(candidate.AlternativesJson);
                var trace = CandidateJson.ReadTrace(candidate.TraceJson);
                if (ArtworkLinks.IsDecidedByPerson(candidate, alternatives, trace))
                {
                    return ArtworkErrors.Decided;
                }

                var reported = command.Distances!.ToDictionary(d => d!.ReleaseId!.Value, d => d!.Distance!.Value);
                var pool = await PoolAsync(alternatives, cancellationToken);
                if (reported.Keys.Any(id => !pool.ContainsKey(id)))
                {
                    return ArtworkErrors.ReleaseNotAnOption;
                }

                // Only an open close call (or one already compared, being corrected) is scored; a barcode is never.
                var alreadyCompared = trace.Steps.Any(s => s.Provider == StepName);
                if (candidate.Source == CandidateSource.Barcode || !(trace.NeedsArt || alreadyCompared))
                {
                    return ArtworkErrors.NotNeeded;
                }

                var (facts, profile) = StoredRead.From(candidate);
                var read = facts.ToReadItem() with
                {
                    ArtDistances = reported.ToDictionary(r => r.Key.ToString(), r => r.Value * options.Art.DistanceScale, StringComparer.Ordinal),
                };
                var result = MatchScorer.Evaluate(
                    read, alternatives.Where(a => pool.ContainsKey(a.ReleaseId)).Select(a => pool[a.ReleaseId]).ToList(), options.Profile(profile));

                // Never turn a close call into "nothing found": the person can still choose among the same releases.
                var verdict = result.Outcome == MatchOutcome.Matched ? ResolutionVerdict.Matched : ResolutionVerdict.Ambiguous;
                var ranked = result.Ranked.Take(3).ToList();
                var steps = trace.Steps.Where(s => s.Provider != StepName).ToList();
                steps.Add(new TraceStep(StepName, verdict.ToString().ToLowerInvariant(), 0, ranked[0].Score));
                var resolution = new BarcodeResolution(
                    verdict, ranked.Select(r => new ResolutionOption(Guid.Parse(r.Release.Key), null, r.Score)).ToList(), null, steps);

                // A decision the server made by itself is remade with the new score; the person's own decision was refused above.
                if (candidate.Decision != CandidateDecision.Undecided)
                {
                    candidate.Decision = CandidateDecision.Undecided;
                }

                await resolver.ApplyAsync(session.LibraryId, candidate, resolution, cancellationToken);
                candidate.ArtDistance = reported.TryGetValue(Guid.Parse(ranked[0].Release.Key), out var leader) ? leader : null;

                // The update moves the candidate's change counter (a database trigger), so ?since= and a reconnect both see it.
                // Its seq stays the creation order: that is how a reconnecting client is told "updated" from "added".
                await db.SaveChangesAsync(cancellationToken);
                return (await CandidateProjection.ToDtosAsync(db, [candidate], cancellationToken))[0];
            }
        }

        /// <summary>The candidate's alternatives as scorer candidates, from the catalogue, for the releases the caller may still see.</summary>
        private async Task<Dictionary<Guid, CandidateRelease>> PoolAsync(IReadOnlyList<StoredAlternative> alternatives, CancellationToken cancellationToken)
        {
            var ids = alternatives.Select(a => a.ReleaseId).Distinct().ToList();
            if (ids.Count == 0)
            {
                return [];
            }

            var rows = await (from r in db.Releases.AsNoTracking()
                              join w in db.Works.AsNoTracking() on r.WorkId equals w.Id
                              where ids.Contains(r.Id)
                              select new { Release = r, Work = w }).ToListAsync(cancellationToken);
            var platformIds = rows.Select(x => x.Release.PlatformId).OfType<Guid>().Distinct().ToList();
            var platforms = platformIds.Count == 0
                ? []
                : await db.Platforms.AsNoTracking().Where(p => platformIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);

            // The same shape the text search scores (TextSearchResolver.FindLocalAsync), so a re-score compares like with like.
            return rows.ToDictionary(
                x => x.Release.Id,
                x => new CandidateRelease
                {
                    Key = x.Release.Id.ToString(),
                    Title = x.Work.Title,
                    Kind = x.Work.Kind,
                    Format = x.Release.Format,
                    Platform = x.Release.PlatformId is { } platformId && platforms.TryGetValue(platformId, out var platform) ? platform.Name : null,
                    Year = x.Release.ReleaseDate?.Year ?? x.Work.OriginalYear,
                    Creators = ItemProjection.Creators(x.Work.CreatorsJson),
                    Edition = x.Release.Edition,
                });
        }

        private static DomainError? Validate(IReadOnlyList<ReportedDistance?>? distances, ArtScoringOptions limits)
        {
            if (distances is null || distances.Count == 0)
            {
                return ScanningErrors.Invalid("scan.art_distances_required", "Send the distances you measured.", "distances");
            }

            if (distances.Count > MaxDistances)
            {
                return ScanningErrors.Invalid("scan.art_too_many_distances", $"Send at most {MaxDistances} distances.", "distances");
            }

            var seen = new HashSet<Guid>();
            for (var i = 0; i < distances.Count; i++)
            {
                var field = $"distances[{i}]";
                if (distances[i] is not { } entry)
                {
                    return ScanningErrors.Invalid("scan.art_distance_invalid", "Each distance needs a releaseId and a distance.", field);
                }

                if (entry.ReleaseId is not { } releaseId || releaseId == Guid.Empty)
                {
                    return ScanningErrors.Invalid("scan.art_distance_invalid", "Each distance needs a releaseId.", field + ".releaseId");
                }

                if (entry.Distance is not { } distance || !double.IsFinite(distance) || distance < 0 || distance > limits.MaxDistance)
                {
                    return ScanningErrors.Invalid(
                        "scan.art_distance_invalid", $"A distance must be a number from 0 to {limits.MaxDistance:0.###}.", field + ".distance");
                }

                if (!seen.Add(releaseId))
                {
                    return ScanningErrors.Invalid("scan.art_distance_duplicate", "Send one distance per release.", field + ".releaseId");
                }
            }

            return null;
        }
    }
}
