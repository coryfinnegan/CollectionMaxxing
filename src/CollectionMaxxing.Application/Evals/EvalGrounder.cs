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
/// The items one photo and engine row shows, how many captured segments lost every read to a skip rule (a fragment or an unfound
/// spine), and the reads skipped that way (<see cref="GroundedItem.Skipped"/>): never scored, kept so the combined row can settle
/// its places over every read, as <c>/identify</c> does, and drop them afterwards.
/// </summary>
public sealed record GroundedShot(IReadOnlyList<GroundedItem> Items, int SkippedSegments, IReadOnlyList<GroundedItem>? Skipped = null);

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
    /// ones asking each read on its own would give, in the order the reads came in, except that a spine that is only a series' subtitle is
    /// then asked again with the series its neighbour carries, as <c>/identify</c> does it (<see cref="IdentifyGrounding.RetrySeriesAsync"/>).
    /// </summary>
    public async Task<IReadOnlyList<GroundedItem>> GroundAsync(
        ScanEngine engine,
        IReadOnlyList<EvalReadItemInput> items,
        EvalGroundingMemo memo,
        WorkKind? printKind,
        CancellationToken cancellationToken,
        IReadOnlyList<WorkKind?>? kindsOfTheSnap = null) =>
        (await GroundWithSkipsAsync(engine, items, memo, printKind, cancellationToken, kindsOfTheSnap)).Items;

    /// <summary>
    /// As <see cref="GroundAsync"/>, and also reports the reads that are skipped rather than scored (a fragment, <see cref="FragmentReads"/>,
    /// or a shelf spine the catalogue found nothing for, <see cref="UnfoundSpines"/>), and how many captured segments were left with no
    /// read at all because every read of them was skipped: the scorer counts those as accounted for, as the combined row does.
    /// </summary>
    public async Task<GroundedShot> GroundWithSkipsAsync(
        ScanEngine engine,
        IReadOnlyList<EvalReadItemInput> items,
        EvalGroundingMemo memo,
        WorkKind? printKind,
        CancellationToken cancellationToken,
        IReadOnlyList<WorkKind?>? kindsOfTheSnap = null)
    {
        if (items.Count == 0)
        {
            return new GroundedShot([], 0);
        }

        // Cleaning also tells each kindless read what kind the photo is of (KindContext): what the shelf's own print said when no read
        // names that kind, else what the rest of the photo's reads agreed on, else the print over a split, the one rule /identify applies. The memo below keys on
        // the read with it, so a lookup made under another photo's kind is never reused.
        var cleaned = IdentifyCleaning.Clean([.. items.Select(i => i.Read)], serials: serials, printKind: printKind, snapKinds: kindsOfTheSnap);
        var remembered = await memo.GroundManyAsync(
            [.. cleaned.Select(c => (c.Facts, c.Profile))], claims => GroundClaimsAsync(claims, cancellationToken), cancellationToken);

        // Then the passes over the whole photo that /identify makes: a spine that is only a series' subtitle is asked again with the
        // series its neighbour carries, and a spine that found another kind than the photo's is asked again as the photo's kind.
        // They need the neighbours' answers, so they follow the memo and are never remembered by it.
        var withSeries = await RetrySeriesAsync(cleaned, remembered, cancellationToken);
        var answers = await RetryShotKindAsync(cleaned, withSeries, printKind, kindsOfTheSnap, cancellationToken);

        // One book in one place, one item per photo and engine: a read that repeats a stronger read of the same release in the same
        // place is not scored, the same rule /identify applies, so the scorer sees what a person scanning this photo would have been
        // shown. The same release read in another place is another item, and each is kept.
        var repeats = RepeatReads.Of(answers, [.. cleaned.Select(c => c.Box)]).ToHashSet();
        var local = await LoadLocalAsync(answers, cancellationToken);

        // And a spine read that only found itself (FragmentReads) is not scored either: /identify skips it the same way.
        var fragments = FragmentReads.Of(
            [.. cleaned.Select((c, i) => FragmentReads.ReadOf(c.Facts, c.Profile, answers[i], id => ReleaseOf(local, id)))]).ToHashSet();

        // And so is a shelf spine the catalogue found nothing for after every retry (UnfoundSpines): /identify skips it the same way.
        var unfound = UnfoundSpines.Of([.. cleaned.Select((c, i) => new UnfoundRead(c.Profile, answers[i].Verdict))]).ToHashSet();
        var grounded = new List<GroundedItem>(items.Count - repeats.Count);
        var skipped = new List<GroundedItem>();
        var standing = new HashSet<string>(StringComparer.Ordinal);
        var emptied = new HashSet<string>(StringComparer.Ordinal);
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
            var isSkipped = fragments.Contains(i) || unfound.Contains(i);
            if (isSkipped)
            {
                emptied.Add(cleaned[i].SegmentId ?? $"\0read{i}");
            }
            else if (cleaned[i].SegmentId is { } segment)
            {
                standing.Add(segment);
            }

            (isSkipped ? skipped : grounded).Add(new GroundedItem(
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
                cleaned[i].Unconfirmed,
                isSkipped));
        }

        return new GroundedShot(grounded, emptied.Count(segment => !standing.Contains(segment)), skipped);
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
        using var database = new SemaphoreSlim(1); // the catalogue calls of the trimmed retries, which run while other reads are being asked
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await Task.WhenAll(toAsk.Select((claim, i) => AskProvidersAsync(claim, pools[i], gate, database, stop, budget, cancellationToken)));
    }

    /// <summary>
    /// Asks the providers about one read once a place frees up, and scores the answer. No one is waiting on an eval, so each read gets a
    /// patient budget (the background job's, scaled for the asks in flight), counted from the moment its ask starts on the injected
    /// clock, not the live request's. When one ask fails, the reads still waiting for a place are left for the next call, and the asks
    /// already under way finish what they started. A spine that finds nothing is asked again without the stray words at its ends, and
    /// last with its creator as the title, as <c>/identify</c> asks it (<see cref="IdentifyGrounding.RetryTrimmedAsync"/>): inside the
    /// same budget, in the same place, and before the read is remembered, so what the memo holds is the final answer.
    /// </summary>
    private async Task AskProvidersAsync(
        EvalGroundingMemo.Claim claim,
        LocalPool local,
        SemaphoreSlim gate,
        SemaphoreSlim database,
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
                var answer = grounding.Score(claim.Facts, claim.Profile, local, providers);
                if (IdentifyGrounding.WantsRetry(claim.Facts, claim.Profile, answer, local))
                {
                    var retried = await grounding.RetryTrimmedAsync(
                        [new TrimmedRetry(claim.Facts, claim.Profile, answer, span => grounding.AskProvidersAsync(span, cancellationToken, asking.Token), asking.Token, local)],
                        cancellationToken,
                        database);
                    answer = retried[0];
                    cancellationToken.ThrowIfCancellationRequested(); // same as above: a retry cut short by the app stopping is never remembered
                }

                claim.Complete(answer);
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

    /// <summary>
    /// The series pass of <c>/identify</c> over the photo's reads, once each is grounded (<see cref="IdentifyGrounding.RetrySeriesAsync"/>):
    /// a spine that is only a series' subtitle is asked again with the series its neighbour carries, offered and never matched. The
    /// round has the budget <c>/identify</c> gives it (<see cref="SeriesNeighbour.Budget"/>, counted from when the round starts, on
    /// the injected clock) and the places it has (<see cref="ScanningOptions.Parallelism"/>), not the patient budget of the first
    /// asks: the pending job cannot make this pass, so a person scanning is only ever given this much, and the eval measures that.
    /// A round that runs out leaves the answers as they were. The catalogue is asked after the
    /// first asks are done, so it needs no gate. The answers are the photo's own and are not remembered by the run: they depend on
    /// the neighbours.
    /// </summary>
    private async Task<IReadOnlyList<BarcodeResolution>> RetrySeriesAsync(
        List<CleanItem> cleaned, IReadOnlyList<BarcodeResolution> answers, CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(Math.Max(1, options.Parallelism));
        using var expiry = new CancellationTokenSource(SeriesNeighbour.Budget, time);
        using var round = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token);
        return await grounding.RetrySeriesAsync(
            [.. cleaned.Select((c, i) => new SeriesRetry(c.Facts, c.Profile, c.Box, answers[i], facts => AskWithinRoundAsync(facts, gate, round.Token, cancellationToken), round.Token))],
            (releaseIds, token) => SeriesNeighbour.LocalTitlesAsync(db, releaseIds, token),
            cancellationToken);
    }

    /// <summary>
    /// The kind pass of <c>/identify</c> over the photo's reads, once the series round is done (<see cref="IdentifyGrounding.RetryShotKindAsync"/>):
    /// a spine on a shelf that found another kind than the shelf's is asked again as the shelf's kind, offered and never matched. The
    /// photo's kind is what its Matched spines agree on, or the shelf's own print. The pass has the budget <c>/identify</c> gives it
    /// (<see cref="ShotKindRule.Budget"/>, counted from when the pass starts, on the injected clock) and the places it has
    /// (<see cref="ScanningOptions.Parallelism"/>), as the series round does. <paramref name="kindsOfTheSnap"/> are the kinds the other
    /// engine's reads of the same snap name (the picture model's standing reads, beside Vision's): the app sends one identify per snap
    /// holding both engines' reads, so they vote on the shot's kind and are never asked. A pass that runs out leaves the answers as they were. The
    /// catalogue is asked after the series round is done, so it needs no gate. The answers are the photo's own and are not remembered
    /// by the run: they depend on the other spines.
    /// </summary>
    private async Task<IReadOnlyList<BarcodeResolution>> RetryShotKindAsync(
        List<CleanItem> cleaned, IReadOnlyList<BarcodeResolution> answers, WorkKind? printKind, IReadOnlyList<WorkKind?>? kindsOfTheSnap,
        CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(Math.Max(1, options.Parallelism));
        using var expiry = new CancellationTokenSource(ShotKindRule.Budget, time);
        using var round = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token);
        return await grounding.RetryShotKindAsync(
            [.. cleaned.Select((c, i) => new ShotKindRetry(c.Facts, c.Profile, answers[i], facts => AskWithinRoundAsync(facts, gate, round.Token, cancellationToken), round.Token))],
            printKind,
            (releaseIds, token) => ShotKindRule.LocalKindsAsync(db, releaseIds, token),
            cancellationToken,
            [.. (kindsOfTheSnap ?? []).Select(kind => new ShotKindRead(Spine: true, kind, ResolutionVerdict.Pending, null))],
            (releaseIds, token) => SeriesNeighbour.LocalTitlesAsync(db, releaseIds, token));
    }

    /// <summary>
    /// Asks the providers about one ask of a whole-photo pass once a place frees up, within the pass's budget; an ask the budget runs
    /// out on is a failed answer (the read keeps the answer it had). An ask cut short by the call being cancelled is no answer: it throws.
    /// </summary>
    private async Task<ProviderAnswers> AskWithinRoundAsync(ReadFacts facts, SemaphoreSlim gate, CancellationToken budget, CancellationToken cancellationToken)
    {
        try
        {
            await gate.WaitAsync(budget);
            try
            {
                var providers = await grounding.AskProvidersAsync(facts, cancellationToken, budget);
                cancellationToken.ThrowIfCancellationRequested(); // an ask cut short by the app stopping may hand back a thin answer: never use it
                return providers;
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProviderAnswers([], [new TraceStep("budget", "timeout", 0)], true);
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

    private static (string Title, IReadOnlyList<string> Creators)? ReleaseOf(Dictionary<Guid, GroundedCandidate> local, Guid id) =>
        local.TryGetValue(id, out var release) ? (release.Title, release.Creators ?? []) : null;

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
