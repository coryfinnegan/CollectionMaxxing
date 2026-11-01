using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.GameSerials;
using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Application.Evals;

/// <summary>
/// Grounds what the phone read in a golden photo with the very code <c>/identify</c> uses (<see cref="IdentifyGrounding"/> and the
/// same cleaning and profile choice), but as a dry run: there is no scan session, and no snap, candidate or release row is
/// written, no artwork re-score is asked for and no background job is queued. The providers are asked through their normal cached
/// path, so a repeat costs nothing. It reads the catalogue as the caller can: a signed-in administrator sees the global releases.
/// </summary>
public sealed class EvalGrounder(
    IdentifyGrounding grounding,
    IAppDbContext db,
    ScanningOptions options,
    IOptions<EvalsOptions> evals,
    IGameSerialLookup serials,
    TimeProvider time)
{
    /// <summary>The top releases kept per item (the same three <c>/identify</c> offers).</summary>
    public const int Alternatives = 3;

    /// <summary>
    /// Grounds the items one photo and engine read. A read the run's <paramref name="memo"/> has already grounded is answered from
    /// it, so the catalogue and the providers are asked once per distinct read in a run, not once per engine and photo. The reads
    /// that are not known yet are grounded together, the way <c>/identify</c> grounds a picture: the catalogue is asked once for all
    /// of them, then the providers for up to <see cref="EvalsOptions.GroundingParallelism"/> of them at a time. The answers are the
    /// ones asking each read on its own would give, in the order the reads came in.
    /// </summary>
    public async Task<IReadOnlyList<GroundedItem>> GroundAsync(
        ScanEngine engine,
        IReadOnlyList<EvalReadItemInput> items,
        EvalGroundingMemo memo,
        WorkKind? printKind,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return [];
        }

        // Cleaning also tells each kindless read what kind the photo is of (KindContext): what the shelf's own print said when no read
        // names that kind, else what the rest of the photo's reads agreed on, the one rule /identify applies. The memo below keys on
        // the read with it, so a lookup made under another photo's kind is never reused.
        var cleaned = IdentifyCleaning.Clean([.. items.Select(i => i.Read)], serials: serials, printKind: printKind);
        var answers = await memo.GroundManyAsync(
            [.. cleaned.Select(c => (c.Facts, c.Profile))], claims => GroundClaimsAsync(claims, cancellationToken), cancellationToken);

        // One book in one place, one item per photo and engine: a read that repeats a stronger read of the same release in the same
        // place is not scored, the same rule /identify applies, so the scorer sees what a person scanning this photo would have been
        // shown. The same release read in another place is another item, and each is kept.
        var repeats = RepeatReads.Of(answers, [.. cleaned.Select(c => c.Box)]).ToHashSet();
        var local = await LoadLocalAsync(answers, cancellationToken);
        var grounded = new List<GroundedItem>(items.Count - repeats.Count);
        for (var i = 0; i < items.Count; i++)
        {
            if (repeats.Contains(i))
            {
                continue;
            }

            var answer = answers[i];
            var candidates = answer.Options
                .Take(Alternatives)
                .Select(o => Candidate(o, local))
                .OfType<GroundedCandidate>()
                .ToList();
            grounded.Add(new GroundedItem(
                cleaned[i].Facts.SearchText,
                items[i].Read.Completeness is { } completeness ? EnumText.ToText(completeness) : null,
                ResolvedByText.For(engine, items[i].ResolvedBy),
                EnumText.ToText(answer.Verdict),
                candidates,
                items[i].Tier is { } tier ? EnumText.ToText(tier) : null,
                items[i].Provisional,
                items[i].IsGuess,
                cleaned[i].SegmentId,
                ReadOf(cleaned[i].Facts, cleaned[i].Profile, items[i].Read.Creator),
                cleaned[i].Box,
                cleaned[i].Unconfirmed));
        }

        return grounded;
    }

    /// <summary>
    /// Grounds the reads this call has claimed, with the same steps <see cref="IdentifyGrounding.GroundAsync"/> takes for one read.
    /// The database step comes first and alone, for every read that has words to look for: a DbContext serves one query at a time,
    /// so no provider is asked while it runs. The provider asks then run together up to the bound; they touch no database.
    /// </summary>
    private async Task GroundClaimsAsync(IReadOnlyList<EvalGroundingMemo.Claim> claims, CancellationToken cancellationToken)
    {
        var toAsk = new List<EvalGroundingMemo.Claim>(claims.Count);
        foreach (var claim in claims)
        {
            if (claim.Facts.SearchText is null)
            {
                claim.Complete(grounding.Score(claim.Facts, claim.Profile, LocalPool.Empty, ProviderAnswers.None));
            }
            else
            {
                toAsk.Add(claim);
            }
        }

        if (toAsk.Count == 0)
        {
            return;
        }

        var pools = await grounding.FindLocalManyAsync([.. toAsk.Select(c => c.Facts)], cancellationToken);

        // Each provider paces itself with one limiter shared by every ask, so with K asks under way each call waits for the other K - 1
        // and takes up to K times as long as it would alone. Each ask's budget is the job budget for K, so queueing behind the others
        // never costs a read the time the sequential grounder gave it, at any rate.
        var places = Math.Min(Math.Max(1, evals.Value.GroundingParallelism), toAsk.Count);
        var budget = options.JobResolveBudget * places;
        using var gate = new SemaphoreSlim(places);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await Task.WhenAll(toAsk.Select((claim, i) => AskProvidersAsync(claim, pools[i], gate, stop, budget, cancellationToken)));
    }

    /// <summary>
    /// Asks the providers about one read once a place frees up, and scores the answer. No one is waiting on an eval, so each read gets a
    /// patient budget (the background job's, scaled for the asks in flight), counted from the moment its ask starts on the injected
    /// clock, not the live request's. When one ask fails, the reads still waiting for a place are left for the next call, and the asks
    /// already under way finish what they started.
    /// </summary>
    private async Task AskProvidersAsync(
        EvalGroundingMemo.Claim claim,
        LocalPool local,
        SemaphoreSlim gate,
        CancellationTokenSource stop,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        try
        {
            try
            {
                await gate.WaitAsync(stop.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                // A place can free up just as the call is cancelled or another ask has failed: no new ask starts then.
                cancellationToken.ThrowIfCancellationRequested();
                if (stop.IsCancellationRequested)
                {
                    cancellationToken.ThrowIfCancellationRequested(); // the call was cancelled, not another ask failed
                    return;
                }

                using var expiry = new CancellationTokenSource(budget, time);
                using var asking = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token);
                var providers = await grounding.AskProvidersAsync(claim.Facts, cancellationToken, asking.Token);

                // An ask cut short by the app stopping may hand back a thin answer instead of an error: never remember that.
                cancellationToken.ThrowIfCancellationRequested();
                claim.Complete(grounding.Score(claim.Facts, claim.Profile, local, providers));
            }
            finally
            {
                gate.Release();
            }
        }
        catch
        {
            await stop.CancelAsync();
            throw;
        }
    }

    /// <summary>What the read claimed besides its title, as it was scored, the profile it was scored with, and the author as read.</summary>
    private static GroundedRead ReadOf(ReadFacts facts, ScoringProfileKind profile, string? creatorAsRead) => new(
        facts.Creator,
        facts.Kind is { } kind ? EnumText.ToText(kind) : null,
        facts.Format is { } format ? EnumText.ToText(format) : null,
        facts.Year,
        facts.Platform,
        facts.Edition,
        EnumText.ToText(profile),
        creatorAsRead);

    private static GroundedCandidate? Candidate(ResolutionOption option, Dictionary<Guid, GroundedCandidate> local)
    {
        if (option.LocalReleaseId is { } id)
        {
            return local.TryGetValue(id, out var found) ? found with { Score = option.Score, Terms = option.Terms } : null;
        }

        var provider = option.Provider;
        return provider is null
            ? null
            : new GroundedCandidate(
                provider.Title,
                EnumText.ToText(provider.Kind),
                provider.Format is { } format ? EnumText.ToText(format) : null,
                provider.Platform,
                provider.Year,
                option.Score,
                provider.Creators,
                option.Terms);
    }

    private async Task<Dictionary<Guid, GroundedCandidate>> LoadLocalAsync(IReadOnlyList<BarcodeResolution> answers, CancellationToken cancellationToken)
    {
        var ids = answers.SelectMany(a => a.Options).Select(o => o.LocalReleaseId).OfType<Guid>().Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var releases = await db.Releases.AsNoTracking().Where(r => ids.Contains(r.Id)).ToListAsync(cancellationToken);
        var workIds = releases.Select(r => r.WorkId).Distinct().ToList();
        var works = await db.Works.AsNoTracking().Where(w => workIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, cancellationToken);
        var platformIds = releases.Select(r => r.PlatformId).OfType<Guid>().Distinct().ToList();
        var platforms = platformIds.Count == 0
            ? []
            : await db.Platforms.AsNoTracking().Where(p => platformIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);

        return releases.ToDictionary(
            r => r.Id,
            r => new GroundedCandidate(
                works[r.WorkId].Title,
                EnumText.ToText(works[r.WorkId].Kind),
                EnumText.ToText(r.Format),
                r.PlatformId is { } platformId && platforms.TryGetValue(platformId, out var platform) ? platform.Name : null,
                r.ReleaseDate?.Year ?? works[r.WorkId].OriginalYear,
                0,
                ItemProjection.Creators(works[r.WorkId].CreatorsJson)));
    }
}
