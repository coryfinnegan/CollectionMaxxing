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
    private sealed class RecordingProvider(Func<CatalogQuery, IReadOnlyList<ProviderRelease>> answer, string name = "openlibrary") : ICatalogProvider
    {
        public List<CatalogQuery> Queries { get; } = [];

        public string Name => name;

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

    [Fact]
    public async Task Each_provider_falls_back_on_its_own_so_a_close_companion_elsewhere_does_not_stop_it()
    {
        // Second eval run: with a misread author, one provider found nothing while the other's bare-words search answered with
        // a companion volume of the same title. That counted as close for both, so the real book was never asked for.
        var book = KleypasBook();
        var companion = KleypasBook() with { Provider = "googlebooks", ExternalId = "gb-1", Title = Title + ": A Reader's Companion" };
        var strict = new RecordingProvider(query => query.Creator is null ? [book] : []);
        var loose = new RecordingProvider(_ => [companion], "googlebooks");
        var resolver = Resolver(strict, loose);

        var answers = await resolver.AskProvidersAsync(
            new CatalogQuery { Title = Title, Creator = "LISA KLEYPOS", Kind = WorkKind.Book }, CancellationToken.None, CancellationToken.None);

        answers.Found.ShouldContain(book);
        answers.Found.ShouldContain(companion);
        strict.Queries.Count.ShouldBe(2);
        loose.Queries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_read_with_junk_after_the_title_is_asked_again_without_its_last_words()
    {
        // OCR runs a spine's other print into the title ("... NIGHT QXRTVZ"): the title search finds nothing with it.
        var book = KleypasBook();
        var provider = new RecordingProvider(query => query.Title == Title ? [book] : []);
        var resolver = Resolver(provider);

        var answers = await resolver.AskProvidersAsync(
            new CatalogQuery { Title = Title + " AVON QXRTVZ", Kind = WorkKind.Book }, CancellationToken.None, CancellationToken.None);

        answers.Found.ShouldBe([book]);
        provider.Queries.Select(q => q.Title).ShouldBe([Title + " AVON QXRTVZ", Title + " AVON", Title]);
    }

    [Fact]
    public async Task At_most_two_words_are_dropped_and_at_least_two_are_left()
    {
        var provider = new RecordingProvider(_ => []);
        var resolver = Resolver(provider);

        await resolver.AskProvidersAsync(new CatalogQuery { Title = "one two three four five", Kind = WorkKind.Book }, CancellationToken.None, CancellationToken.None);
        await resolver.AskProvidersAsync(new CatalogQuery { Title = "one two three", Kind = WorkKind.Book }, CancellationToken.None, CancellationToken.None);

        provider.Queries.Select(q => q.Title).ShouldBe(["one two three four five", "one two three four", "one two three", "one two three", "one two"]);
    }

    [Fact]
    public async Task The_shortened_titles_are_asked_without_the_author()
    {
        var provider = new RecordingProvider(_ => []);
        var resolver = Resolver(provider);

        await resolver.AskProvidersAsync(
            new CatalogQuery { Title = Title + " AVON", Creator = Author, Kind = WorkKind.Book }, CancellationToken.None, CancellationToken.None);

        provider.Queries.Select(q => (q.Title, q.Creator)).ShouldBe([(Title + " AVON", Author), (Title + " AVON", null), (Title, null)]);
    }

    [Fact]
    public async Task A_provider_that_answers_close_to_a_shortened_title_is_not_asked_shorter()
    {
        var book = KleypasBook();
        var provider = new RecordingProvider(query => query.Title == Title + " AVON" ? [book] : []);
        var resolver = Resolver(provider);

        var answers = await resolver.AskProvidersAsync(
            new CatalogQuery { Title = Title + " AVON XQZT", Kind = WorkKind.Book }, CancellationToken.None, CancellationToken.None);

        answers.Found.ShouldBe([book]);
        provider.Queries.Count.ShouldBe(2);
    }
}
