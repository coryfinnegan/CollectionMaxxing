using System.Text.Json;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Application.Scanning.Learning;
using CollectionMaxxing.Application.Scanning.Photo;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Operations;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CollectionMaxxing.Application.Scanning;

/// <summary>
/// Use case: add what the person accepted to the library, in one transaction. Every accepted candidate becomes an item
/// (as many as its copies), with the session's default location and status. Anything undecided or rejected is left out.
/// All or nothing: if any accepted item can't be added, none is, and the session stays open. Committing twice never adds
/// anything: the same <c>Idempotency-Key</c> gets the first answer back, and any other request is a conflict.
/// <para>
/// Review may send options (SCAN-1d): where to put the items, their status and condition, and per-candidate overrides (location,
/// status, condition, copies). They are checked before anything changes and applied in the same transaction as the items: an
/// override beats the "Add to" choice, which beats the session's defaults. A candidate the person edited (format, platform,
/// edition) is added as the release of the same work that has those, which the commit creates for the library when none does.
/// </para>
/// <para>
/// A photo the person chose to use ("Use my photo") becomes the cover of the items its candidate creates; the photos of candidates that
/// weren't accepted are deleted, files included, so nothing is left behind.
/// </para>
/// </summary>
public static class CommitScanSession
{
    private static readonly TimeSpan KeyLifetime = TimeSpan.FromHours(24);

    public sealed record Command(Guid SessionId, string? IdempotencyKey, CommitOptions? Options = null);

    public sealed class Handler(
        IAppDbContext db,
        IIdGenerator ids,
        ICurrentUser currentUser,
        TimeProvider clock,
        BarcodeLearner learner,
        IPhotoStore photoStore,
        ReleaseCovers releaseCovers,
        ILogger<Handler> logger)
    {
        public async Task<Result<CommitResultDto>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            var userId = currentUser.UserId
                ?? throw new InvalidOperationException("CommitScanSession needs a signed-in caller; the endpoint requires authorization.");

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
            var storedKey = ScanSessions.IdempotencyKeyFor("commit", session.Id, key);
            var hash = CommitOptions.Hash(session.Id, command.Options);
            var previous = await db.IdempotencyKeys.AsNoTracking().FirstOrDefaultAsync(k => k.UserId == userId && k.Key == storedKey, cancellationToken);
            if (previous is not null)
            {
                if (previous.ExpiresAt > now && previous.ResponseBodyJson is { } stored)
                {
                    if (previous.RequestHash != hash)
                    {
                        return ItemErrors.KeyReused; // the same key, a different request
                    }

                    var again = JsonSerializer.Deserialize<CommitResultDto>(stored, CandidateJson.Options);
                    if (again is null)
                    {
                        return ScanningErrors.AlreadyCommitted;
                    }

                    // A commit that made items and left the session open was undone (SCAN-1e): its items are gone, so its answer is no answer.
                    if (again.Created == 0 || session.Status != ScanSessionStatus.Open)
                    {
                        return again;
                    }
                }

                // An answer that no longer fits (the key expired, or that commit was undone): forget it and commit afresh under the same key.
                await db.IdempotencyKeys.Where(k => k.UserId == userId && k.Key == storedKey).ExecuteDeleteAsync(cancellationToken);
            }

            if (session.Status == ScanSessionStatus.Committed)
            {
                return ScanningErrors.AlreadyCommitted;
            }

            if (session.Status != ScanSessionStatus.Open)
            {
                return ScanningErrors.SessionClosed;
            }

            var candidates = await db.ScanCandidates
                .Where(c => c.SessionId == session.Id)
                .OrderBy(c => c.Seq)
                .ToListAsync(cancellationToken);
            var accepted = candidates
                .Where(c => c.Decision == CandidateDecision.Accepted && c.MatchedReleaseId is not null && c.CreatedItemId is null)
                .ToList();

            // Check everything before changing anything: each release must still be one this library may use.
            var releaseIds = accepted.Select(c => c.MatchedReleaseId!.Value).Distinct().ToList();
            var usable = await db.Releases.AsNoTracking()
                .Where(r => releaseIds.Contains(r.Id) && (r.OwnerLibraryId == null || r.OwnerLibraryId == session.LibraryId))
                .Select(r => r.Id)
                .ToListAsync(cancellationToken);
            if (releaseIds.Any(id => !usable.Contains(id)))
            {
                return ScanningErrors.ReleaseUnavailable;
            }

            if (session.DefaultLocationId is { } locationId
                && !await ItemProjection.LocationInLibraryAsync(db, session.LibraryId, locationId, cancellationToken))
            {
                return ScanningErrors.ReleaseUnavailable;
            }

            // The person's options, all checked before anything is added: a bad one leaves no item and no changed candidate.
            var options = command.Options;
            var invalid = await ValidateAsync(options, session, candidates, cancellationToken);
            if (invalid is not null)
            {
                return invalid;
            }

            var overrides = (options?.Overrides ?? []).ToDictionary(o => o.CandidateId!.Value);
            var releaseFor = await new EditedReleases(db, ids).ResolveAsync(session, accepted, now, cancellationToken);

            var coverFiles = await releaseCovers.FetchMissingAsync(releaseFor.Values.Distinct().ToList(), cancellationToken);
            var photos = await CandidatePhotos.AssetIdsAsync(db, accepted, cancellationToken);
            var created = new List<Guid>();
            foreach (var candidate in accepted)
            {
                overrides.TryGetValue(candidate.Id, out var over);
                Guid? photoId = photos.TryGetValue(candidate.Id, out var photo) ? photo : null;
                var copies = over?.Copies ?? candidate.Copies;
                candidate.Copies = copies;
                for (var copy = 0; copy < copies; copy++)
                {
                    var item = new Item
                    {
                        Id = ids.NewId(),
                        LibraryId = session.LibraryId,
                        ReleaseId = releaseFor[candidate.Id],
                        Status = over?.Status ?? options?.Status ?? session.DefaultStatus ?? ItemStatus.Owned,
                        Condition = over?.Condition ?? options?.Condition,
                        Completeness = CompletenessOf(candidate),
                        LocationId = over?.LocationId ?? options?.AddTo ?? session.DefaultLocationId,
                        CoverChoice = photoId is null ? CoverChoice.Release : CoverChoice.MyPhoto,
                        CustomCoverAssetId = photoId,
                        AddedVia = candidate.Source switch
                        {
                            CandidateSource.Barcode => AddedVia.Barcode,
                            CandidateSource.Search => AddedVia.Search,
                            _ => AddedVia.Picture,
                        },
                        CreatedAt = now,
                        CreatedBy = userId, // always the caller
                        UpdatedAt = now,
                    };
                    db.Items.Add(item);
                    created.Add(item.Id);
                    if (copy == 0)
                    {
                        candidate.CreatedItemId = item.Id;
                    }
                }
            }

            await learner.LearnAsync(session.Id, accepted, cancellationToken); // the next scan of these barcodes is a local hit

            // A commit that added nothing (nothing was accepted yet) leaves the session open for the person to carry on.
            var unusedPhotos = new List<PhotoFile>();
            if (created.Count > 0)
            {
                session.Status = ScanSessionStatus.Committed;
                session.CommittedAt = now;
                session.CommittedItemIds = [.. created]; // what Undo removes: these, by id

                // Photos of what was rejected or left undecided have no item to be a cover for.
                var unused = candidates.Where(c => c.CreatedItemId is null && !accepted.Contains(c)).ToList();
                unusedPhotos = await CandidatePhotos.DiscardAsync(db, unused, cancellationToken);
            }

            var result = new CommitResultDto(created.Count, candidates.Count - accepted.Count, created);
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

            // One SaveChanges is one transaction. A second commit racing this one loses on the session's row version.
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch
            {
                await releaseCovers.DeleteFilesAsync(coverFiles); // nothing points at the covers just downloaded
                throw;
            }

            await CandidatePhotos.DeleteFilesAsync(db, photoStore, logger, unusedPhotos, cancellationToken);
            return result;
        }

