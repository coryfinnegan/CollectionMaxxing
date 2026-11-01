using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.Extensions.Logging;

namespace CollectionMaxxing.Application.Evals;

/// <summary>Builds the open run as the phone sees it from what the PC has stored.</summary>
internal static class EvalOpenRunStatuses
{
    public static EvalOpenRunStatus Build(OpenRunHeader header, IEvalRunStore store, IEvalResults results)
    {
        var stored = store.StoredIds();
        var grounded = store.GroundedIds();
        var received = header.Photos.Where(p => stored.Contains(p.Id)).Select(p => p.Id).ToList();
        var summary = header.State == OpenRunState.Done && results.LoadLatestSummary() is { } latest && latest.RunId == header.RunId ? latest : null;
        return new EvalOpenRunStatus(
            header.RunId,
            EnumText.ToText(header.State),
            header.Partial,
            header.OpenedAt,
            header.ConfigVersion,
            header.Device,
            header.BaselineNote,
            header.Photos,
            received,
            received.Count(grounded.Contains),
            header.Refused ?? [],
            header.Failure,
            summary);
    }
}

/// <summary>
/// Use case: the phone opens a run on the PC (<c>POST /api/admin/evals/runs/open</c>). The run lists the golden photos in the order the phone reads
/// them and lives on the PC until it is finished or discarded. There is at most one open run; a scored one is replaced by the next.
/// </summary>
public static class OpenEvalRun
{
    public sealed class Handler(
        IGoldenSet golden,
        IEvalResults results,
        IEvalRunStore store,
        EvalRunCoordinator coordinator,
        IIdGenerator ids,
        ICurrentUser currentUser,
        TimeProvider clock,
        ILogger<Handler> logger)
    {
        public async Task<Result<EvalOpenRunStatus>> HandleAsync(EvalOpenRunInput input, CancellationToken cancellationToken)
        {
            if (!golden.IsAvailable)
            {
                return EvalErrors.NotFound;
            }

            if (!store.IsConfigured)
            {
                return EvalErrors.ResultsPathMissing;
            }

            var invalid = SubmitEvalRun.DeviceProblem(input.Device)
                ?? SubmitEvalRun.ConfigVersionProblem(input.ConfigVersion)
                ?? SubmitEvalRun.NoteProblem(input.BaselineNote);
            if (invalid is not null)
            {
                return invalid;
            }

            // The phone reads shelves before singles, each set in id order.
            var photos = golden.List()
                .OrderBy(p => p.Set, StringComparer.Ordinal)
                .ThenBy(p => p.Id, StringComparer.Ordinal)
                .Select(p => new EvalRunPhoto(p.Id, p.Set))
                .ToList();
            if (photos.Count == 0)
            {
                return EvalErrors.NoPhotos;
            }

            if (photos.Count > SubmitEvalRun.MaxPhotos)
            {
                return EvalErrors.Invalid("evals.too_many_results", $"A run covers at most {SubmitEvalRun.MaxPhotos} photos; the golden folder has {photos.Count}.", "photos");
            }

            using var gate = await coordinator.EnterAsync(cancellationToken);
            if (store.Load() is { State: not OpenRunState.Done })
            {
                return EvalErrors.RunAlreadyOpen;
            }

            var note = string.IsNullOrWhiteSpace(input.BaselineNote) ? null : input.BaselineNote.Trim();
            var header = new OpenRunHeader(
                ids.NewId().ToString("N"),
                clock.GetUtcNow(),
                new EvalDevice(input.Device!.Model!.Trim(), input.Device.OsVersion!.Trim(), input.Device.AppVersion!.Trim()),
                input.ConfigVersion!.Trim(),
                note,
                photos,
                OpenedBy: currentUser.UserId);
            try
            {
                store.Create(header);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("The eval run could not be opened ({Failure}).", exception.GetType().Name);
                return EvalErrors.ResultsNotWritable;
            }

            coordinator.Forget();
            coordinator.RunOpened(); // a re-ground of the last run's readings in progress gives way
            return EvalOpenRunStatuses.Build(header, store, results);
        }
    }
}

