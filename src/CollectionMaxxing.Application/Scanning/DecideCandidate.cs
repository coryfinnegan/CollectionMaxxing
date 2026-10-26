using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Scanning.Learning;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Scanning;

/// <summary>
/// Use case: the person decides about one scanned item: accept it, reject it, pick one of the top three matches, or change
/// how many copies. Nothing is added to the library until the session is committed. An exactly owned release is skipped
/// unless the person accepts it themselves ("Add another copy").
/// </summary>
public static class DecideCandidate
{
    /// <summary>The longest platform or edition a person can type over what was matched.</summary>
    public const int MaxEditLength = 100;

    public sealed record Command(
        Guid SessionId,
        Guid CandidateId,
        CandidateDecision? Decision,
        Guid? ReleaseId,
        int? Copies,
        ReleaseFormat? Format = null,
        string? Platform = null,
        string? Edition = null);

    public sealed class Handler(IAppDbContext db, ICurrentUser currentUser, CandidateResolver resolver, MatchFeedbackRecorder feedback)
    {
        public async Task<Result<CandidateDto>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            if (command is { Decision: null, ReleaseId: null, Copies: null, Format: null, Platform: null, Edition: null })
            {
                return ScanningErrors.Invalid(
                    "scan.nothing_to_change", "Say what to change: decision, releaseId, copies, format, platform or edition.", "decision");
            }

            if (command.Copies is { } copies && (copies < 1 || copies > ScanningOptions.MaxCopies))
            {
                return ScanningErrors.Invalid("scan.copies_invalid", $"Copies must be from 1 to {ScanningOptions.MaxCopies}.", "copies");
            }

            if (command.Platform?.Trim().Length > MaxEditLength)
            {
                return ScanningErrors.Invalid("scan.platform_too_long", $"A platform can be at most {MaxEditLength} characters.", "platform");
            }

            if (command.Edition?.Trim().Length > MaxEditLength)
            {
                return ScanningErrors.Invalid("scan.edition_too_long", $"An edition can be at most {MaxEditLength} characters.", "edition");
            }

            if (await ScanSessions.FindMineAsync(db, currentUser, command.SessionId, cancellationToken) is not { } session)
            {
                return ScanningErrors.SessionNotFound;
            }

            var candidate = await db.ScanCandidates.FirstOrDefaultAsync(
                c => c.SessionId == session.Id && c.Id == command.CandidateId, cancellationToken);
            if (candidate is null)
            {
                return ScanningErrors.CandidateNotFound;
            }

            if (session.Status != ScanSessionStatus.Open)
            {
                return ScanningErrors.SessionClosed;
            }

            if (command.ReleaseId is { } releaseId)
            {
                var picked = await PickAsync(session.LibraryId, candidate, releaseId, command.Decision is not null, cancellationToken);
                if (picked is not null)
                {
                    return picked;
                }
            }

            if (command.Decision is { } decision)
            {
                if (decision == CandidateDecision.Accepted && candidate.MatchedReleaseId is null)
                {
                    return ScanningErrors.Invalid("scan.pick_first", "Pick one of the matches before accepting this item.", "decision");
                }

                candidate.Decision = decision;

                // An explicit decision is the person's even when the value is the one the server chose by itself: forget the
                // server's own pick, so artwork (SCAN-2b) no longer treats the item as one it may correct.
                var trace = CandidateJson.ReadTrace(candidate.TraceJson);
                if (trace.AutoDecision is not null)
                {
                    candidate.TraceJson = CandidateJson.Serialize(trace with { AutoDecision = null });
                }
            }

            if (command.Copies is { } newCopies)
            {
                candidate.Copies = newCopies;
            }

            // The person's corrections stay on the candidate (a blank text clears one); commit adds the release that has them, and a
            // search again scores with them. A match the person corrected is no longer just the scanner's guess.
            if (command.Format is { } format)
            {
                candidate.EditedFormat = format;
                candidate.UserEdited = true;
            }

            if (command.Platform is { } platform)
            {
                candidate.EditedPlatform = string.IsNullOrWhiteSpace(platform) ? null : platform.Trim();
                candidate.UserEdited = true;
            }

            if (command.Edition is { } edition)
            {
                candidate.EditedEdition = string.IsNullOrWhiteSpace(edition) ? null : edition.Trim();
                candidate.UserEdited = true;
            }

            await db.SaveChangesAsync(cancellationToken);
            return (await CandidateProjection.ToDtosAsync(db, [candidate], cancellationToken))[0];
        }

        /// <summary>
        /// Points the candidate at one of its own options. Only the matched release or one of the top three is allowed, so a
        /// session can't be used to attach an arbitrary release. Returns an error, or null when it worked.
        /// </summary>
        private async Task<DomainError?> PickAsync(
            Guid libraryId, ScanCandidate candidate, Guid releaseId, bool decisionGiven, CancellationToken cancellationToken)
        {
            var alternatives = CandidateJson.ReadAlternatives(candidate.AlternativesJson);
            var chosen = alternatives.FirstOrDefault(a => a.ReleaseId == releaseId);
            if (chosen is null && candidate.MatchedReleaseId != releaseId)
            {
                return ScanningErrors.Invalid("scan.release_not_an_option", "That isn't one of the matches for this item.", "releaseId");
            }

            var owned = await resolver.OwnedAsync(libraryId, new EnsuredRelease(releaseId, false), cancellationToken);
            var rankedFirst = alternatives.Count > 0 ? alternatives[0].ReleaseId : candidate.MatchedReleaseId;
            await feedback.RecordPickAsync(candidate, rankedFirst, releaseId, cancellationToken);
            var trace = CandidateJson.ReadTrace(candidate.TraceJson);
            candidate.MatchedReleaseId = releaseId;
            candidate.Resolution = CandidateResolution.Matched;
            candidate.GuidanceCode = null; // picked: a resolved row carries no guidance
            candidate.Score = chosen?.Score ?? candidate.Score;
            candidate.AlreadyOwnedCount = owned.Count;
            candidate.TraceJson = CandidateJson.Serialize(trace with { OwnedAt = CandidateResolver.OwnedAt(owned) });

            // Picking is a choice to add this one, unless the library already has it: then the person has to say so.
            if (!decisionGiven)
            {
                candidate.Decision = owned.Count > 0 ? CandidateDecision.Rejected : CandidateDecision.Accepted;
            }

            return null;
        }
    }
}