        /// <summary>Checks the options against the library and the session. Null when they are fine.</summary>
        private async Task<DomainError?> ValidateAsync(
            CommitOptions? options, ScanSession session, List<ScanCandidate> candidates, CancellationToken cancellationToken)
        {
            if (options is null)
            {
                return null;
            }

            var overrides = options.Overrides ?? [];
            if (overrides.Any(o => o.CandidateId is null))
            {
                return ScanningErrors.Invalid("scan.override_candidate_required", "Say which scanned item each override is for.", "overrides");
            }

            if (overrides.Select(o => o.CandidateId).Distinct().Count() != overrides.Count)
            {
                return ScanningErrors.Invalid("scan.override_duplicate", "Each scanned item can have one override.", "overrides");
            }

            if (overrides.Any(o => o.Copies is { } copies && (copies < 1 || copies > ScanningOptions.MaxCopies)))
            {
                return ScanningErrors.Invalid("scan.copies_invalid", $"Copies must be from 1 to {ScanningOptions.MaxCopies}.", "overrides");
            }

            if (overrides.Any(o => candidates.All(c => c.Id != o.CandidateId)))
            {
                return ScanningErrors.CandidateNotFound;
            }

            var locationIds = overrides.Select(o => o.LocationId).Append(options.AddTo).OfType<Guid>().Distinct().ToList();
            if (locationIds.Count > 0)
            {
                var found = await db.Locations.AsNoTracking()
                    .CountAsync(l => l.LibraryId == session.LibraryId && locationIds.Contains(l.Id), cancellationToken);
                if (found != locationIds.Count)
                {
                    return ItemErrors.LocationNotFound; // another library's location looks like no location at all
                }
            }

            return null;
        }

        /// <summary>The completeness the phone read from a picture (loose cartridge, disc only...), if it said.</summary>
        private static ItemCompleteness? CompletenessOf(ScanCandidate candidate)
        {
            if (CandidateJson.ReadElement(candidate.InferredJson) is not { ValueKind: JsonValueKind.Object } inferred
                || !inferred.TryGetProperty("completeness", out var text)
                || text.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            foreach (var value in Enum.GetValues<ItemCompleteness>())
            {
                if (EnumText.ToText(value) == text.GetString())
                {
                    return value;
                }
            }

            return null;
        }
    }
}
