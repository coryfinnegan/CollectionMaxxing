using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Scanning.Provisional;

/// <summary>
/// Use case: "Keep as unverified" (design §8(e)). An unreadable spine becomes a provisional record instead of staying a T2
/// row: the guess title comes from what the phone read (never invented), the location from the session's default, and an
/// uploaded crop is claimed as its visual anchor. The row retires (rejected, linked to the record) so the segment is visible
/// exactly once, in Needs attention — the provisional-or-counted invariant.
/// </summary>
public static class CreateProvisional
{
    public sealed record Command(Guid SessionId, Guid? CandidateId, Guid? CropAssetId);

    public sealed class Handler(IAppDbContext db, ICurrentUser currentUser, IIdGenerator ids, TimeProvider clock)
    {
        public async Task<Result<CreateProvisionalResultDto>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            if (command.CandidateId is not { } candidateId || candidateId == Guid.Empty)
            {
                return ScanningErrors.Invalid("scan.candidate_required", "Say which scanned row to keep.", "candidateId");
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

            if (candidate.Decision == CandidateDecision.Accepted || candidate.CreatedItemId is not null)
            {
                return new DomainError(
                    ErrorKind.Conflict,
                    "scan.provisional_success_refused",
                    "That item is already on its way into the library, so there is nothing to keep unverified.",
                    "candidateId");
            }

            if (candidate.ProvisionalId is { } keptId
                && await db.ProvisionalRecords.AnyAsync(p => p.Id == keptId && p.ResolvedAt == null, cancellationToken))
            {
                return new DomainError(
                    ErrorKind.Conflict,
                    "scan.provisional_already_kept",
                    "That row was already kept as unverified. Find it under Needs attention.",
                    "candidateId");
            }

            Asset? crop = null;
            if (command.CropAssetId is { } cropId)
            {
                crop = await db.Assets.FirstOrDefaultAsync(
                    a => a.Id == cropId && a.OwnerLibraryId == session.LibraryId && a.Kind == AssetKind.ScanPhoto, cancellationToken);
                if (crop is null)
                {
                    return ScanningErrors.Invalid(
                        "scan.crop_not_found", "That crop doesn't exist in this library. Upload it again.", "cropAssetId");
                }

                if (await db.ProvisionalRecords.AnyAsync(p => p.CropAssetId == cropId, cancellationToken))
                {
                    return new DomainError(
                        ErrorKind.Conflict,
                        "scan.crop_already_claimed",
                        "That crop already belongs to another kept row. Upload it again for this one.",
                        "cropAssetId");
                }
            }

            var provisional = new ProvisionalRecord
            {
                Id = ids.NewId(),
                LibraryId = session.LibraryId,
                SessionId = session.Id,
                SnapId = candidate.SnapId,
                CropAssetId = crop?.Id,
                TitleGuess = TitleGuessOf(candidate),
                MediaHint = session.MediaHint,
                LocationId = session.DefaultLocationId,
                Status = ProvisionalStatus.Unverified,
                CandidateId = candidate.Id,
                CreatedAt = clock.GetUtcNow(),
            };
            db.ProvisionalRecords.Add(provisional);
            candidate.Decision = CandidateDecision.Rejected; // retired: Needs attention shows it now, not Review
            candidate.GuidanceCode = null; // retired rows carry no guidance
            candidate.ProvisionalId = provisional.Id;
            await db.SaveChangesAsync(cancellationToken);

            var dto = (await CandidateProjection.ToDtosAsync(db, [candidate], cancellationToken))[0];
            return new CreateProvisionalResultDto(ProvisionalProjection.ToDto(provisional), dto);
        }

        /// <summary>What the phone read, in its own words; a code or a plain "Unknown item" when it read nothing usable.</summary>
        private static string TitleGuessOf(ScanCandidate candidate)
        {
            if (!string.IsNullOrWhiteSpace(candidate.VisibleText))
            {
                return Truncate(candidate.VisibleText.Trim());
            }

            var alternatives = CandidateJson.ReadAlternatives(candidate.AlternativesJson);
            if (alternatives.Count > 0)
            {
                return Truncate(alternatives[0].Title);
            }

            return string.IsNullOrWhiteSpace(candidate.RawCode) ? "Unknown item" : $"Code {candidate.RawCode!.Trim()}";
        }

        private static string Truncate(string text) => text.Length > CreateItem.MaxTitleLength ? text[..CreateItem.MaxTitleLength] : text;
    }
}
