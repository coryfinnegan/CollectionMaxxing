using System.Diagnostics;
using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CollectionMaxxing.Application.Scanning;

/// <summary>
/// What the local catalogue has for a read, as scorer candidates, and the step that records the lookup (none when there was
/// nothing to look up). <c>WorkIdsByKey</c> maps each candidate's key to its work, so a neighbour bias can find its own.
/// </summary>
public sealed record LocalPool(
    IReadOnlyList<(CandidateRelease Scoring, ResolutionOption Option)> Items, TraceStep? Step, IReadOnlyDictionary<string, Guid> WorkIdsByKey)
{
    public static LocalPool Empty { get; } = new([], null, new Dictionary<string, Guid>());
}

/// <summary>What the providers said about a read: the releases, the steps taken, and whether any provider failed or ran out of time.</summary>
public sealed record ProviderAnswers(IReadOnlyList<ProviderRelease> Found, IReadOnlyList<TraceStep> Steps, bool AnyFailed)
{
    public static ProviderAnswers None { get; } = new([], [], false);
}

/// <summary>
/// Looks for a release by the person's own words: the local catalogue first (trigram similarity), then the providers. A typed
/// title is one signal, so a result is never "sure": it comes back as a list to pick from (docs/02 section 6).
/// </summary>
/// <remarks>
/// The search is also available in its three steps (<see cref="FindLocalAsync"/>, <see cref="AskProvidersAsync(CatalogQuery, CancellationToken, CancellationToken)"/>,
/// <see cref="Score"/>) so that a caller with many reads (<c>/identify</c>) can run the database step one read at a time and the
/// provider step for all of them at once, within one time budget.
/// </remarks>
public sealed class TextSearchResolver(
    IAppDbContext db,
    ILocalCatalogSearch local,
    IEnumerable<ICatalogProvider> providers,
    ScanningOptions options,
    ILogger<TextSearchResolver> logger)
{
    private const int Limit = 10;
    private const int Alternatives = 3;
    private const int LocalCap = 50;

    private readonly IReadOnlyList<ICatalogProvider> _providers = providers.ToList();

    public Task<BarcodeResolution> SearchAsync(string text, CancellationToken cancellationToken) =>
        SearchAsync(text, null, null, null, cancellationToken);

    /// <summary>
    /// Like <see cref="SearchAsync(string, CancellationToken)"/>, with what the person corrected on the candidate: a format, a
    /// platform or an edition is one more signal for the scorer (and the providers), never a filter.
    /// </summary>
    public Task<BarcodeResolution> SearchAsync(
        string text, ReleaseFormat? format, string? platform, string? edition, CancellationToken cancellationToken) =>
        SearchAsync(text, format, platform, edition, null, cancellationToken);

    /// <summary>
    /// Like the above, with the segment neighbours' works (design §8(d)): releases of those works sort higher, as a ranking
    /// bias only — never a filter, and the globally-best match can never drop out of the top three.
    /// </summary>
    public async Task<BarcodeResolution> SearchAsync(
        string text,
        ReleaseFormat? format,
        string? platform,
        string? edition,
        IReadOnlyCollection<Guid>? contextWorkIds,
        CancellationToken cancellationToken)
    {
        var read = new ReadItem { Title = text, Format = format, Platform = platform, Edition = edition };
        var found = await FindLocalAsync(text, cancellationToken);
        var query = new CatalogQuery { Title = text, Format = format, Platform = platform, Limit = Limit };
        using var failFast = ProviderCalls.FailFast(); // a person is waiting
        var answers = await AskProvidersAsync(query, options.ResolveBudget, cancellationToken);
        return Score(read, CatalogProjection.Profile, found, answers, pendingWhenProvidersFailed: false, contextWorkIds, found.WorkIdsByKey);
    }

    /// <summary>The releases the local catalogue has for a title (trigram search), as scorer candidates. Uses the database: one call at a time.</summary>
    public async Task<LocalPool> FindLocalAsync(string text, CancellationToken cancellationToken)
    {
        var normalized = TitleNormalizer.Normalize(text);
        if (normalized.Length == 0)
        {
            return LocalPool.Empty;
        }

        var started = Stopwatch.GetTimestamp();
        var hits = await local.FindAsync(new LocalSearch(normalized, null, null, null, LocalCap), cancellationToken);
        var step = new TraceStep("catalogue", hits.Count > 0 ? "hit" : "miss", (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        var platformIds = hits.Select(h => h.Release.PlatformId).OfType<Guid>().Distinct().ToList();
        var platforms = platformIds.Count == 0
            ? []
            : await db.Platforms.AsNoTracking().Where(p => platformIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
        var pool = new List<(CandidateRelease Scoring, ResolutionOption Option)>();
        var workIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var hit in hits)
        {
            var platform = hit.Release.PlatformId is { } id && platforms.TryGetValue(id, out var found) ? found.Name : null;
            pool.Add((
                new CandidateRelease
                {
                    Key = hit.Release.Id.ToString(),
                    Title = hit.Work.Title,
                    Kind = hit.Work.Kind,
                    Format = hit.Release.Format,
                    Platform = platform,
                    Year = hit.Release.ReleaseDate?.Year ?? hit.Work.OriginalYear,
                    Creators = ItemProjection.Creators(hit.Work.CreatorsJson),
                    Edition = hit.Release.Edition,
                },
                new ResolutionOption(hit.Release.Id, null, 0)));
            workIds.TryAdd(hit.Release.Id.ToString(), hit.Work.Id);
        }

        return new LocalPool(pool, step, workIds);
    }

    /// <summary>Asks the providers that know the query's kind (all of them when it has none), within <paramref name="budget"/>.</summary>
    public async Task<ProviderAnswers> AskProvidersAsync(CatalogQuery query, TimeSpan budget, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(budget);
        return await AskProvidersAsync(query, cancellationToken, timeout.Token);
    }

    /// <summary>
    /// Asks the providers that know the query's kind, until <paramref name="budgetToken"/> is cancelled. A provider that fails or
    /// runs out of time (thrown, or reported by the provider layer) is recorded, not thrown, and not asked down the rest of its
    /// ladder; only <paramref name="callerToken"/> cancelling escapes. Touches no database,
    /// so reads can be asked in parallel. Each provider goes down <see cref="Ladder"/> on its own, keeping every release it
    /// found, until it answers with a title close to the one it was asked. "Close", not "anything": a bare-words search with a
    /// misread author still answers, with unrelated books. "On its own": one provider's close companion volume says nothing
    /// about whether another, stricter one needs the plainer query (second eval run). The steps keep every rung.
    /// </summary>
    public async Task<ProviderAnswers> AskProvidersAsync(CatalogQuery query, CancellationToken callerToken, CancellationToken budgetToken)
    {
        var steps = new List<TraceStep>();
        var asked = _providers.Where(p => query.Kind is { } kind ? p.Supports(kind) : Enum.GetValues<WorkKind>().Any(p.Supports)).ToList();
        var answers = await Task.WhenAll(asked.Select(p => AskUntilCloseAsync(p, query, steps, callerToken, budgetToken)));
        return new ProviderAnswers(answers.SelectMany(a => a.Found).ToList(), steps, answers.Any(a => a.Failed));
    }

    /// <summary>A found title at least this similar to the query's is close: the Ambiguous line of the default thresholds.</summary>
    private const double CloseTitle = 0.6;

    /// <summary>The most trailing words a retry drops, and the fewest it leaves.</summary>
    private const int MaxDroppedWords = 2;

    private const int MinWordsLeft = 2;

    /// <summary>
    /// The fewest words a read has before a retry drops its leading words. On a short read the first word is usually part of
    /// the title (dropping it from "TNX for EVERYONE" finds "For Everyone"), and most junk reads are three to five words that
    /// never find anything, so each extra rung is a wasted call.
    /// </summary>
    private const int MinWordsForLeadingDrop = 4;

    /// <summary>
    /// The queries to try, plainest last, none twice, and every one with the title's accents stripped (an accent an OCR read
    /// puts on an English word, "TÉENAGE", is noise): as read; with the title alone, when the read named a creator (#438
    /// stage 1: the fallback, never the default); without the title's last word, and its last two, while two words are left (a
    /// spine read by OCR often runs other print into the title, "... NIGHT QXRTVZ", and a title search finds nothing with it);
    /// with its numbers written the other way, 3 as III and III as 3 (the catalogue may spell a sequel the other way than the
    /// cover); then, when the read has at least four words, without its first word, and its first two, while two words are left,
    /// each also with the numbers swapped (a misread first word, "SLEEP AWAY" for "Sleepaway", sinks a search the rest of the
    /// title would find).
    /// </summary>
    private static IEnumerable<CatalogQuery> Ladder(CatalogQuery asRead)
    {
        var query = asRead with { Title = TitleNormalizer.StripAccents(asRead.Title) };
        var asked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Key(query) };
        yield return query;
        var titleOnly = query with { Creator = null };
        if (!string.IsNullOrWhiteSpace(query.Creator) && asked.Add(Key(titleOnly)))
        {
            yield return titleOnly;
        }

        var words = query.Title.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rungs = new List<string[]>();
        for (var drop = 1; drop <= MaxDroppedWords && words.Length - drop >= MinWordsLeft; drop++)
        {
            rungs.Add(words[..^drop]);
        }

        rungs.AddRange(TitleNumerals.Swapped(words));
        for (var skip = 1; words.Length >= MinWordsForLeadingDrop && skip <= MaxDroppedWords && words.Length - skip >= MinWordsLeft; skip++)
        {
            var rest = words[skip..];
            rungs.Add(rest);
            rungs.AddRange(TitleNumerals.Swapped(rest));
        }

        foreach (var rung in rungs.Select(rung => titleOnly with { Title = string.Join(' ', rung) }))
        {
            if (asked.Add(Key(rung)))
            {
                yield return rung;
            }
        }
    }

    /// <summary>Two rungs are the same when they have the same title (whatever its case) and creator.</summary>
    private static string Key(CatalogQuery rung) => $"{rung.Title}\n{rung.Creator}";

    private async Task<Attempt> AskUntilCloseAsync(
        ICatalogProvider provider, CatalogQuery query, List<TraceStep> steps, CancellationToken callerToken, CancellationToken budgetToken)
    {
        var found = new List<ProviderRelease>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rung in Ladder(query))
        {
            var attempt = await AskAsync(provider, rung, steps, callerToken, budgetToken);
            found.AddRange(attempt.Found.Where(release => seen.Add($"{release.Provider}:{release.ExternalId}")));
            if (attempt.Failed)
            {
                return new Attempt(found, Failed: true);
            }

            if (budgetToken.IsCancellationRequested || attempt.Found.Any(release => TitleNormalizer.Similarity(rung.Title, release.Title) >= CloseTitle))
            {
                break;
            }
        }

        return new Attempt(found, Failed: false);
    }

    /// <summary>
    /// Scores everything found against the read and decides. Pure. With <paramref name="pendingWhenProvidersFailed"/>, a read
    /// that found nothing good while a provider was down or slow is Pending (worth asking again) instead of NotFound.
    /// <paramref name="contextWorkIds"/> biases the ranking toward the segment neighbours' works (design §8(d)): the verdict
    /// and every score stay exactly as the scorer computed them. <paramref name="kindContext"/> is the kind the rest of the
    /// read's snap agreed on (<see cref="Identify.KindContext"/>): for a read that names no kind, a close call lists releases of
    /// that kind's family first when they score within <see cref="KindBias"/> of the others — the same bias, the same rules.
    /// </summary>
    public static BarcodeResolution Score(
        ReadItem read,
        ScoringProfile profile,
        LocalPool found,
        ProviderAnswers answers,
        bool pendingWhenProvidersFailed,
        IReadOnlyCollection<Guid>? contextWorkIds = null,
        IReadOnlyDictionary<string, Guid>? workIdsByKey = null,
        WorkKind? kindContext = null)
    {
        var steps = new List<TraceStep>();
        if (found.Step is { } localStep)
        {
            steps.Add(localStep);
        }

        steps.AddRange(answers.Steps);

        var pool = found.Items.ToList();
        var known = pool.Select(o => CatalogProjection.DuplicateKey(o.Scoring.Title, o.Scoring.Year, o.Scoring.Platform, o.Scoring.Creators)).ToHashSet(StringComparer.Ordinal);
        foreach (var release in answers.Found)
        {
            if (known.Add(CatalogProjection.DuplicateKey(release.Title, release.Year, release.Platform, release.Creators)))
            {
                pool.Add((release.ToCandidate(), new ResolutionOption(null, release, 0)));
            }
        }

        var nothing = pendingWhenProvidersFailed && answers.AnyFailed
            ? BarcodeResolution.Pending(steps)
            : new BarcodeResolution(ResolutionVerdict.NotFound, [], null, steps);
        if (pool.Count == 0)
        {
            return nothing;
        }

        var result = MatchScorer.Evaluate(read, pool.Select(o => o.Scoring).ToList(), profile);
        var byKey = pool.ToDictionary(o => o.Scoring.Key, o => o.Option, StringComparer.Ordinal);
        steps.Add(new TraceStep("scorer", result.Outcome.ToString().ToLowerInvariant(), 0, (result.Ranked.Count > 0 ? result.Ranked[0].Score : (double?)null)));
        if (result.Outcome == MatchOutcome.NotFound)
        {
            return nothing;
        }

        // The snap's kind speaks only for a read that named none, and only in a close call: a Matched top has a lead far beyond
        // the bias (KindBias is under MatchedLead by default), but a profile may be tuned, and the verdict is about the top.
        var kindFamily = (read.Kind is null or WorkKind.Other) && result.Outcome == MatchOutcome.Ambiguous ? kindContext : null;
        var ranked = BiasRanking(result.Ranked, contextWorkIds, workIdsByKey, kindFamily, out var boosted, out var kindMoved);
        if (boosted > 0)
        {
            steps.Add(new TraceStep("context", "biased", 0));
        }

        if (kindMoved)
        {
            steps.Add(new TraceStep("kind", "biased", 0));
        }

        var options = ranked.Take(Alternatives).Select(r => byKey[r.Release.Key] with { Score = r.Score, Terms = r.Terms }).ToList();
        return new BarcodeResolution(
            result.Outcome == MatchOutcome.Matched ? ResolutionVerdict.Matched : ResolutionVerdict.Ambiguous, options, null, steps);
    }

    /// <summary>How much higher a segment neighbour sorts, in score units. Small on purpose: it settles close calls, never landslides.</summary>
    public const double ContextBias = 0.05;

    /// <summary>
    /// How much higher a release of the snap's kind sorts for a read that named none, in score units: a quarter of the Matched
    /// lead (0.08 by default), so it settles a tie, or a gap of a hair, and nothing more. Smaller than <see cref="ContextBias"/>:
    /// a neighbour is a work the person is known to be scanning, while the snap's kind is only what most of its other reads say.
    /// </summary>
    public const double KindBias = 0.02;

    /// <summary>
    /// Reorders a scored ranking toward the neighbours' works and, for a read that names no kind, toward the snap's kind
    /// (<paramref name="kindContext"/>, null for none). The scores are untouched — only the order changes — and the unbiased best
    /// is pinned into the top three, so the bias reorders but can never hide the globally-best match. A release's sort key is
    /// its score plus its bonuses, rounded to the scorer's nine decimals so that a gap a bonus exactly closes is a tie (the
    /// scorer's order stands). <paramref name="boosted"/> counts the ranked releases that got the neighbour bonus;
    /// <paramref name="kindMoved"/> says the kind bonus changed which releases are offered, or in which order. Pure.
    /// </summary>
    private static List<ScoredRelease> BiasRanking(
        IReadOnlyList<ScoredRelease> ranked,
        IReadOnlyCollection<Guid>? contextWorkIds,
        IReadOnlyDictionary<string, Guid>? workIdsByKey,
        WorkKind? kindContext,
        out int boosted,
        out bool kindMoved)
    {
        boosted = 0;
        kindMoved = false;
        var neighbours = contextWorkIds is { Count: > 0 } && workIdsByKey is not null
            ? contextWorkIds as IReadOnlySet<Guid> ?? contextWorkIds.ToHashSet()
            : null;
        if (ranked.Count == 0 || (neighbours is null && kindContext is null))
        {
            return ranked.ToList();
        }

        double NeighbourBonus(ScoredRelease entry) =>
            neighbours is not null && workIdsByKey!.TryGetValue(entry.Release.Key, out var workId) && neighbours.Contains(workId) ? ContextBias : 0;

        boosted = ranked.Count(entry => NeighbourBonus(entry) > 0);
        var neighbourOrder = Ordered(ranked, NeighbourBonus);
        if (kindContext is not { } kind)
        {
            return neighbourOrder;
        }

        var order = Ordered(ranked, entry => NeighbourBonus(entry) + (Identify.KindContext.InFamily(entry.Release.Kind, kind) ? KindBias : 0));
        kindMoved = !order.Take(Alternatives).Select(entry => entry.Release.Key).SequenceEqual(neighbourOrder.Take(Alternatives).Select(entry => entry.Release.Key));
        return order;
    }

    private static List<ScoredRelease> Ordered(IReadOnlyList<ScoredRelease> ranked, Func<ScoredRelease, double> bonus)
    {
        var order = ranked.OrderByDescending(entry => Math.Round(entry.Score + bonus(entry), 9)).ToList();

        // The unbiased best stays selectable whatever the bonuses say: at worst it slides to third.
        var best = ranked[0].Release.Key;
        if (order.FindIndex(entry => entry.Release.Key == best) > Alternatives - 1)
        {
            order.RemoveAll(entry => entry.Release.Key == best);
            order.Insert(Alternatives - 1, ranked[0]);
        }

        return order;
    }

    private sealed record Attempt(IReadOnlyList<ProviderRelease> Found, bool Failed);

    private async Task<Attempt> AskAsync(
        ICatalogProvider provider, CatalogQuery query, List<TraceStep> steps, CancellationToken callerToken, CancellationToken budget)
    {
        var started = Stopwatch.GetTimestamp();
        var outcome = "miss";
        var failed = false;
        IReadOnlyList<ProviderRelease> found = [];
        try
        {
            // An adapter that cannot reach its provider answers "nothing" without throwing; the report tells that from "not
            // known". One report per call: the providers are asked at the same time, and each must hear only its own failures.
            using var report = ProviderCalls.Observe();
            found = await provider.SearchAsync(query, budget);
            failed = report.Failed;
            outcome = failed ? "failed" : found.Count > 0 ? "hit" : "miss";
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // A provider may fail in any way; none of them may fail the search.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            logger.LogWarning("Provider {Provider} failed ({Failure}) during a search.", provider.Name, exception.GetType().Name);
            outcome = "failed";
            failed = true;
        }

        lock (steps)
        {
            steps.Add(new TraceStep(provider.Name, outcome, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        }

        return new Attempt(found, failed);
    }
}

/// <summary>
/// Use case: search again for one scanned item with the person's own words (for a code nobody knew, or a wrong match). The
/// item goes back to "pick one" with what was found; nothing is decided for the person.
/// </summary>
public static class SearchForCandidate
{
    public const int MaxTextLength = 200;

    /// <summary>The most neighbour works a search may carry. A segment holds at most 40 items, so this never binds a real one.</summary>
    public const int MaxContextWorkIds = 40;

    public sealed record Command(Guid SessionId, Guid CandidateId, string? Text, IReadOnlyList<Guid>? ContextWorkIds = null);

    public sealed class Handler(IAppDbContext db, ICurrentUser currentUser, CandidateResolver resolver, TextSearchResolver search)
    {
        public async Task<Result<CandidateDto>> HandleAsync(Command command, CancellationToken cancellationToken)
        {
            var text = command.Text?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                return ScanningErrors.Invalid("scan.text_required", "Type what to search for.", "text");
            }

            if (text.Length > MaxTextLength)
            {
                return ScanningErrors.Invalid("scan.text_too_long", $"Search text can be at most {MaxTextLength} characters.", "text");
            }

            if (command.ContextWorkIds is { } context)
            {
                if (context.Any(id => id == Guid.Empty))
                {
                    return ScanningErrors.Invalid("scan.context_invalid", "Every context work id must be a real id.", "contextWorkIds");
                }

                if (context.Count > MaxContextWorkIds)
                {
                    return ScanningErrors.Invalid(
                        "scan.context_too_many", $"A search carries at most {MaxContextWorkIds} context works.", "contextWorkIds");
                }
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

            if (candidate.AutoCommittedItemIds is { Length: > 0 })
            {
                return ScanningErrors.AutoCommitted;
            }

            var started = Stopwatch.GetTimestamp();
            var found = await search.SearchAsync(
                text, candidate.EditedFormat, candidate.EditedPlatform, candidate.EditedEdition, command.ContextWorkIds, cancellationToken);
            await resolver.ApplyAsync(session.LibraryId, candidate, found, cancellationToken);
            resolver.SetVisibleText(candidate, text);

            // Fallback (d) fired: mark the trace, so evals can audit fallback usage per candidate. Unknown context ids simply
            // match nothing — the bias degrades to a plain search, never an error, never a filter.
            var trace = CandidateJson.ReadTrace(candidate.TraceJson);
            candidate.TraceJson = CandidateJson.Serialize(trace with
            {
                Steps =
                [
                    .. trace.Steps,
                    new TraceStep("search", command.ContextWorkIds is { Count: > 0 } ? "biased" : "plain", (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds),
                ],
            });

            await db.SaveChangesAsync(cancellationToken);
            return (await CandidateProjection.ToDtosAsync(db, [candidate], cancellationToken))[0];
        }
    }
}
