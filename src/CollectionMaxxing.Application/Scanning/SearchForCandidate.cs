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
/// Looks for a release by the person's own words: the local catalogue first (trigram similarity), then the providers. A typed
/// title is one signal, so a result is never "sure": it comes back as a list to pick from (docs/02 section 6).
/// </summary>
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

    public async Task<BarcodeResolution> SearchAsync(string text, CancellationToken cancellationToken)
    {
        var steps = new List<TraceStep>();
        var read = new ReadItem { Title = text };
        var pool = new List<(CandidateRelease Scoring, ResolutionOption Option)>();

        var normalized = TitleNormalizer.Normalize(text);
        if (normalized.Length > 0)
        {
            var started = Stopwatch.GetTimestamp();
            var hits = await local.FindAsync(new LocalSearch(normalized, null, null, null, LocalCap), cancellationToken);
            steps.Add(new TraceStep("catalogue", hits.Count > 0 ? "hit" : "miss", (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            var platformIds = hits.Select(h => h.Release.PlatformId).OfType<Guid>().Distinct().ToList();
            var platforms = platformIds.Count == 0
                ? []
                : await db.Platforms.AsNoTracking().Where(p => platformIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
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
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.ResolveBudget);
        var asked = _providers.Where(p => Enum.GetValues<WorkKind>().Any(p.Supports)).ToList();
        var query = new CatalogQuery { Title = text, Limit = Limit };
        var answers = await Task.WhenAll(asked.Select(p => AskAsync(p, query, steps, cancellationToken, timeout.Token)));

        var known = pool.Select(o => CatalogProjection.DuplicateKey(o.Scoring.Title, o.Scoring.Year, o.Scoring.Platform)).ToHashSet(StringComparer.Ordinal);
        foreach (var found in answers.SelectMany(a => a))
        {
            if (known.Add(CatalogProjection.DuplicateKey(found.Title, found.Year, found.Platform)))
            {
                pool.Add((found.ToCandidate(), new ResolutionOption(null, found, 0)));
            }
        }

        if (pool.Count == 0)
        {
            return new BarcodeResolution(ResolutionVerdict.NotFound, [], null, steps);
        }

        var result = MatchScorer.Evaluate(read, pool.Select(o => o.Scoring).ToList(), CatalogProjection.Profile);
        var byKey = pool.ToDictionary(o => o.Scoring.Key, o => o.Option, StringComparer.Ordinal);
        steps.Add(new TraceStep("scorer", result.Outcome.ToString().ToLowerInvariant(), 0, (result.Ranked.Count > 0 ? result.Ranked[0].Score : (double?)null)));
        if (result.Outcome == MatchOutcome.NotFound)
        {
            return new BarcodeResolution(ResolutionVerdict.NotFound, [], null, steps);
        }

        var ranked = result.Ranked.Take(Alternatives).Select(r => byKey[r.Release.Key] with { Score = r.Score }).ToList();
        return new BarcodeResolution(
            result.Outcome == MatchOutcome.Matched ? ResolutionVerdict.Matched : ResolutionVerdict.Ambiguous, ranked, null, steps);
    }

    private async Task<IReadOnlyList<ProviderRelease>> AskAsync(
        ICatalogProvider provider, CatalogQuery query, List<TraceStep> steps, CancellationToken callerToken, CancellationToken budget)
    {
        var started = Stopwatch.GetTimestamp();
        var outcome = "miss";
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
        }

        lock (steps)
        {
            steps.Add(new TraceStep(provider.Name, outcome, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        }

        return found;
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

            var found = await search.SearchAsync(text, cancellationToken);
            await resolver.ApplyAsync(session.LibraryId, candidate, found, cancellationToken);
            resolver.SetVisibleText(candidate, text);
            await db.SaveChangesAsync(cancellationToken);
            return (await CandidateProjection.ToDtosAsync(db, [candidate], cancellationToken))[0];
        }
    }
}
