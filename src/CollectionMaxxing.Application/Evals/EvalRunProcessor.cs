using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Common;
using Microsoft.Extensions.Logging;

namespace CollectionMaxxing.Application.Evals;

/// <summary>What one pass of the background work found to do.</summary>
public enum EvalRunStep
{
    /// <summary>Nothing to do: wait to be woken.</summary>
    Idle,

    /// <summary>Something was done: look again at once.</summary>
    Worked,

    /// <summary>Something failed and waits for its turn to be tried again: look again soon.</summary>
    Waiting,
}

/// <summary>
/// What the worker remembers between passes, in memory only (after a restart the files are all there is): the run's grounding memo, so a read that
/// repeats is looked up once per run, and how often each photo's grounding has failed and when it may be tried again.
/// </summary>
public sealed class EvalRunProgress
{
    private readonly Dictionary<string, (int Failures, DateTimeOffset RetryAt)> _failing = new(StringComparer.Ordinal);

    public string? RunId { get; private set; }

    public EvalGroundingMemo Memo { get; private set; } = new();

    /// <summary>
    /// True once the worker has decided, since the API started, whether the last run's kept readings need grounding again (<see cref="EvalRegrounder"/>).
    /// It decides once, whatever the answer and however the try ends, so a failure is never tried again in a loop; a request file is another matter.
    /// </summary>
    public bool RegroundDecided { get; set; }

    /// <summary>True once the worker has said that it is waiting for an open phone run, so the log says it once.</summary>
    public bool RegroundWaitLogged { get; set; }

    /// <summary>Starts a new memo and forgets the failures when the run is another one than the last pass saw.</summary>
    public void Follow(string runId)
    {
        if (RunId == runId)
        {
            return;
        }

        RunId = runId;
        Memo = new EvalGroundingMemo();
        _failing.Clear();
    }

    public bool IsWaiting(string photoId, DateTimeOffset now) => _failing.TryGetValue(photoId, out var failure) && failure.RetryAt > now;

    public int Failures(string photoId) => _failing.TryGetValue(photoId, out var failure) ? failure.Failures : 0;

    public int Failed(string photoId, DateTimeOffset retryAt)
    {
        var failures = Failures(photoId) + 1;
        _failing[photoId] = (failures, retryAt);
        return failures;
    }

    public void Forgive(string photoId) => _failing.Remove(photoId);
}

