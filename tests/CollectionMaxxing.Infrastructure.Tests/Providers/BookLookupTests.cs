using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace CollectionMaxxing.Infrastructure.Tests.Providers;

/// <summary>An ISBN lookup end to end: the permanent local copy, then Open Library and Google Books (WireMock and the test Postgres).</summary>
public class BookLookupTests(PostgresFixture db) : DatabaseTest(db)
{
    private static readonly string[] Books = ["openlibrary", "googlebooks"];

    private static readonly string EditionPath = $"/isbn/{ProviderHost.ValidIsbn}.json";

    private const string GooglePath = "/books/v1/volumes";

    private static void Stub(ProviderHost host, string path, int status, string? fixture = null, TimeSpan? delay = null)
    {
        var response = Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json");
        if (fixture is not null)
        {
            response = response.WithBody(ProviderHost.Fixture(fixture));
        }

        host.Server.Given(Request.Create().WithPath(path).UsingGet()).RespondWith(delay is { } wait ? response.WithDelay(wait) : response);
    }

    private static void StubOpenLibraryFound(ProviderHost host)
    {
        Stub(host, EditionPath, 200, "openlibrary-isbn-found.json");
        Stub(host, "/authors/OL34184A.json", 200, "openlibrary-author.json");
    }

    private static BookLookup LookupOn(ProviderHost host, CapturingLogger? log = null) =>
        new(host.Services.GetServices<ICatalogProvider>(), host.Get<IIsbnStore>(), log ?? new CapturingLogger());

    private static int ProviderCalls(ProviderHost host) => host.Server.LogEntries.Count;

    [Fact]
    public async Task A_book_that_was_found_before_is_answered_locally_with_no_provider_call()
    {
        using var host = ProviderHost.Create(Db);
        StubOpenLibraryFound(host);
        var lookup = LookupOn(host);
        (await lookup.LookupAsync(ProviderCase.Isbn, Books, null, default)).Release.ShouldNotBeNull();
        var callsAfterFirst = ProviderCalls(host);

        var again = await lookup.LookupAsync(ProviderCase.Isbn, Books, null, default);

        again.Release.ShouldNotBeNull();
        again.Release.Title.ShouldBe("Fantastic Mr. Fox: a play");
        ProviderCalls(host).ShouldBe(callsAfterFirst);
        again.Steps.ShouldBe([new BookLookupStep(BookSource.LocalCache, "hit", again.Steps[0].Ms)]);
    }

    [Fact]
    public async Task The_whole_answer_is_kept_and_comes_back_whole()
    {
        using var host = ProviderHost.Create(Db);
        StubOpenLibraryFound(host);
        await LookupOn(host).LookupAsync(ProviderCase.Isbn, Books, null, default);

        var kept = (await LookupOn(host).LookupAsync(ProviderCase.Isbn, Books, null, default)).Release;

        kept.ShouldNotBeNull();
        kept.Provider.ShouldBe("openlibrary");
        kept.ExternalId.ShouldBe("OL7353617M");
        kept.Creators.ShouldBe(["Roald Dahl"]);
        kept.Publisher.ShouldBe("Puffin");
        kept.Year.ShouldBe(1988);
        kept.CoverUrl.ShouldBe("https://covers.openlibrary.org/b/id/8739161-L.jpg");
        kept.Identifiers.ShouldContain(ProviderCase.Isbn);
    }

    [Fact]
    public async Task A_book_that_was_never_found_falls_through_to_the_providers_and_is_then_kept()
    {
        using var host = ProviderHost.Create(Db);
        StubOpenLibraryFound(host);

        var first = await LookupOn(host).LookupAsync(ProviderCase.Isbn, Books, null, default);

        first.Release.ShouldNotBeNull();
        first.Steps.Select(s => (s.Source, s.Outcome)).ShouldContain((BookSource.LocalCache, "miss"));
        host.Requests(EditionPath).ShouldBe(1);
    }

    [Fact]
    public async Task A_book_nobody_knows_is_not_kept_so_the_next_scan_asks_again()
    {
        using var host = ProviderHost.Create(Db);
        Stub(host, EditionPath, 404);
        Stub(host, GooglePath, 200, "googlebooks-empty.json");

        var first = await LookupOn(host).LookupAsync(ProviderCase.Isbn, Books, null, default);
        first.Release.ShouldBeNull();
        first.Failed.ShouldBeFalse();
        var callsAfterFirst = ProviderCalls(host);

        var second = await LookupOn(host).LookupAsync(ProviderCase.Isbn, Books, null, default);

        second.Release.ShouldBeNull();
        ProviderCalls(host).ShouldBeGreaterThan(callsAfterFirst);
        host.Requests(EditionPath).ShouldBe(2);
    }

    [Fact]
    public async Task A_failed_lookup_is_not_kept_and_works_once_the_provider_is_back()
    {
        using var host = ProviderHost.Create(Db, settings: new() { ["Providers:GoogleBooks:ApiKey"] = null, ["Providers:Resilience:MaxRetryAttempts"] = "1" });
        host.Server.Given(Request.Create().WithPath(EditionPath).UsingGet()).AtPriority(1).RespondWith(Response.Create().WithStatusCode(503));
        var failed = await LookupOn(host).LookupAsync(ProviderCase.Isbn, Books, null, default);
        failed.Release.ShouldBeNull();

        host.Server.Reset();
        StubOpenLibraryFound(host);
        var later = await LookupOn(host).LookupAsync(ProviderCase.Isbn, Books, null, default);

        later.Release.ShouldNotBeNull();
    }

