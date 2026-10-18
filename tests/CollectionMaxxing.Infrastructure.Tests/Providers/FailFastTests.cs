using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Shouldly;
using Xunit;

namespace CollectionMaxxing.Infrastructure.Tests.Providers;

/// <summary>
/// A person waiting on a scan or a search is not made to sit through retry back-off: with the production retry settings, a
/// provider that fails at once is given up on at once, once. Background jobs (no scope) keep their retries.
/// </summary>
public class FailFastTests(PostgresFixture db) : DatabaseTest(db)
{
    private static Dictionary<string, string?> ProductionRetries() => new()
    {
        ["Providers:Resilience:MaxRetryAttempts"] = "3",
        ["Providers:Resilience:RetryDelay"] = "00:00:01",
    };

    [Theory]
    [MemberData(nameof(ProviderCase.Names), MemberType = typeof(ProviderCase))]
    public async Task An_interactive_ask_gives_up_at_once_on_a_provider_that_answers_500(string name)
    {
        using var host = ProviderHost.Create(Db, settings: ProductionRetries());
        var provider = ProviderCase.Named(name);
        provider.RespondWith(host, 500, null);

        var started = DateTimeOffset.UtcNow;
        using (ProviderCalls.FailFast())
        {
            (await provider.Ask(host)).ShouldBe(0);
        }

        (DateTimeOffset.UtcNow - started).ShouldBeLessThan(TimeSpan.FromMilliseconds(900));
        host.Requests(provider.Path).ShouldBe(1);
    }

    [Theory]
    [MemberData(nameof(ProviderCase.Names), MemberType = typeof(ProviderCase))]
    public async Task An_interactive_ask_gives_up_at_once_on_a_provider_that_refuses_the_connection(string name)
    {
        using var host = ProviderHost.Create(Db, settings: ProductionRetries());
        var provider = ProviderCase.Named(name);
        host.Server.Stop(); // nothing listens on the port any more: connection refused

        var started = DateTimeOffset.UtcNow;
        using (ProviderCalls.FailFast())
        {
            (await provider.Ask(host)).ShouldBe(0);
        }

        // Windows alone takes about 2 s per address to learn a port is closed; four tries and their back-off would be over 20 s (IGDB asks twice: a token, then the search).
        (DateTimeOffset.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(12));
    }

    [Theory]
    [MemberData(nameof(ProviderCase.Names), MemberType = typeof(ProviderCase))]
    public async Task The_app_wide_http_defaults_do_not_add_a_second_retry_pipeline_around_the_provider_one(string name)
    {
        // The app (ServiceDefaults) gives every HttpClient the standard resilience handler. A provider client must keep only its own,
        // or an unconfigured outer one retries (3 times, 2 s apart, doubling) past the caller's time budget.
        var settings = new Dictionary<string, string?>
        {
            ["Providers:Resilience:MaxRetryAttempts"] = "1",
            ["Providers:Resilience:RetryDelay"] = "00:00:00.050",
        };
        using var host = ProviderHost.Create(Db, settings: settings, configure: s => s.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler()));
        var provider = ProviderCase.Named(name);
        provider.RespondWith(host, 500, null);

        (await provider.Ask(host)).ShouldBe(0);

        host.Requests(provider.Path).ShouldBe(2); // the first try and the one retry, no more
    }

    [Fact]
    public async Task A_background_ask_still_retries_a_500()
    {
        using var host = ProviderHost.Create(Db, settings: ProductionRetries());
        var provider = ProviderCase.Named("openlibrary");
        provider.FailOnceThenSucceed(host, 500);

        (await provider.Ask(host)).ShouldBeGreaterThan(0);

        host.Requests(provider.Path).ShouldBe(2);
    }
}
