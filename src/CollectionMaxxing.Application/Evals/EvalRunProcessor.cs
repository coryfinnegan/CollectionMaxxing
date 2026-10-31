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
    IEvalRunStore store,
    EvalRunCoordinator coordinator,
    EvalRunScoring scoring,
    TimeProvider clock,
    ILogger<EvalRunProcessor> logger)
{
    /// <summary>How long to wait after the first, second… failure of a photo before trying it again. One more failure than there are waits gives it up.</summary>
    public static readonly IReadOnlyList<TimeSpan> RetryAfter = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];

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

            await GroundAsync(header.RunId, photo.Id, progress, cancellationToken);
            return EvalRunStep.Worked;
        }

        if (waiting)
        {
            return EvalRunStep.Waiting;
        }

        return header.State == OpenRunState.Finishing ? await ScoreAsync(header.RunId, progress, cancellationToken) : EvalRunStep.Idle;
    }

    private async Task GroundAsync(string runId, string photoId, EvalRunProgress progress, CancellationToken cancellationToken)
    {
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
            var skipped = (store.LoadPhoto(photoId)?.Skipped ?? []).Append(new SkippedPhoto(photoId, "grounding_failed")).ToList();
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
                header.Partial,
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

        store.Save(header with { State = OpenRunState.Done, FinishedAt = clock.GetUtcNow(), Failure = null });
        try
        {
            store.ClearPhotos(); // the result files hold everything; a failure here leaves files that the next run clears
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("The stored eval photos could not be cleared ({Failure}).", exception.GetType().Name);
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Eval run grounded {Grounded} distinct reads and reused {Reused} repeats.", progress.Memo.Grounded, progress.Memo.Reused);
        }

        return EvalRunStep.Worked;
    }
}