/// <summary>Use case: the run the PC holds and the photos it already has (<c>GET /api/admin/evals/runs/open</c>).</summary>
public static class GetOpenEvalRun
{
    public sealed class Handler(IGoldenSet golden, IEvalResults results, IEvalRunStore store)
    {
        public Result<EvalOpenRunStatus> Handle()
        {
            if (!golden.IsAvailable)
            {
                return EvalErrors.NotFound;
            }

            return store.Load() is { } header ? EvalOpenRunStatuses.Build(header, store, results) : EvalErrors.NoOpenRun;
        }
    }
}

/// <summary>
/// Use case: the phone sends one photo, the results of its engines (<c>PUT /api/admin/evals/runs/open/photos/{photoId}</c>). The photo is checked
/// on its own (<see cref="EvalPhotoChecks"/>) and stored, and that is the whole request: nothing is grounded here, the worker does that afterwards.
/// Sending a photo again replaces it. A photo that fails its own checks is refused alone and never takes the run with it.
/// </summary>
public static class StoreEvalPhoto
{
    public sealed class Handler(
        IGoldenSet golden,
        IEvalRunStore store,
        EvalRunCoordinator coordinator,
        TimeProvider clock,
        ILogger<Handler> logger)
    {
        public async Task<Result<EvalPhotoStored>> HandleAsync(EvalPhotoSubmission input, CancellationToken cancellationToken)
        {
            if (!golden.IsAvailable)
            {
                return EvalErrors.NotFound;
            }

            if (!store.IsConfigured)
            {
                return EvalErrors.ResultsPathMissing;
            }

            if (!EvalIds.IsValid(input.PhotoId))
            {
                return EvalErrors.Invalid("evals.photo_id_invalid", "A photoId is letters, digits, _ and -, up to 64 characters.", "photoId");
            }

            var photoId = input.PhotoId!;
            using var gate = await coordinator.EnterAsync(cancellationToken);
            if (store.Load() is not { } header)
            {
                return EvalErrors.NoOpenRun;
            }

            if (header.State != OpenRunState.Open)
            {
                return EvalErrors.RunNotOpen;
            }

            if (!header.Photos.Any(p => p.Id == photoId))
            {
                return EvalErrors.PhotoNotInRun;
            }

            var skipped = new List<SkippedPhoto>();
            var (accepted, problem) = Screen(photoId, input.Results, skipped);
            try
            {
                if (problem is not null)
                {
                    Refuse(header, photoId, problem);
                    return problem;
                }

                // Order matters if the PC stops half way: the old grounding goes first, so a photo is never left beside the grounding of an older send.
                coordinator.Replaced(photoId);
                store.DeleteGrounded(photoId);
                store.SavePhoto(photoId, new StoredEvalPhoto(accepted, skipped, clock.GetUtcNow()));
                if (header.Refused?.Any(r => r.PhotoId == photoId) == true)
                {
                    store.Save(header with { Refused = [.. header.Refused.Where(r => r.PhotoId != photoId)] });
                }

                coordinator.Wake();
                var stored = store.StoredIds();
                return new EvalPhotoStored(photoId, header.Photos.Count(p => stored.Contains(p.Id)), header.Photos.Count);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("An eval photo could not be stored ({Failure}).", exception.GetType().Name);
                return EvalErrors.ResultsNotWritable;
            }
        }

        /// <summary>
        /// A photo nothing could be kept of is listed as refused (unless the PC already holds an earlier send of it, which a bad one never throws
        /// away), so the phone does not read it again and the score lists it as skipped.
        /// </summary>
        private void Refuse(OpenRunHeader header, string photoId, DomainError problem)
        {
            if (store.StoredIds().Contains(photoId))
            {
                return;
            }

            var refused = (header.Refused ?? []).Where(r => r.PhotoId != photoId).Append(new SkippedPhoto(photoId, problem.Code));
            store.Save(header with { Refused = [.. refused] });
        }

        /// <summary>
        /// Checks each engine's result on its own, as the one-shot route does: one that fails is left out and listed with its code, one the checks had
        /// to change is kept with the change listed. An engine sent twice, a result that names another photo, or no result left, refuses the photo.
        /// </summary>
        private (List<EvalPhotoInput> Accepted, DomainError? Problem) Screen(string photoId, IReadOnlyList<EvalPhotoInput>? results, List<SkippedPhoto> skipped)
        {
            if (results is not { Count: > 0 })
            {
                return ([], EvalErrors.Invalid("evals.results_required", "Send at least one result for the photo.", "results"));
            }

            var accepted = new List<EvalPhotoInput>(results.Count);
            var seen = new HashSet<ScanEngine>();
            DomainError? first = null;
            for (var i = 0; i < results.Count; i++)
            {
                if (results[i]?.PhotoId is { } named && !string.Equals(named, photoId, StringComparison.Ordinal))
                {
                    return ([], EvalErrors.PhotoIdMismatch);
                }

                var check = EvalPhotoChecks.Check(results[i] is null ? null : results[i] with { PhotoId = photoId }, i);
                if (check.Problem is { } problem)
                {
                    first ??= problem;
                    skipped.Add(new SkippedPhoto(check.Label, problem.Code));
                    logger.LogWarning(
                        "Eval photo {PhotoId} ({Engine}) was skipped: {ProblemCode} at {Field}.",
                        check.Label,
                        results[i]?.Engine is { } engine ? EnumText.ToText(engine) : "(none)",
                        problem.Code,
                        problem.Field);
                    continue;
                }

                var photo = check.Photo!;
                if (!seen.Add(photo.Engine!.Value))
                {
                    return ([], EvalErrors.Invalid("evals.duplicate_result", "Each photo can be sent once per engine.", $"results[{i}]"));
                }

                if (check.Note is { } note)
                {
                    skipped.Add(new SkippedPhoto(check.Label, note));
                    logger.LogWarning("Eval photo {PhotoId} ({Engine}) was kept with a change: {Note}.", check.Label, EnumText.ToText(photo.Engine.Value), note);
                }

                accepted.Add(photo);
            }

            return accepted.Count == 0 ? ([], first) : (accepted, null);
        }
    }
}

