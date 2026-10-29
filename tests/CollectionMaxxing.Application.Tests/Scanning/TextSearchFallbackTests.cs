using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;
using Microsoft.Extensions.Logging.Abstractions;

namespace CollectionMaxxing.Application.Tests.Scanning;

/// <summary>
/// Stage 1 (#438): the providers are asked with title + author first; a title-only query is the fallback, not the
/// default. It fires only when the qualified round ran fine and found nothing — never on a hit, never on a failure.
/// </summary>
public class TextSearchFallbackTests
{
    private const string Title = "Suddenly You";
    private const string Author = "Lisa Kleypas";

    private static ProviderRelease KleypasBook() => new()
    {
        Provider = "openlibrary",
        ExternalId = "OL7353617M",
        Title = Title,
        Kind = WorkKind.Book,
        Year = 2001,
        Creators = [Author],
    };

    /// <summary>A provider that answers from a script and remembers every query it was asked.</summary>
    private sealed class RecordingProvider(Func<CatalogQuery, IReadOnlyList<ProviderRelease>> answer) : ICatalogProvider
    {
        public List<CatalogQuery> Queries { get; } = [];

        public string Name => "openlibrary";

        public bool Supports(WorkKind kind) => kind == WorkKind.Book;

        public Task<ProviderRelease?> LookupAsync(Identifier id, CancellationToken ct) => Task.FromResult<ProviderRelease?>(null);

        public Task<IReadOnlyList<ProviderRelease>> SearchAsync(CatalogQuery query, CancellationToken ct)
        {
            Queries.Add(query);
            return Task.FromResult(answer(query));
        }
    }

    private static TextSearchResolver Resolver(params ICatalogProvider[] providers) => new(
        null!, null!, providers, new ScanningOptions(), NullLogger<TextSearchResolver>.Instance);

    [Fact]
    public async Task An_author_qualified_search_that_finds_nothing_falls_back_to_title_only()
    {
        var book = KleypasBook();
        var provider = new RecordingProvider(query => query.Creator is null ? [book] : []);
        var resolver = Resolver(provider);

        var answers = await resolver.AskProvidersAsync(
            new CatalogQuery { Title = Title, Creator = Author, Kind = WorkKind.Book }, CancellationToken.None, CancellationToken.None);

        answers.Found.ShouldBe([book]);
        answers.AnyFailed.ShouldBeFalse();
        provider.Queries.Count.ShouldBe(2);
        provider.Queries[0].Creator.ShouldBe(Author);
        provider.Queries[1].Title.ShouldBe(Title);
        provider.Queries[1].Creator.ShouldBeNull();
    }

    [Fact]
    public async Task An_author_qualified_search_that_hits_is_never_asked_twice()
    {
        var book = KleypasBook();
        var provider = new RecordingProvider(_ => [book]);
        var resolver = Resolver(provider);

        var answers = await resolver.AskProvidersAsync(
            new CatalogQuery { Title = Title, Creator = Author, Kind = WorkKind.Book }, CancellationToken.None, CancellationToken.None);

        answers.Found.ShouldBe([book]);
        provider.Queries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task An_author_qualified_search_that_finds_only_unrelated_books_falls_back_to_title_only_and_keeps_both()
    {
        // A bare-words search with a misread author (Google Books) still answers, with books that have nothing to do with the
        // title; that used to stop the fallback, so a spine whose author was misread found nothing (first eval run).
        var book = KleypasBook();
        var unrelated = KleypasBook() with { ExternalId = "OL1M", Title = "A Field Guide to North American Birds" };
        var provider = new RecordingProvider(query => query.Creator is null ? [book] : [unrelated]);
        var resolver = Resolver(provider);

        var answers = await resolver.AskProvidersAsync(
            new CatalogQuery { Title = Title, Creator = "LISA KLEYPAs NOVEL", Kind = WorkKind.Book }, CancellationToken.None, CancellationToken.None);

        answers.Found.ShouldBe([unrelated, book]);
        provider.Queries.Count.ShouldBe(2);
        provider.Queries[1].Creator.ShouldBeNull();
    }

    [Fact]
    public async Task A_failed_provider_is_not_asked_twice()
    {
        var provider = new RecordingProvider(_ => throw new InvalidOperationException("the provider is down"));
        var resolver = Resolver(provider);

        var answers = await resolver.AskProvidersAsync(
            new CatalogQuery { Title = Title, Creator = Author, Kind = WorkKind.Book }, CancellationToken.None, CancellationToken.None);

        answers.Found.ShouldBeEmpty();
        answers.AnyFailed.ShouldBeTrue();
        provider.Queries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Without_an_author_there_is_nothing_to_fall_back_from()
    {
        var provider = new RecordingProvider(_ => []);
        var resolver = Resolver(provider);

        var answers = await resolver.AskProvidersAsync(
            new CatalogQuery { Title = Title, Kind = WorkKind.Book }, CancellationToken.None, CancellationToken.None);

        answers.Found.ShouldBeEmpty();
        provider.Queries.Count.ShouldBe(1);
    }
}
