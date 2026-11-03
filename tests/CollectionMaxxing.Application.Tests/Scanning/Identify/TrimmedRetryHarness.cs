using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Identifiers;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Application.Tests.Scanning.Identify;

/// <summary>
/// The fakes of the trimmed-spine-retry tests, shared by the live-grounding and the eval-grounding ones: a catalogue that holds
/// books by title and remembers what it was asked, and a provider that finds nothing (or is down).
/// </summary>
internal static class TrimmedRetryHarness
{
    /// <summary>A book the catalogue holds. Titles are neutral.</summary>
    public static LocalHit Book(string title)
    {
        var work = new Work { Id = Guid.NewGuid(), Kind = WorkKind.Book, Title = title, NormalizedTitle = TitleNormalizer.Normalize(title), CreatorsJson = "[]" };
        return new LocalHit(new Release { Id = Guid.NewGuid(), WorkId = work.Id, Format = ReleaseFormat.Hardcover }, work);
    }

    /// <summary>A book the catalogue holds, with its author. Titles and names are neutral.</summary>
    public static LocalHit BookBy(string title, string creator)
    {
        var book = Book(title);
        book.Work.CreatorsJson = $"[\"{creator}\"]";
        return book;
    }

    public sealed class FixedScoring : IOptionsMonitor<ScoringOptions>
    {
        public ScoringOptions CurrentValue { get; } = new();

        public ScoringOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ScoringOptions, string?> listener) => null;
    }

    /// <summary>
    /// A catalogue that holds the books it was given and answers a lookup with the books whose (normalised) title is exactly the one
    /// asked for, so a test says which span finds a release. Remembers every title it was asked for, in order.
    /// </summary>
    public sealed class Catalogue(params LocalHit[] books) : ILocalCatalogSearch
    {
        private readonly object _gate = new();
        private int _inFlight;

        /// <summary>Every title asked for, in the order asked.</summary>
        public List<string> Lookups { get; } = [];

        /// <summary>The titles asked for together, one entry per database call.</summary>
        public List<string[]> Batches { get; } = [];

        /// <summary>The most database calls that were under way at the same moment: a DbContext serves one at a time, so it must stay 1.</summary>
        public int MostAtOnce { get; private set; }

        public async Task<IReadOnlyList<LocalHit>> FindAsync(LocalSearch search, CancellationToken cancellationToken)
        {
            await UsingTheDatabaseAsync([search.NormalizedTitle]);
            return Found(search);
        }

        async Task<IReadOnlyList<IReadOnlyList<LocalHit>>> ILocalCatalogSearch.FindManyAsync(
            IReadOnlyList<LocalSearch> searches, CancellationToken cancellationToken)
        {
            await UsingTheDatabaseAsync([.. searches.Select(s => s.NormalizedTitle)]);
            return [.. searches.Select(Found)];
        }

        private async Task UsingTheDatabaseAsync(string[] titles)
        {
            lock (_gate)
            {
                Lookups.AddRange(titles);
                Batches.Add(titles);
                MostAtOnce = Math.Max(MostAtOnce, ++_inFlight);
            }

            await Task.Delay(5); // long enough for a call that is not kept apart from another to overlap it
            lock (_gate)
            {
                _inFlight--;
            }
        }

        private IReadOnlyList<LocalHit> Found(LocalSearch search) =>
            [.. books.Where(book => string.Equals(book.Work.NormalizedTitle, search.NormalizedTitle, StringComparison.Ordinal))];
    }

    /// <summary>A book a provider knows. Titles are neutral.</summary>
    public static ProviderRelease Known(string title, string externalId = "b1") => new()
    {
        Provider = "openlibrary",
        ExternalId = externalId,
        Title = title,
        Kind = WorkKind.Book,
        Format = ReleaseFormat.Paperback,
        Year = 2012,
    };

    /// <summary>
    /// A provider that knows the books it was given (it finds one for a search of exactly its title, whatever the case) and no other,
    /// or is down. Remembers the titles it was asked about.
    /// </summary>
    public sealed class Providers(bool down = false, params ProviderRelease[] known) : ICatalogProvider
    {
        private readonly object _gate = new();

        public List<string> Titles { get; } = [];

        public string Name => "openlibrary";

        public bool Supports(WorkKind kind) => kind == WorkKind.Book;

        public Task<ProviderRelease?> LookupAsync(Identifier id, CancellationToken ct) => Task.FromResult<ProviderRelease?>(null);

        public Task<IReadOnlyList<ProviderRelease>> SearchAsync(CatalogQuery query, CancellationToken ct)
        {
            lock (_gate)
            {
                Titles.Add(query.Title);
            }

            return down
                ? throw new InvalidOperationException("the provider is down")
                : Task.FromResult<IReadOnlyList<ProviderRelease>>([.. known.Where(book => string.Equals(book.Title, query.Title, StringComparison.OrdinalIgnoreCase))]);
        }
    }
}
