using CollectionMaxxing.TestSupport;
using Shouldly;
using Xunit;

namespace CollectionMaxxing.Infrastructure.Tests.Providers;

/// <summary>What every provider adapter does when the provider answers well, badly or not at all (WireMock, no live calls).</summary>
public class ProviderBehaviourTests(PostgresFixture db) : DatabaseTest(db)
{
    [Theory]
    [MemberData(nameof(ProviderCase.Names), MemberType = typeof(ProviderCase))]
    public async Task Finds_what_the_provider_returns(string name)
    {
        using var host = ProviderHost.Create(Db);
        var provider = ProviderCase.Named(name);
        provider.RespondWith(host, 200, provider.FoundFixture);

        (await provider.Ask(host)).ShouldBeGreaterThan(0);
    }

    [Theory]
    [MemberData(nameof(ProviderCase.Names), MemberType = typeof(ProviderCase))]
    public async Task Reports_nothing_when_the_provider_has_an_empty_answer(string name)
    {
        using var host = ProviderHost.Create(Db);
        var provider = ProviderCase.Named(name);
        provider.RespondWith(host, 200, provider.EmptyFixture);

        (await provider.Ask(host)).ShouldBe(0);
    }

    [Theory]
    [MemberData(nameof(ProviderCase.Names), MemberType = typeof(ProviderCase))]
    public async Task Reports_nothing_when_the_provider_says_404(string name)
    {
        using var host = ProviderHost.Create(Db);
        var provider = ProviderCase.Named(name);
        provider.RespondWith(host, 404, null);

        (await provider.Ask(host)).ShouldBe(0);
    }

    [Theory]
    [MemberData(nameof(ProviderCase.Names), MemberType = typeof(ProviderCase))]
    public async Task Survives_malformed_json_without_throwing_or_caching_it(string name)
    {
        using var host = ProviderHost.Create(Db);
        var provider = ProviderCase.Named(name);
        provider.RespondWith(host, 200, "malformed.json");

        (await provider.Ask(host)).ShouldBe(0);
        (await provider.Ask(host)).ShouldBe(0);

        host.Requests(provider.Path).ShouldBe(2); // a bad body is never cached: the second ask went out again
    }

    [Theory]
    [MemberData(nameof(ProviderCase.Names), MemberType = typeof(ProviderCase))]
    public async Task Retries_after_a_429_and_waits_for_Retry_After(string name)
    {
        using var host = ProviderHost.Create(Db);
        var provider = ProviderCase.Named(name);
        provider.FailOnceThenSucceed(host, 429, retryAfterSeconds: "1");

        var started = DateTimeOffset.UtcNow;
        (await provider.Ask(host)).ShouldBeGreaterThan(0);

        (DateTimeOffset.UtcNow - started).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(900));
        host.Requests(provider.Path).ShouldBe(2);
    }

    [Theory]
    [MemberData(nameof(ProviderCase.Names), MemberType = typeof(ProviderCase))]
    public async Task Retries_a_500_and_then_succeeds(string name)
    {
        using var host = ProviderHost.Create(Db);
        var provider = ProviderCase.Named(name);
        provider.FailOnceThenSucceed(host, 500);

        (await provider.Ask(host)).ShouldBeGreaterThan(0);

        host.Requests(provider.Path).ShouldBe(2);
    }

    [Theory]
    [MemberData(nameof(ProviderCase.Names), MemberType = typeof(ProviderCase))]
    public async Task Gives_up_quietly_when_the_provider_never_answers_in_time(string name)
    {
        using var host = ProviderHost.Create(Db, settings: new() { ["Providers:Resilience:MaxRetryAttempts"] = "1", ["Providers:Resilience:AttemptTimeout"] = "00:00:00.300" });
        var provider = ProviderCase.Named(name);
        provider.RespondWith(host, 200, provider.FoundFixture, delay: TimeSpan.FromSeconds(3));

        (await provider.Ask(host)).ShouldBe(0); // the 300 ms attempt timeout cut each try short; nothing thrown

        (await host.WaitForRequestsAsync(provider.Path, 2)).ShouldBe(2); // the first try and one retry
    }

    [Theory]
    [MemberData(nameof(ProviderCase.Names), MemberType = typeof(ProviderCase))]
    public async Task Gives_up_quietly_when_the_provider_keeps_failing(string name)
    {
        using var host = ProviderHost.Create(Db);
        var provider = ProviderCase.Named(name);
        provider.RespondWith(host, 503, null);

        (await provider.Ask(host)).ShouldBe(0);
    }

    [Theory]
    [MemberData(nameof(ProviderCase.Names), MemberType = typeof(ProviderCase))]
    public async Task Answers_a_repeat_from_the_30_day_cache_until_it_expires(string name)
    {
        using var host = ProviderHost.Create(Db, fakeTime: true);
        var provider = ProviderCase.Named(name);
        provider.RespondWith(host, 200, provider.FoundFixture);

        (await provider.Ask(host)).ShouldBeGreaterThan(0);
        (await provider.Ask(host)).ShouldBeGreaterThan(0);
        host.Requests(provider.Path).ShouldBe(1); // the second lookup made no HTTP call

        host.Fake.Advance(TimeSpan.FromDays(30));
        (await provider.Ask(host)).ShouldBeGreaterThan(0);
        host.Requests(provider.Path).ShouldBe(2); // exactly 30 days is already expired...

        host.Fake.Advance(TimeSpan.FromDays(29));
        (await provider.Ask(host)).ShouldBeGreaterThan(0);
        host.Requests(provider.Path).ShouldBe(2); // ...but the fresh copy lasts another 30 days

        host.Fake.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));
        (await provider.Ask(host)).ShouldBeGreaterThan(0);
        host.Requests(provider.Path).ShouldBe(3); // 30 days and a second later: fetched again
    }

    [Theory]
    [MemberData(nameof(ProviderCase.Names), MemberType = typeof(ProviderCase))]
    public async Task Remembers_that_a_code_is_unknown_so_it_is_not_asked_again(string name)
    {
        using var host = ProviderHost.Create(Db, fakeTime: true);
        var provider = ProviderCase.Named(name);
        provider.RespondWith(host, 404, null);

        (await provider.Ask(host)).ShouldBe(0);
        (await provider.Ask(host)).ShouldBe(0);

        host.Requests(provider.Path).ShouldBe(1);
    }
}