    [Fact]
    public async Task Google_books_is_skipped_without_a_key_and_open_library_still_answers()
    {
        using var host = ProviderHost.Create(Db, settings: new() { ["Providers:GoogleBooks:ApiKey"] = null });
        StubOpenLibraryFound(host);

        var result = await LookupOn(host).LookupAsync(ProviderCase.Isbn, Books, null, default);

        result.Release.ShouldNotBeNull();
        result.Failed.ShouldBeFalse();
        host.Requests(GooglePath).ShouldBe(0);
        result.Steps.ShouldNotContain(s => s.Source == "googlebooks");
    }

    [Fact]
    public async Task A_scan_succeeds_when_only_google_books_answers()
    {
        using var host = ProviderHost.Create(Db, settings: new() { ["Providers:Resilience:MaxRetryAttempts"] = "1" });
        Stub(host, EditionPath, 503);
        Stub(host, GooglePath, 200, "googlebooks-found.json");

        var result = await LookupOn(host).LookupAsync(ProviderCase.Isbn, Books, null, default);

        result.Release.ShouldNotBeNull();
        result.Release.Provider.ShouldBe("googlebooks");
    }

    [Fact]
    public async Task A_quick_google_books_answer_does_not_wait_for_a_slow_open_library()
    {
        using var host = ProviderHost.Create(Db);
        Stub(host, EditionPath, 200, "openlibrary-isbn-found.json", delay: TimeSpan.FromSeconds(4));
        Stub(host, GooglePath, 200, "googlebooks-found.json");

        var started = DateTimeOffset.UtcNow;
        var result = await LookupOn(host).LookupAsync(ProviderCase.Isbn, Books, null, default);

        result.Release.ShouldNotBeNull();
        result.Release.Provider.ShouldBe("googlebooks");
        (DateTimeOffset.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task The_log_tells_a_cache_hit_from_a_provider_hit()
    {
        using var host = ProviderHost.Create(Db);
        StubOpenLibraryFound(host);
        var log = new CapturingLogger();

        await LookupOn(host, log).LookupAsync(ProviderCase.Isbn, Books, null, default);
        await LookupOn(host, log).LookupAsync(ProviderCase.Isbn, Books, null, default);

        log.Messages.ShouldBe(["Book lookup answered by provider openlibrary.", "Book lookup served from the local cache, no provider called."]);
        log.Messages.ShouldAllBe(m => !m.Contains(ProviderHost.ValidIsbn)); // a barcode never goes to the log
    }

    [Fact]
    public async Task A_kept_book_stays_kept_for_years()
    {
        using var host = ProviderHost.Create(Db, fakeTime: true);
        StubOpenLibraryFound(host);
        await LookupOn(host).LookupAsync(ProviderCase.Isbn, Books, null, default);
        var callsAfterFirst = ProviderCalls(host);

        host.Fake.Advance(TimeSpan.FromDays(365 * 20));
        var later = await LookupOn(host).LookupAsync(ProviderCase.Isbn, Books, null, default);

        later.Release.ShouldNotBeNull();
        ProviderCalls(host).ShouldBe(callsAfterFirst);
    }

    [Fact]
    public async Task A_first_answer_without_a_cover_waits_for_an_answer_with_one()
    {
        using var host = ProviderHost.Create(Db);
        Stub(host, EditionPath, 200, "openlibrary-isbn-no-cover.json");
        Stub(host, "/authors/OL34184A.json", 200, "openlibrary-author.json");
        Stub(host, GooglePath, 200, "googlebooks-found.json", delay: TimeSpan.FromMilliseconds(500));

        var result = await LookupOn(host).LookupAsync(ProviderCase.Isbn, Books, null, default);

        result.Release.ShouldNotBeNull();
        result.Release.Provider.ShouldBe("googlebooks");
        result.Release.CoverUrl.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_first_answer_with_a_cover_does_not_wait_for_the_other_provider()
    {
        using var host = ProviderHost.Create(Db);
        StubOpenLibraryFound(host);
        Stub(host, GooglePath, 200, "googlebooks-found.json", delay: TimeSpan.FromSeconds(4));

        var started = DateTimeOffset.UtcNow;
        var result = await LookupOn(host).LookupAsync(ProviderCase.Isbn, Books, null, default);

        result.Release.ShouldNotBeNull();
        result.Release.Provider.ShouldBe("openlibrary");
        (DateTimeOffset.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Two_answers_without_a_cover_return_the_first_and_are_kept()
    {
        using var host = ProviderHost.Create(Db);
        Stub(host, EditionPath, 200, "openlibrary-isbn-no-cover.json");
        Stub(host, "/authors/OL34184A.json", 200, "openlibrary-author.json");
        Stub(host, GooglePath, 200, "googlebooks-empty.json");

        var first = await LookupOn(host).LookupAsync(ProviderCase.Isbn, Books, null, default);
        first.Release.ShouldNotBeNull();
        first.Release.CoverUrl.ShouldBeNull();
        var callsAfterFirst = ProviderCalls(host);

        var again = await LookupOn(host).LookupAsync(ProviderCase.Isbn, Books, null, default);

        again.Release.ShouldNotBeNull();
        ProviderCalls(host).ShouldBe(callsAfterFirst);
    }

    [Fact]
    public async Task The_log_names_the_requested_providers_that_were_never_consulted()
    {
        using var host = ProviderHost.Create(Db, settings: new() { ["Providers:GoogleBooks:ApiKey"] = null });
        StubOpenLibraryFound(host);
        var log = new CapturingLogger();

        var result = await LookupOn(host, log).LookupAsync(ProviderCase.Isbn, Books, null, default);

        result.Release.ShouldNotBeNull();
        log.Messages.ShouldContain("Book lookup did not consult googlebooks (no enabled provider by that name).");
        log.Messages.ShouldAllBe(m => !m.Contains(ProviderHost.ValidIsbn)); // a barcode never goes to the log
    }

    private sealed class CapturingLogger : ILogger<BookLookup>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
