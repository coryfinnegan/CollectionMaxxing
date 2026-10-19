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

/// <summary>What the local catalogue has for a read, as scorer candidates, and the step that records the lookup (none when there was nothing to look up).</summary>
public sealed record LocalPool(IReadOnlyList<(CandidateRelease Scoring, ResolutionOption Option)> Items, TraceStep? Step)
{
    public static LocalPool Empty { get; } = new([], null);
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
    public async Task<BarcodeResolution> SearchAsync(
        string text, ReleaseFormat? format, string? platform, string? edition, CancellationToken cancellationToken)
    {
        var read = new ReadItem { Title = text, Format = format, Platform = platform, Edition = edition };
        var found = await FindLocalAsync(text, cancellationToken);
        var query = new CatalogQuery { Title = text, Format = format, Platform = platform, Limit = Limit };
        using var failFast = ProviderCalls.FailFast(); // a person is waiting
        var answers = await AskProvidersAsync(query, options.ResolveBudget, cancellationToken);
        return Score(read, CatalogProjection.Profile, found, answers, pendingWhenProvidersFailed: false);
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
        }

        return new LocalPool(pool, step);
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
    /// </summary>
    public static BarcodeResolution Score(
        ReadItem read, ScoringProfile profile, LocalPool found, ProviderAnswers answers, bool pendingWhenProvidersFailed)
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

        var ranked = result.Ranked.Take(Alternatives).Select(r => byKey[r.Release.Key] with { Score = r.Score }).ToList();
        return new BarcodeResolution(
            result.Outcome == MatchOutcome.Matched ? ResolutionVerdict.Matched : ResolutionVerdict.Ambiguous, ranked, null, steps);
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

    public sealed record Command(Guid SessionId, Guid CandidateId, string? Text);

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

            var found = await search.SearchAsync(
                text, candidate.EditedFormat, candidate.EditedPlatform, candidate.EditedEdition, cancellationToken);
            await resolver.ApplyAsync(session.LibraryId, candidate, found, cancellationToken);
            resolver.SetVisibleText(candidate, text);
            await db.SaveChangesAsync(cancellationToken);
            return (await CandidateProjection.ToDtosAsync(db, [candidate], cancellationToken))[0];
        }
    }
}
