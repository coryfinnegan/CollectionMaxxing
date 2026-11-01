using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Application.Evals;

/// <summary>
/// The worker's other job, after the resumable run's: when the API starts as another server version than the one that made the latest result, ground and
/// score the last phone run's kept readings (<c>latest-inputs.json</c>) again with this version's own code, as a new run marked with the run it came from.
/// Most recognition fixes change only the PC's half of a run, so this measures one minutes after it deploys, with no phone. A phone-side fix still needs
/// a phone run.
/// <list type="bullet">
/// <item>It decides once per start, whatever the answer, and a try that fails is logged by the type of its error and not repeated until the next start.</item>
/// <item>A photo that cannot be grounded is given up on alone, as in the phone run (<c>grounding_failed</c> in the skipped list), and the rest are scored;
/// the re-scored run is partial when the phone's was, or when a photo was given up here, and not otherwise. When no photo can be grounded nothing is written.</item>
/// <item>It never touches an open run. A run that opens while it works stops it, and the phone run goes ahead; nothing half done is written.</item>
/// <item>An empty <c>reground.request</c> file in the results folder asks for one now, even when the version is unchanged. The file is deleted as the
/// request is taken, and it waits while a phone run is open.</item>
/// <item>It acts as the person who opened the run, with the libraries they belong to now, as the phone run's grounding does, and it grounds with the same
/// <see cref="EvalRunScoring.GroundAsync"/> and a fresh memo. It writes the usual result files, and no draft truth files.</item>
/// </list>
/// </summary>
public sealed class EvalRegrounder(
    IGoldenSet golden,
    IEvalResults results,
    IEvalInputs inputs,
    IEvalRunStore store,
    EvalRunCoordinator coordinator,
    EvalRunScoring scoring,
    CurrentUser currentUser,
    IMembershipReader memberships,
    IOptions<EvalsOptions> options,
    IIdGenerator ids,
    TimeProvider clock,
    ILogger<EvalRegrounder> logger)
{
    private static readonly DomainError NothingGrounded = new(ErrorKind.Conflict, "evals.nothing_grounded", "None of the kept phone readings could be grounded.");

    public async Task<EvalRunStep> StepAsync(EvalRunProgress progress, CancellationToken cancellationToken)
    {
        if (!golden.IsAvailable || !store.IsConfigured || !inputs.IsConfigured)
        {
            return EvalRunStep.Idle;
        }

        // Once it has decided, and with no request file, there is nothing to look at: the common pass reads no run file at all.
        if (progress.RegroundDecided && !inputs.HasRegroundRequest())
        {
            return EvalRunStep.Idle;
        }

        if (await RunIsOpenAsync(cancellationToken))
        {
            if (!progress.RegroundWaitLogged)
            {
                progress.RegroundWaitLogged = true;
                logger.LogInformation("Eval re-ground: waiting for the open phone run to finish; its readings are never touched.");
            }

            return EvalRunStep.Idle;
        }

        var requested = inputs.TakeRegroundRequest();
        if (!requested && progress.RegroundDecided)
        {
            return EvalRunStep.Idle;
        }

        var latest = results.LoadLatestSummary();
        if (!requested && !NeedsRegrounding(progress, latest))
        {
            return EvalRunStep.Idle;
        }

        return KeptFor(latest) is { } kept ? await RegroundAsync(progress, kept, requested, cancellationToken) : EvalRunStep.Idle;
    }

    /// <summary>The automatic check, made once per start: true when the latest result was made by another server version than this one.</summary>
    private bool NeedsRegrounding(EvalRunProgress progress, EvalSummary? latest)
    {
        progress.RegroundDecided = true;
        if (!options.Value.RegroundOnStart)
        {
            logger.LogInformation("Eval re-ground on start is switched off (Evals:RegroundOnStart); a reground.request file still asks for one.");
            return false;
        }

        var sha = BuildSha.Resolve(options.Value);
        if (latest is null)
        {
            logger.LogInformation("Eval re-ground: there is no result yet, so there is nothing to re-ground.");
            return false;
        }

        if (latest.BuildSha == sha)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Eval re-ground: the last result was made by this server version ({Sha}), so there is nothing to re-ground.", sha);
            }

            return false;
        }

        return true;
    }

    /// <summary>The kept readings when they are the ones the latest result came from, else null (said in the log).</summary>
    private KeptRunInputs? KeptFor(EvalSummary? latest)
    {
        if (inputs.Load() is not { } kept)
        {
            logger.LogInformation("Eval re-ground: the PC holds no phone readings of the last run, so there is nothing to re-ground.");
            return null;
        }

        // The latest result came from these readings if it is their run, or the run made from them on an earlier start.
        if (latest is not null && latest.RunId != kept.RunId && latest.RegroundedFrom?.RunId != kept.RunId)
        {
            logger.LogInformation("Eval re-ground: the kept phone readings belong to another run than the latest result, so they are not scored over it.");
            return null;
        }

        return kept;
    }

    private async Task<EvalRunStep> RegroundAsync(EvalRunProgress progress, KeptRunInputs kept, bool requested, CancellationToken stopping)
    {
        progress.RegroundDecided = true; // one try per start, a requested one included: a failed request is not followed by an automatic retry
        // The token is taken before the open run is looked at again: a run opened from here on cancels it, and one opened before is seen by the look.
        using var interrupted = CancellationTokenSource.CreateLinkedTokenSource(stopping, coordinator.RunOpenedToken);
        try
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Eval re-ground: scoring the phone readings of run {RunId} again with server version {Sha} ({Why}).",
                    kept.RunId,
                    BuildSha.Resolve(options.Value),
                    requested ? "requested" : "new server version");
            }

            var scored = await GroundAndScoreAsync(kept, interrupted.Token);
            if (scored is null)
            {
                return Interrupted(progress);
            }

            if (!scored.IsSuccess)
            {
                logger.LogWarning("Eval re-ground failed ({Failure}); it is tried again on the next start or request.", scored.Error!.Code);
                return EvalRunStep.Idle;
            }

            return EvalRunStep.Worked;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (interrupted.IsCancellationRequested)
        {
            await Task.Yield(); // the cancelling thread may be running this inline: carry on from the pool
            return Interrupted(progress);
        }
        catch (Exception exception)
        {
            // The message may name a path or a title; only the type is logged. The decision stays made: no new try before the next start or request.
            logger.LogWarning("Eval re-ground failed ({Failure}); it is tried again on the next start or request.", exception.GetType().Name);
            return EvalRunStep.Idle;
        }
    }

    /// <summary>A phone run opened, so nothing was written. The next pass after that run (or its discarding) decides afresh.</summary>
    private EvalRunStep Interrupted(EvalRunProgress progress)
    {
        progress.RegroundDecided = false;
        logger.LogInformation("Eval re-ground: stopped because a phone run was opened; the phone run goes ahead.");
        return EvalRunStep.Idle;
    }

    /// <summary>Grounds and scores the kept readings as a new run. Null when a phone run is open: nothing was written.</summary>
    private async Task<Result<EvalRunResult>?> GroundAndScoreAsync(KeptRunInputs kept, CancellationToken cancellationToken)
    {
        if (await RunIsOpenAsync(cancellationToken))
        {
            return null;
        }

        // A job has no signed-in caller: act as the person who opened the run, with the libraries they belong to now.
        if (kept.OpenedBy is { } opener)
        {
            currentUser.Initialize(opener, await memberships.GetMembershipsAsync(opener, cancellationToken));
        }

        var skipped = new List<SkippedPhoto>(kept.Skipped);
        var truthSet = scoring.Prepare(kept.Photos.Select(p => p.PhotoId).Distinct(StringComparer.Ordinal), skipped);
        var memo = new EvalGroundingMemo();
        var photos = new List<GroundedPhoto>();
        var givenUp = 0;
        foreach (var photo in kept.Photos.Where(p => truthSet.Listing.ContainsKey(p.PhotoId)))
        {
            try
            {
                var grounded = new List<GroundedPhoto>(photo.Results.Count);
                foreach (var result in photo.Results)
                {
                    grounded.Add(await scoring.GroundAsync(result, memo, cancellationToken));
                }

                photos.AddRange(grounded);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                // As in the phone run: a photo that cannot be grounded is left out and listed, and one bad photo never holds the rest up. Only the type is logged.
                skipped.Add(new SkippedPhoto(photo.PhotoId, EvalRunProcessor.GroundingFailed));
                givenUp++;
                logger.LogWarning("Eval re-ground: a photo could not be grounded ({Failure}); it is left out of the score.", exception.GetType().Name);
            }

            // The token stops a wait on a provider at once; this stops the work between photos as well.
            cancellationToken.ThrowIfCancellationRequested();
            if (await RunIsOpenAsync(cancellationToken))
            {
                return null;
            }
        }

        if (photos.Count == 0 && givenUp > 0)
        {
            return NothingGrounded; // a result with no photo in it must not replace a real one
        }

        // Written under the gate, after one last look: a run that opens after this starts to write waits for it, and one that opened before is left alone.
        using var gate = await coordinator.EnterAsync(cancellationToken);
        if (store.Load() is { State: not OpenRunState.Done })
        {
            return null;
        }

        var run = new EvalRunToScore(
            ids.NewId().ToString("N"),
            clock.GetUtcNow(),
            kept.Device,
            kept.ConfigVersion,
            kept.Partial || givenUp > 0, // the phone's own flag, and partial besides only when this re-ground had to leave a photo out
            kept.BaselineNote,
            photos,
            new EvalRegroundedFrom(kept.RunId, kept.ReadAt));
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Eval re-ground grounded {Grounded} distinct reads and reused {Reused} repeats.", memo.Grounded, memo.Reused);
        }

        return await scoring.ScoreAsync(run, truthSet, skipped, cancellationToken);
    }

    /// <summary>Whether a phone run is open or being scored. Read under the gate, as every writer of the run file holds it, so a read never meets a write half way.</summary>
    private async Task<bool> RunIsOpenAsync(CancellationToken cancellationToken)
    {
        using var gate = await coordinator.EnterAsync(cancellationToken);
        return store.Load() is { State: not OpenRunState.Done };
    }
}