/// <summary>
/// The background half of the resumable run, one step at a time: ground a photo the PC holds and has not grounded yet (the run folder is the queue),
/// or, once the phone has finished the run and every stored photo is grounded, score it once. Safe to stop at any point and to start again after a
/// restart: whatever is not in a grounded file is simply grounded again. A photo whose grounding fails is tried again after a wait, and given up on
/// (listed as <c>grounding_failed</c>) after the last try, so one bad photo never holds the run up.
/// </summary>
public sealed class EvalRunProcessor(
    IGoldenSet golden,
    IEvalResults results,
    IEvalRunStore store,
    IEvalInputs inputs,
    EvalRunCoordinator coordinator,
    EvalRunScoring scoring,
    CurrentUser currentUser,
    IMembershipReader memberships,
    TimeProvider clock,
    ILogger<EvalRunProcessor> logger)
{
    /// <summary>How long to wait after the first, second… failure of a photo before trying it again. One more failure than there are waits gives it up.</summary>
    public static readonly IReadOnlyList<TimeSpan> RetryAfter = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];

    /// <summary>Why a photo was left out of the score when its grounding failed for good. A run that has one is partial, so it never gets a full verdict or becomes a baseline.</summary>
    public const string GroundingFailed = "grounding_failed";

    private static readonly DomainError ScoringFailed = new(ErrorKind.Conflict, "evals.scoring_failed", "The run could not be scored. The PC will try again.");

    public async Task<EvalRunStep> StepAsync(EvalRunProgress progress, CancellationToken cancellationToken)
    {
        if (!golden.IsAvailable || !store.IsConfigured || store.Load() is not { } header || header.State == OpenRunState.Done)
        {
            return EvalRunStep.Idle;
        }

        progress.Follow(header.RunId);
        var stored = store.StoredIds();
        var grounded = store.GroundedIds();
        var now = clock.GetUtcNow();
        var waiting = false;
        foreach (var photo in header.Photos)
        {
            if (!stored.Contains(photo.Id) || grounded.Contains(photo.Id))
            {
                continue;
            }

            if (progress.IsWaiting(photo.Id, now))
            {
                waiting = true;
                continue;
            }

            await GroundAsync(header, photo.Id, progress, cancellationToken);
            return EvalRunStep.Worked;
        }

        if (waiting)
        {
            return EvalRunStep.Waiting;
        }

        return header.State == OpenRunState.Finishing ? await ScoreAsync(header.RunId, progress, cancellationToken) : EvalRunStep.Idle;
    }

    private async Task GroundAsync(OpenRunHeader header, string photoId, EvalRunProgress progress, CancellationToken cancellationToken)
    {
        var runId = header.RunId;
        long version = 0;
        try
        {
            StoredEvalPhoto? photo;
            using (await coordinator.EnterAsync(cancellationToken))
            {
                // The photo and its count are read together: a send replaces both under this same gate, so they always belong to one send.
                version = coordinator.VersionOf(photoId);
                photo = store.LoadPhoto(photoId);
                if (photo is null && store.Load() is { } current && current.RunId == runId)
                {
                    // A stored photo that cannot be read is dropped: the phone sees the PC does not have it and sends it again.
                    store.DeletePhoto(photoId);
                }
            }

            if (photo is null)
            {
                logger.LogWarning("A stored eval photo could not be read, so it was dropped and will be asked for again.");
                return;
            }

            // A job has no signed-in caller. It acts as the person who opened the run, with the libraries they belong to now, so it sees the
            // catalogue as the one-shot route and the scanner do for them (a release a library made for itself is private to its members).
            if (header.OpenedBy is { } opener)
            {
                currentUser.Initialize(opener, await memberships.GetMembershipsAsync(opener, cancellationToken));
            }

            var grounded = new List<GroundedPhoto>(photo.Results.Count);
            foreach (var result in photo.Results)
            {
                grounded.Add(await scoring.GroundAsync(result, progress.Memo, cancellationToken));
            }

            // A grounding that was cut short by the app stopping may hand back a thin answer instead of an error: never keep that.
            cancellationToken.ThrowIfCancellationRequested();
            await KeepAsync(runId, photoId, version, new GroundedPhotoFile(grounded, photo.Skipped), cancellationToken);
            progress.Forgive(photoId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await FailedAsync(runId, photoId, version, progress, exception, cancellationToken);
        }
    }

    /// <summary>Keeps the grounding unless the run was discarded or the photo was sent again while it was being grounded; then it is looked at again.</summary>
    private async Task KeepAsync(string runId, string photoId, long version, GroundedPhotoFile file, CancellationToken cancellationToken)
    {
        using var gate = await coordinator.EnterAsync(cancellationToken);
        if (store.Load() is { } header && header.RunId == runId && header.State != OpenRunState.Done && coordinator.VersionOf(photoId) == version)
        {
            store.SaveGrounded(photoId, file);
        }
    }

    private async Task FailedAsync(string runId, string photoId, long version, EvalRunProgress progress, Exception exception, CancellationToken cancellationToken)
    {
        // The exception's message may name a path or a query; only its type is logged.
        var failures = progress.Failed(photoId, clock.GetUtcNow() + RetryAfter[Math.Min(progress.Failures(photoId), RetryAfter.Count - 1)]);
        if (failures <= RetryAfter.Count)
        {
            logger.LogWarning("Grounding an eval photo failed ({Failure}); try {Try} of {Tries}, again later.", exception.GetType().Name, failures, RetryAfter.Count + 1);
            return;
        }

        logger.LogError("Grounding an eval photo failed {Failures} times ({Failure}); it is left out of the score.", failures, exception.GetType().Name);
        try
        {
            var skipped = (store.LoadPhoto(photoId)?.Skipped ?? []).Append(new SkippedPhoto(photoId, GroundingFailed)).ToList();
            await KeepAsync(runId, photoId, version, new GroundedPhotoFile([], skipped), cancellationToken);
            progress.Forgive(photoId);
        }
        catch (Exception giveUp) when (giveUp is IOException or UnauthorizedAccessException)
        {
            logger.LogError("An eval photo could not be given up on either ({Failure}); it stays in the queue.", giveUp.GetType().Name);
        }
    }

    /// <summary>The phone finished the run and every stored photo is grounded: score it once and drop the stored photos.</summary>
    private async Task<EvalRunStep> ScoreAsync(string runId, EvalRunProgress progress, CancellationToken cancellationToken)
    {
        using var gate = await coordinator.EnterAsync(cancellationToken);
        if (store.Load() is not { } header || header.RunId != runId || header.State != OpenRunState.Finishing)
        {
            return EvalRunStep.Worked; // discarded or changed meanwhile: look again
        }

        // The results of this run may already be on disk: the API stopped, or the header could not be written, after they were. Then the run is only
        // marked done; scoring it again would write a second result file for one run.
        if (results.LoadLatestSummary() is { } written && written.RunId == header.RunId)
        {
            if (inputs.Load()?.RunId != header.RunId)
            {
                await KeepReadingsAsync(header); // stopped between the result files and the readings: the photos are usually still here (it keeps nothing if not)
            }

            Done(header with { Partial = written.Partial });
            return EvalRunStep.Worked;
        }

        var stored = store.StoredIds();
        var files = new List<GroundedPhotoFile>();
        foreach (var photo in header.Photos.Where(p => stored.Contains(p.Id)))
        {
            if (store.LoadGrounded(photo.Id) is not { } file)
            {
                store.DeleteGrounded(photo.Id); // unreadable: ground it again
                return EvalRunStep.Worked;
            }

            files.Add(file);
        }

        var skipped = new List<SkippedPhoto>(header.Refused ?? []);
        foreach (var file in files)
        {
            skipped.AddRange(file.Skipped);
        }

        // A photo given up on is a photo the run did not cover: the run is partial, whatever the phone said.
        var partial = header.Partial || skipped.Any(s => s.Reason == GroundingFailed);
        Result<EvalRunResult> scored;
        try
        {
            var photos = files.SelectMany(f => f.Photos).ToList();
            var truthSet = scoring.Prepare(photos.Select(p => p.PhotoId).Distinct(StringComparer.Ordinal), skipped);
            var run = new EvalRunToScore(
                header.RunId,
                clock.GetUtcNow(),
                header.Device,
                header.ConfigVersion,
                partial,
                header.BaselineNote,
                [.. photos.Where(p => truthSet.Listing.ContainsKey(p.PhotoId))]);
            scored = await scoring.ScoreAsync(run, truthSet, skipped, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            scored = ScoringFailed;
            logger.LogError("Scoring an eval run failed ({Failure}); it will be tried again.", exception.GetType().Name);
        }

        if (!scored.IsSuccess)
        {
            if (header.Failure != scored.Error!.Code)
            {
                store.Save(header with { Failure = scored.Error.Code });
            }

            return EvalRunStep.Waiting;
        }

        await KeepReadingsAsync(header);
        Done(header with { Partial = partial });
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Eval run grounded {Grounded} distinct reads and reused {Reused} repeats.", progress.Memo.Grounded, progress.Memo.Reused);
        }

        return EvalRunStep.Worked;
    }

    /// <summary>
    /// Keeps the phone's readings of the run just scored, from the stored photos (which <see cref="Done"/> is about to clear), so a later server version can
    /// ground and score them again without the phone. A failure to keep them is logged and never fails the run: the result files are already written, and
    /// readings that belong to an older run are simply not used (<see cref="EvalRegrounder"/> checks they match the latest result). The partial flag kept
    /// is the phone's own (the header's): a photo the PC gave up grounding is not part of the readings, so a re-ground that grounds it is a complete run.
    /// </summary>
    private async Task KeepReadingsAsync(OpenRunHeader header)
    {
        try
        {
            var stored = store.StoredIds();
            var photos = new List<KeptPhoto>();
            var skipped = new List<SkippedPhoto>(header.Refused ?? []);
            foreach (var id in header.Photos.Where(p => stored.Contains(p.Id)).Select(p => p.Id))
            {
                if (store.LoadPhoto(id) is not { } photo)
                {
                    logger.LogWarning("A stored eval photo could not be read, so the phone's readings of this run were not kept.");
                    return;
                }

                photos.Add(new KeptPhoto(id, photo.Results));
                skipped.AddRange(photo.Skipped);
            }

            if (photos.Count == 0)
            {
                // The photos were already cleared (a stop after Done cleared them and before the run was marked done): there is nothing to keep, and
                // readings with no photo in them must never stand in for the run.
                logger.LogWarning("The stored eval photos were already gone, so the phone's readings of this run were not kept.");
                return;
            }

            await inputs.SaveAsync(new KeptRunInputs(header.RunId, header.OpenedAt, header.Device, header.ConfigVersion, header.BaselineNote, header.Partial, header.OpenedBy, photos, skipped), CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("The phone's readings of the eval run could not be kept ({Failure}).", exception.GetType().Name);
        }
    }

    /// <summary>
    /// Drops the stored photos, then marks the run scored: the result files and the kept readings hold everything. In that order, a run the phone
    /// sees as done never still has photos stored; a stop between the two leaves a run whose results are on disk, which the next pass only marks done.
    /// </summary>
    private void Done(OpenRunHeader header)
    {
        try
        {
            store.ClearPhotos(); // a failure here leaves files that the next run clears
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("The stored eval photos could not be cleared ({Failure}).", exception.GetType().Name);
        }

        store.Save(header with { State = OpenRunState.Done, FinishedAt = clock.GetUtcNow(), Failure = null });
    }
}