/// <summary>
/// Use case: the phone has sent every photo it will (<c>POST /api/admin/evals/runs/open/finish</c>), or stops early to score what it has. This only
/// marks the run: the worker scores it once every stored photo is grounded. The run is partial when the phone said so, or when a photo that was
/// neither stored nor refused is missing. Finishing again changes nothing.
/// </summary>
public static class FinishEvalRun
{
    public sealed class Handler(IGoldenSet golden, IEvalResults results, IEvalRunStore store, EvalRunCoordinator coordinator, ILogger<Handler> logger)
    {
        public async Task<Result<EvalOpenRunStatus>> HandleAsync(bool partial, CancellationToken cancellationToken)
        {
            if (!golden.IsAvailable)
            {
                return EvalErrors.NotFound;
            }

            using var gate = await coordinator.EnterAsync(cancellationToken);
            if (store.Load() is not { } header)
            {
                return EvalErrors.NoOpenRun;
            }

            if (header.State != OpenRunState.Open)
            {
                return EvalOpenRunStatuses.Build(header, store, results);
            }

            var stored = store.StoredIds();
            var received = header.Photos.Count(p => stored.Contains(p.Id));
            if (received == 0)
            {
                return EvalErrors.Invalid("evals.results_required", "Send at least one photo before finishing the run.", "results");
            }

            var covered = received + (header.Refused?.Count ?? 0);
            header = header with { State = OpenRunState.Finishing, Partial = partial || covered < header.Photos.Count };
            try
            {
                store.Save(header);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("The eval run could not be finished ({Failure}).", exception.GetType().Name);
                return EvalErrors.ResultsNotWritable;
            }

            coordinator.Wake();
            return EvalOpenRunStatuses.Build(header, store, results);
        }
    }
}

/// <summary>Use case: the phone starts over (<c>DELETE /api/admin/evals/runs/open</c>). The run and every file stored for it are removed, scored or not.</summary>
public static class DiscardEvalRun
{
    public sealed class Handler(IGoldenSet golden, IEvalRunStore store, EvalRunCoordinator coordinator, ILogger<Handler> logger)
    {
        public async Task<Result> HandleAsync(CancellationToken cancellationToken)
        {
            if (!golden.IsAvailable || !store.IsConfigured)
            {
                return Result.Failure(EvalErrors.NotFound);
            }

            using var gate = await coordinator.EnterAsync(cancellationToken);
            var existed = store.Load() is not null;
            try
            {
                store.Discard();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("The eval run could not be discarded ({Failure}).", exception.GetType().Name);
                return Result.Failure(EvalErrors.ResultsNotWritable);
            }

            coordinator.Forget();
            return existed ? Result.Success() : Result.Failure(EvalErrors.NoOpenRun);
        }
    }
}
