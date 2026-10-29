using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Infrastructure.Providers.GoogleBooks;
using CollectionMaxxing.Infrastructure.Providers.OpenLibrary;
using CollectionMaxxing.Infrastructure.Providers.UpcItemDb;
using CollectionMaxxing.TestSupport;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace CollectionMaxxing.Infrastructure.Tests.Providers;

/// <summary>
/// An adapter gives up quietly on a provider it cannot reach (<see cref="ProviderBehaviourTests"/>), so it says so through the
/// caller's failure report instead: a 503, a timeout or a refused connection marks it; a provider that answered "nothing"
/// (a 404, an empty answer, an answer kept in the cache, a spent quota) does not.
/// </summary>
public class ProviderFailureReportTests(PostgresFixture db) : DatabaseTest(db)
{
    private static Dictionary<string, string?> OneRetry() => new()
    {
        ["Providers:Resilience:MaxRetryAttempts"] = "1",
        ["Providers:Resilience:RetryDelay"] = "00:00:00.050",
        ["Providers:Resilience:AttemptTimeout"] = "00:00:00.300",
    };

    private static async Task<bool> FailedAsync(Func<Task> ask)
    {
        using var report = ProviderCalls.Observe();
        await ask();
        return report.Failed;
    }

    [Theory]
    [MemberData(nameof(ProviderCase.Names), MemberType = typeof(ProviderCase))]
    public async Task A_provider_that_answers_503_marks_the_report(string name)
    {
        using var host = ProviderHost.Create(Db, settings: OneRetry());
        var provider = ProviderCase.Named(name);
        provider.RespondWith(host, 503, null);

        (await FailedAsync(() => provider.Ask(host))).ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(ProviderCase.Names), MemberType = typeof(ProviderCase))]
    public async Task A_provider_that_never_answers_in_time_marks_the_report(string name)
    {
        using var host = ProviderHost.Create(Db, settings: OneRetry());
        var provider = ProviderCase.Named(name);
        provider.RespondWith(host, 200, provider.FoundFixture, delay: TimeSpan.FromSeconds(3));

        (await FailedAsync(() => provider.Ask(host))).ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(ProviderCase.Names), MemberType = typeof(ProviderCase))]
    public async Task A_provider_that_refuses_the_connection_marks_the_report(string name)
    {
        using var host = ProviderHost.Create(Db, settings: OneRetry());
        var provider = ProviderCase.Named(name);
        host.Server.Stop(); // nothing listens on the port any more

        using (ProviderCalls.FailFast())
        {
            (await FailedAsync(() => provider.Ask(host))).ShouldBeTrue();
        }
    }

    [Theory]
    [MemberData(nameof(ProviderCase.Names), MemberType = typeof(ProviderCase))]
    public async Task A_404_does_not_mark_the_report(string name)
    {
        using var host = ProviderHost.Create(Db, settings: OneRetry());
        var provider = ProviderCase.Named(name);
        provider.RespondWith(host, 404, null);

        (await FailedAsync(() => provider.Ask(host))).ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(ProviderCase.Names), MemberType = typeof(ProviderCase))]
    public async Task An_empty_answer_does_not_mark_the_report(string name)
    {
        using var host = ProviderHost.Create(Db, settings: OneRetry());
        var provider = ProviderCase.Named(name);
        provider.RespondWith(host, 200, provider.EmptyFixture);

        (await FailedAsync(() => provider.Ask(host))).ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(ProviderCase.Names), MemberType = typeof(ProviderCase))]
    public async Task A_provider_that_answers_does_not_mark_the_report(string name)
    {
        using var host = ProviderHost.Create(Db, settings: OneRetry());
        var provider = ProviderCase.Named(name);
        provider.RespondWith(host, 200, provider.FoundFixture);
        if (name == "openlibrary")
        {
            host.Server.Given(Request.Create().WithPath("/authors/OL34184A.json").UsingGet())
                .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                    .WithBody(ProviderHost.Fixture("openlibrary-author.json")));
        }

        (await FailedAsync(() => provider.Ask(host))).ShouldBeFalse();
    }

    [Fact]
    public async Task A_spent_quota_does_not_mark_the_report()
    {
        // Out of quota is not an outage: asking again a minute later is refused the same way.
        using var host = ProviderHost.Create(Db, settings: new() { ["Providers:UpcItemDb:DailyLimit"] = "0" });
        var upc = host.Get<UpcItemDbProvider>();

        using var report = ProviderCalls.Observe();
        (await upc.LookupWithStatusAsync(ProviderCase.Upc, default)).Status.ShouldBe(UpcLookupStatus.QuotaExhausted);

        report.Failed.ShouldBeFalse();
    }

    /// <summary>The two book providers' title searches (their <see cref="ProviderCase"/> asks are ISBN lookups).</summary>
    public static TheoryData<string> BookSearches() => new("openlibrary", "googlebooks");

    private static readonly CatalogQuery Search = new() { Title = "Lanterns Over Quell Harbour", Kind = WorkKind.Book };

    private static (string Path, string EmptyFixture, Func<ProviderHost, Task<int>> Ask) BookSearch(string name) => name switch
    {
        "openlibrary" => ("/search.json", "openlibrary-search-empty.json",
            async host => (await host.Get<OpenLibraryProvider>().SearchAsync(Search, default)).Count),
        _ => ("/books/v1/volumes", "googlebooks-empty.json",
            async host => (await host.Get<GoogleBooksProvider>().SearchAsync(Search, default)).Count),
    };

    private static void Stub(ProviderHost host, string path, int status, string? fixture = null)
    {
        var response = Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json");
        host.Server.Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(fixture is null ? response : response.WithBody(ProviderHost.Fixture(fixture)));
    }

    [Theory]
    [MemberData(nameof(BookSearches))]
    public async Task A_book_search_the_provider_answers_503_marks_the_report(string name)
    {
        using var host = ProviderHost.Create(Db, settings: OneRetry());
        var (path, _, ask) = BookSearch(name);
        Stub(host, path, 503);

        (await FailedAsync(() => ask(host))).ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(BookSearches))]
    public async Task A_book_search_with_an_empty_answer_does_not_mark_the_report(string name)
    {
        using var host = ProviderHost.Create(Db, settings: OneRetry());
        var (path, empty, ask) = BookSearch(name);
        Stub(host, path, 200, empty);

        (await FailedAsync(() => ask(host))).ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(BookSearches))]
    public async Task A_book_search_answered_from_the_cache_does_not_mark_the_report(string name)
    {
        // The empty answer is kept for a day; the provider going down behind it changes nothing until then.
        using var host = ProviderHost.Create(Db, settings: OneRetry());
        var (path, empty, ask) = BookSearch(name);
        Stub(host, path, 200, empty);
        await ask(host);
        host.Server.Reset();
        Stub(host, path, 503);

        (await FailedAsync(() => ask(host))).ShouldBeFalse();
        host.Requests(path).ShouldBe(0);
    }

    [Fact]
    public async Task An_open_library_title_search_that_fails_does_not_go_on_to_the_general_search()
    {
        // Both searches are the same endpoint: when the title search cannot be had, the general one cannot either.
        using var host = ProviderHost.Create(Db, settings: OneRetry());
        Stub(host, "/search.json", 503);

        using (ProviderCalls.FailFast())
        {
            (await FailedAsync(() => BookSearch("openlibrary").Ask(host))).ShouldBeTrue();
        }

        host.Requests("/search.json").ShouldBe(1);
    }
}
