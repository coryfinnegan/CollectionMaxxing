using System.Diagnostics;
using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Identifiers;
using Microsoft.Extensions.Logging;

namespace CollectionMaxxing.Application.Scanning;

public enum ResolutionVerdict
{
    /// <summary>A clear winner: included by default.</summary>
    Matched,

    /// <summary>Several close answers: the person picks one of the top three.</summary>
    Ambiguous,

    /// <summary>Nothing close enough.</summary>
    NotFound,

    /// <summary>A provider could not answer in time (it was down, slow or out of quota): worth asking again.</summary>
    Pending,
}

/// <summary>One step of how a code was resolved. Never holds the code or any key: it explains the answer, nothing more.</summary>
public sealed record TraceStep(string Provider, string Outcome, long Ms, double? Score = null);

/// <summary>A release that answers a code: one the catalogue already has, or one a provider just told us about.</summary>
public sealed record ResolutionOption(Guid? LocalReleaseId, ProviderRelease? Provider, double Score);

/// <summary>How a code resolved (docs/02 section 3): the verdict, up to three options best first, and the steps taken.</summary>
public sealed record BarcodeResolution(
    ResolutionVerdict Verdict, IReadOnlyList<ResolutionOption> Options, string? ProductTitle, IReadOnlyList<TraceStep> Steps)
{
    public static BarcodeResolution Pending(IReadOnlyList<TraceStep> steps, string? productTitle = null) =>
        new(ResolutionVerdict.Pending, [], productTitle, steps);
}

