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
    /// runs out of time is recorded, not thrown; only <paramref name="callerToken"/> cancelling escapes. Touches no database,
    /// so reads can be asked in parallel.
    /// </summary>
    public async Task<ProviderAnswers> AskProvidersAsync(CatalogQuery query, CancellationToken callerToken, CancellationToken budgetToken)
    {
        var steps = new List<TraceStep>();
        var asked = _providers.Where(p => query.Kind is { } kind ? p.Supports(kind) : Enum.GetValues<WorkKind>().Any(p.Supports)).ToList();
        var answers = await Task.WhenAll(asked.Select(p => AskAsync(p, query, steps, callerToken, budgetToken)));
        return new ProviderAnswers(answers.SelectMany(a => a.Found).ToList(), steps, answers.Any(a => a.Failed));
    }

    /// <summary>
    /// Scores everything found against the read and decides. Pure. With <paramref name="pendingWhenProvidersFailed"/>, a read
    /// that found nothing good while a provider was down or slow is Pending (worth asking again) instead of NotFound.
    /// <paramref name="contextWorkIds"/> biases the ranking toward the segment neighbours' works (design §8(d)): the verdict
    /// and every score stay exactly as the scorer computed them.
    /// </summary>
    public static BarcodeResolution Score(
        ReadItem read,
        ScoringProfile profile,
        LocalPool found,
        ProviderAnswers answers,
        bool pendingWhenProvidersFailed,
        IReadOnlyCollection<Guid>? contextWorkIds = null,
        IReadOnlyDictionary<string, Guid>? workIdsByKey = null)
    {
        var steps = new List<TraceStep>();
        if (found.Step is { } localStep)
        {
            steps.Add(localStep);
        }

        steps.AddRange(answers.Steps);

        var pool = found.Items.ToList();
        var known = pool.Select(o => CatalogProjection.DuplicateKey(o.Scoring.Title, o.Scoring.Year, o.Scoring.Platform)).ToHashSet(StringComparer.Ordinal);
        foreach (var release in answers.Found)
        {
            if (known.Add(CatalogProjection.DuplicateKey(release.Title, release.Year, release.Platform)))
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

        var ranked = BiasRanking(result.Ranked, contextWorkIds, workIdsByKey, out var boosted);
        if (boosted > 0)
        {
            steps.Add(new TraceStep("context", "biased", 0));
        }

        var options = ranked.Take(Alternatives).Select(r => byKey[r.Release.Key] with { Score = r.Score }).ToList();
        return new BarcodeResolution(
            result.Outcome == MatchOutcome.Matched ? ResolutionVerdict.Matched : ResolutionVerdict.Ambiguous, options, null, steps);
    }

    /// <summary>How much higher a segment neighbour sorts, in score units. Small on purpose: it settles close calls, never landslides.</summary>
    public const double ContextBias = 0.05;

    /// <summary>
    /// Reorders a scored ranking toward the neighbours' works. The scores are untouched — only the order changes — and the
    /// unbiased best is pinned into the top three, so the bias reorders but can never hide the globally-best match.
    /// <paramref name="boosted"/> counts the ranked releases that got the bonus. Pure.
    /// </summary>
    private static List<ScoredRelease> BiasRanking(
        IReadOnlyList<ScoredRelease> ranked, IReadOnlyCollection<Guid>? contextWorkIds, IReadOnlyDictionary<string, Guid>? workIdsByKey, out int boosted)
    {
        boosted = 0;
        if (ranked.Count == 0 || contextWorkIds is not { Count: > 0 } || workIdsByKey is null)
        {
            return ranked.ToList();
        }

        var context = contextWorkIds as IReadOnlySet<Guid> ?? contextWorkIds.ToHashSet();
        var order = ranked
            .Select(entry => (Entry: entry, Boosted: workIdsByKey.TryGetValue(entry.Release.Key, out var workId) && context.Contains(workId)))
            .OrderByDescending(x => x.Entry.Score + (x.Boosted ? ContextBias : 0))
            .Select(x => x.Entry)
            .ToList();
        boosted = order.Count(entry => workIdsByKey.TryGetValue(entry.Release.Key, out var workId) && context.Contains(workId));

        // The unbiased best stays selectable whatever the neighbours say: at worst it slides to third.
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
            found = await provider.SearchAsync(query, budget);
            outcome = found.Count > 0 ? "hit" : "miss";
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
