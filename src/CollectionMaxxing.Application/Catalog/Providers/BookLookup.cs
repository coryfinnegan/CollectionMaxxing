using System.Diagnostics;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;
using Microsoft.Extensions.Logging;

namespace CollectionMaxxing.Application.Catalog.Providers;

/// <summary>Where a book answer can come from besides a provider.</summary>
public static class BookSource
{
    public const string LocalCache = "local-cache";
}

/// <summary>One step of a book lookup: the local store or one provider, how it ended, and how long it took.</summary>
public sealed record BookLookupStep(string Source, string Outcome, long Ms);

/// <summary>The answer of <see cref="BookLookup"/>. <see cref="Failed"/> is true when a provider threw or timed out and nothing was found.</summary>
public sealed record BookLookupResult(ProviderRelease? Release, bool Failed, IReadOnlyList<BookLookupStep> Steps);

/// <summary>The permanent local copy of every ISBN that has been found, so it is never looked up outside again.</summary>
public interface IIsbnStore
{
    Task<ProviderRelease?> FindAsync(string isbn13, CancellationToken ct);

    /// <summary>Keeps the full answer for good. Only found books are saved: a barcode nobody knew is asked again next time.</summary>
    Task SaveAsync(string isbn13, ProviderRelease release, CancellationToken ct);
}

/// <summary>
/// Looks a book up by ISBN: the local store first, then every book provider that is switched on at once, taking whichever
/// answers first with a book. A slow Open Library does not hold up a quick Google Books answer, and one provider failing does
/// not stop the other. A found book is saved for good; "not found" and failures are never saved.
/// </summary>
public sealed class BookLookup(IEnumerable<ICatalogProvider> providers, IIsbnStore store, ILogger<BookLookup> logger)
{
    private readonly IReadOnlyList<ICatalogProvider> _providers = providers.ToList();

    /// <param name="id">The ISBN-13.</param>
    /// <param name="providerNames">The providers to ask, by name; null asks every provider that knows books.</param>
    /// <param name="timeout">How long the providers have in all; null leaves that to the caller's token. Running out counts as a failure.</param>
    public async Task<BookLookupResult> LookupAsync(
        Identifier id, IReadOnlyCollection<string>? providerNames, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        var steps = new List<BookLookupStep>();
        var started = Stopwatch.GetTimestamp();

        try
        {
            if (await store.FindAsync(id.Value, cancellationToken) is { } cached)
            {
                steps.Add(new BookLookupStep(BookSource.LocalCache, "hit", Elapsed(started)));
                logger.LogInformation("Book lookup served from the local cache, no provider called.");
                return new BookLookupResult(cached, false, steps);
            }

            steps.Add(new BookLookupStep(BookSource.LocalCache, "miss", Elapsed(started)));
        }
#pragma warning disable CA1031 // A broken local store must not stop the providers from answering.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            logger.LogWarning("The local book cache could not be read ({Failure}); asking the providers.", exception.GetType().Name);
            steps.Add(new BookLookupStep(BookSource.LocalCache, "failed", Elapsed(started)));
        }

        var asked = _providers
            .Where(p => (providerNames is null || providerNames.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) && p.Supports(WorkKind.Book))
            .ToList();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } limit)
        {
            deadline.CancelAfter(limit);
        }

        using var race = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var pending = asked.Select(p => AskAsync(p, id, race.Token)).ToList();
        var anyFailed = false;
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending);
            pending.Remove(done);
            var attempt = await done;
            steps.Add(attempt.Step);
            anyFailed |= attempt.Failed;
            if (attempt.Release is not { } book)
            {
                continue;
            }

            await race.CancelAsync(); // the slower provider is no longer needed
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Book lookup answered by provider {Provider}.", attempt.Step.Source);
            }

            await SaveAsync(id, book, cancellationToken);
            return new BookLookupResult(book, false, steps);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new BookLookupResult(null, anyFailed || deadline.IsCancellationRequested, steps);
    }

    private async Task SaveAsync(Identifier id, ProviderRelease book, CancellationToken cancellationToken)
    {
        try
        {
            await store.SaveAsync(id.Value, book, cancellationToken);
        }
#pragma warning disable CA1031 // The book was found; failing to remember it must not lose the answer.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            logger.LogWarning("The found book could not be saved to the local cache ({Failure}).", exception.GetType().Name);
        }
    }

    private sealed record Attempt(ProviderRelease? Release, bool Failed, BookLookupStep Step);

    private async Task<Attempt> AskAsync(ICatalogProvider provider, Identifier id, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var release = await provider.LookupAsync(id, cancellationToken);
            return new Attempt(release, false, new BookLookupStep(provider.Name, release is null ? "miss" : "hit", Elapsed(started)));
        }
        catch (OperationCanceledException)
        {
            // Either the caller gave up, or another provider already won: not a failure of this provider.
            return new Attempt(null, false, new BookLookupStep(provider.Name, "cancelled", Elapsed(started)));
        }
#pragma warning disable CA1031 // A provider may fail in any way; none of them may fail the lookup.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            logger.LogWarning("Provider {Provider} failed ({Failure}); leaving the code to try again later.", provider.Name, exception.GetType().Name);
            return new Attempt(null, true, new BookLookupStep(provider.Name, "failed", Elapsed(started)));
        }
    }

    private static long Elapsed(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