/// <summary>
/// The outside half of resolving a barcode (docs/02 section 3, steps 2 and 3): books by ISBN at Open Library and Google
/// Books; any other product by UPCitemdb, then its cleaned name searched at IGDB or TMDB and scored. It touches no database,
/// so a batch of codes can run it in parallel. It never throws for a provider that fails or is slow: that code comes back
/// Pending. Only the caller's own cancellation (the request's time budget) escapes, and the caller treats it as Pending too.
/// </summary>
public sealed class BarcodeResolver(
    IEnumerable<ICatalogProvider> providers,
    IProductLookup products,
    ScanningOptions options,
    ILogger<BarcodeResolver> logger)
{
    /// <summary>What a provider that gives the exact ISBN is worth (docs/02: 0.98, a hair under a local hit).</summary>
    public const double ProviderExactScore = 0.98;

    private const int Alternatives = 3;

    private readonly IReadOnlyList<ICatalogProvider> _providers = providers.ToList();

    public async Task<BarcodeResolution> ResolveAsync(Identifier id, CancellationToken cancellationToken)
    {
        var steps = new List<TraceStep>();
        var anyFailed = false;

        if (id.ProbableKind == WorkKind.Book)
        {
            var books = _providers
                .Where(p => options.BookProviders.Contains(p.Name, StringComparer.OrdinalIgnoreCase) && p.Supports(WorkKind.Book))
                .ToList();
            var answers = await Task.WhenAll(books.Select(p => AskAsync(p.Name, token => p.LookupAsync(id, token), cancellationToken)));
            steps.AddRange(answers.Select(a => a.Step));
            anyFailed |= answers.Any(a => a.Failed);
            if (answers.Select(a => a.Value).FirstOrDefault(v => v is not null) is { } book)
            {
                return new BarcodeResolution(
                    ResolutionVerdict.Matched, [new ResolutionOption(null, book, ProviderExactScore)], book.Title, steps);
            }
        }

        var product = await AskAsync("product-lookup", token => products.LookupAsync(id, token), cancellationToken);
        steps.Add(product.Step with { Outcome = product.Failed ? "failed" : OutcomeOf(product.Value) });
        anyFailed |= product.Failed;
        if (product.Value is not { Status: ProductLookupStatus.Found, Release: { } found })
        {
            var pending = product.Failed || product.Value is { Status: ProductLookupStatus.Unavailable or ProductLookupStatus.QuotaExhausted };
            return pending || anyFailed
                ? BarcodeResolution.Pending(steps)
                : new BarcodeResolution(ResolutionVerdict.NotFound, [], null, steps);
        }

        return await ScoreAsync(found, steps, anyFailed, cancellationToken);
    }

    /// <summary>Searches the providers that know this kind of thing for the product's name and scores what they find.</summary>
    private async Task<BarcodeResolution> ScoreAsync(ProviderRelease product, List<TraceStep> steps, bool anyFailed, CancellationToken cancellationToken)
    {
        var query = new CatalogQuery
        {
            Title = product.Title,
            Kind = product.Kind,
            Format = product.Format,
            Platform = product.Platform,
            Year = product.Year,
            Limit = 10,
        };
        var asked = _providers.Where(p => p.Supports(product.Kind)).ToList();
        var answers = await Task.WhenAll(asked.Select(p => AskAsync(p.Name, token => p.SearchAsync(query, token), cancellationToken)));
        foreach (var answer in answers)
        {
            steps.Add(answer.Step with { Outcome = answer.Failed ? "failed" : answer.Value is { Count: > 0 } ? "hit" : "miss" });
            anyFailed |= answer.Failed;
        }

        var read = new ReadItem
        {
            Title = product.Title,
            Kind = product.Kind,
            Format = product.Format,
            Platform = product.Platform,
            Year = product.Year,
        };

        // The same product reported by two providers is one candidate (the better scoring of the two), or it would tie with itself.
        var candidates = answers
            .SelectMany(a => a.Value ?? [])
            .GroupBy(r => CatalogProjection.DuplicateKey(r.Title, r.Year, r.Platform))
            .Select(g => g.OrderByDescending(r => MatchScorer.Score(read, r.ToCandidate(), CatalogProjection.Profile)).First())
            .ToList();
        if (candidates.Count == 0)
        {
            return anyFailed
                ? BarcodeResolution.Pending(steps, product.Title)
                : new BarcodeResolution(ResolutionVerdict.NotFound, [], product.Title, steps);
        }

        var result = MatchScorer.Evaluate(read, candidates.Select(c => c.ToCandidate()).ToList(), CatalogProjection.Profile);
        var byKey = candidates.ToDictionary(c => $"{c.Provider}:{c.ExternalId}", StringComparer.Ordinal);
        steps.Add(new TraceStep("scorer", result.Outcome.ToString().ToLowerInvariant(), 0, (result.Ranked.Count > 0 ? result.Ranked[0].Score : (double?)null)));
        if (result.Outcome == MatchOutcome.NotFound)
        {
            return new BarcodeResolution(ResolutionVerdict.NotFound, [], product.Title, steps);
        }

        var ranked = result.Ranked.Take(Alternatives)
            .Select(r => new ResolutionOption(null, byKey[r.Release.Key], r.Score))
            .ToList();
        return new BarcodeResolution(
            result.Outcome == MatchOutcome.Matched ? ResolutionVerdict.Matched : ResolutionVerdict.Ambiguous, ranked, product.Title, steps);
    }

    private static string OutcomeOf(ProductLookupResult? result) => result?.Status switch
    {
        ProductLookupStatus.Found => "hit",
        ProductLookupStatus.NotFound => "miss",
        ProductLookupStatus.QuotaExhausted => "quota",
        _ => "failed",
    };

    private sealed record Attempt<T>(T? Value, bool Failed, TraceStep Step);

    /// <summary>
    /// Asks one provider and times it. A provider that throws or times out counts as failed; the log gets the provider's
    /// name and the kind of failure, never the message (which can carry the URL, and so the code or a key).
    /// </summary>
    private async Task<Attempt<T>> AskAsync<T>(string provider, Func<CancellationToken, Task<T>> ask, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var value = await ask(cancellationToken);
            return new Attempt<T>(value, false, new TraceStep(provider, value is null ? "miss" : "hit", Elapsed(started)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // A provider may fail in any way; none of them may fail the batch.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            logger.LogWarning("Provider {Provider} failed ({Failure}); leaving the code to try again later.", provider, exception.GetType().Name);
            return new Attempt<T>(default, true, new TraceStep(provider, "failed", Elapsed(started)));
        }
    }

    private static long Elapsed(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
