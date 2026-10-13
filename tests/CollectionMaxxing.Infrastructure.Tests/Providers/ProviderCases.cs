using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Infrastructure.Providers.GoogleBooks;
using CollectionMaxxing.Infrastructure.Providers.Igdb;
using CollectionMaxxing.Infrastructure.Providers.OpenLibrary;
using CollectionMaxxing.Infrastructure.Providers.Tmdb;
using CollectionMaxxing.Infrastructure.Providers.UpcItemDb;
using WireMock;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Types;
using WireMock.Util;

namespace CollectionMaxxing.Infrastructure.Tests.Providers;

/// <summary>
/// One way to ask each provider a question, so the same behaviour tests (found, not found, malformed, 429, 500, timeout,
/// cache) run against all five adapters.
/// </summary>
internal sealed record ProviderCase(
    string Name,
    string Path,
    bool Post,
    string FoundFixture,
    string EmptyFixture,
    Func<ProviderHost, Task<int>> Ask)
{
    public static readonly Identifier Isbn = Identifier.TryParse(ProviderHost.ValidIsbn, out var isbn) ? isbn : throw new InvalidOperationException();

    public static readonly Identifier Upc = Identifier.TryParse("045496830434", out var upc) ? upc : throw new InvalidOperationException();

    public static IReadOnlyList<ProviderCase> All { get; } =
    [
        new("openlibrary", "/api/books", false, "openlibrary-isbn-found.json", "openlibrary-isbn-notfound.json",
            async host => Count(await host.Get<OpenLibraryProvider>().LookupAsync(Isbn, default))),
        new("googlebooks", "/books/v1/volumes", false, "googlebooks-found.json", "googlebooks-empty.json",
            async host => Count(await host.Get<GoogleBooksProvider>().LookupAsync(Isbn, default))),
        new("upcitemdb", "/prod/trial/lookup", false, "upcitemdb-game.json", "upcitemdb-empty.json",
            async host => Count(await host.Get<UpcItemDbProvider>().LookupAsync(Upc, default))),
        new("tmdb", "/3/search/movie", false, "tmdb-movie-search.json", "tmdb-empty.json",
            async host => (await host.Get<TmdbProvider>().SearchAsync(
                new CatalogQuery { Title = "Casablanca", Kind = WorkKind.Film }, default)).Count),
        new("igdb", "/v4/games", true, "igdb-games.json", "igdb-empty.json",
            async host =>
            {
                host.StubIgdbToken();
                return (await host.Get<IgdbProvider>().SearchAsync(
                    new CatalogQuery { Title = "Super Mario 64", Kind = WorkKind.Game }, default)).Count;
            }),
    ];

    public static TheoryData<string> Names() => new(All.Select(c => c.Name));

    public static ProviderCase Named(string name) => All.Single(c => c.Name == name);

    private static int Count(ProviderRelease? release) => release is null ? 0 : 1;

    public IRequestBuilder Request()
    {
        var request = Request_.Create().WithPath(Path);
        return Post ? request.UsingPost() : request.UsingGet();
    }

    /// <summary>Answers every call with the fixture.</summary>
    public void RespondWith(ProviderHost host, int status, string? fixture, TimeSpan? delay = null)
    {
        if (Name == "igdb")
        {
            host.StubIgdbToken();
        }

        host.Server.Given(Request()).RespondWith(Build(status, fixture, delay));
    }

    /// <summary>Answers the first call with <paramref name="firstStatus"/> and every later call with the found fixture.</summary>
    public void FailOnceThenSucceed(ProviderHost host, int firstStatus, string? retryAfterSeconds = null)
    {
        if (Name == "igdb")
        {
            host.StubIgdbToken();
        }

        var calls = 0;
        var found = ProviderHost.Fixture(FoundFixture);
        host.Server.Given(Request()).RespondWith(Response.Create().WithCallback(_ =>
        {
            var failing = Interlocked.Increment(ref calls) == 1;
            var message = new ResponseMessage
            {
                StatusCode = failing ? firstStatus : 200,
                BodyData = new BodyData { BodyAsString = failing ? "{}" : found, DetectedBodyType = BodyType.String },
            };
            message.AddHeader("Content-Type", "application/json");
            if (failing && retryAfterSeconds is not null)
            {
                message.AddHeader("Retry-After", retryAfterSeconds);
            }

            return message;
        }));
    }

    private static IResponseBuilder Build(int status, string? fixture, TimeSpan? delay)
    {
        var response = Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json");
        if (fixture is not null)
        {
            response = response.WithBody(ProviderHost.Fixture(fixture));
        }

        return delay is { } wait ? response.WithDelay(wait) : response;
    }
}

internal static class ProviderHostStubs
{
    /// <summary>The Twitch token endpoint, answering with a fixed token.</summary>
    public static void StubIgdbToken(this ProviderHost host)
    {
        if (host.Server.Mappings.Any(m => m.Title == "igdb-token"))
        {
            return;
        }

        host.Server.Given(Request_.Create().WithPath("/oauth2/token").UsingPost())
            .WithTitle("igdb-token")
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody(ProviderHost.Fixture("igdb-token.json")));
    }
}

/// <summary>WireMock's <c>Request</c> clashes with nothing here, but the short alias keeps the call sites readable.</summary>
internal static class Request_
{
    public static IRequestBuilder Create() => Request.Create();
}
