using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Infrastructure.Providers.UpcItemDb;
using CollectionMaxxing.Infrastructure.Scanning;
using CollectionMaxxing.Infrastructure.Tests.Providers;
using CollectionMaxxing.TestSupport;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace CollectionMaxxing.Infrastructure.Tests.Scanning;

public class ScanningStorageTests(PostgresFixture db) : DatabaseTest(db)
{
    private const string Path = "/prod/trial/lookup";

    private static Identifier Upc()
    {
        Identifier.TryParse("045496830434", out var id).ShouldBeTrue();
        return id!;
    }

    private static UpcProductLookup LookupAgainst(ProviderHost host) => new(host.Get<UpcItemDbProvider>());

    [Fact]
    public async Task A_known_product_is_found()
    {
        using var host = ProviderHost.Create(Db);
        host.Server.Given(Request.Create().WithPath(Path).UsingGet())
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody(ProviderHost.Fixture("upcitemdb-game.json")));

        var result = await LookupAgainst(host).LookupAsync(Upc(), default);

        result.Status.ShouldBe(ProductLookupStatus.Found);
        result.Release!.Title.ShouldBe("Super Mario 64");
    }

    [Fact]
    public async Task An_unknown_product_is_not_found()
    {
        using var host = ProviderHost.Create(Db);
        host.Server.Given(Request.Create().WithPath(Path).UsingGet())
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody(ProviderHost.Fixture("upcitemdb-empty.json")));

        (await LookupAgainst(host).LookupAsync(Upc(), default)).Status.ShouldBe(ProductLookupStatus.NotFound);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    public async Task A_provider_error_is_unavailable_not_not_found(int status)
    {
        using var host = ProviderHost.Create(Db);
        host.Server.Given(Request.Create().WithPath(Path).UsingGet()).RespondWith(Response.Create().WithStatusCode(status));

        (await LookupAgainst(host).LookupAsync(Upc(), default)).Status.ShouldBe(ProductLookupStatus.Unavailable);
    }

    [Fact]
    public async Task A_provider_that_times_out_is_unavailable()
    {
        using var host = ProviderHost.Create(Db, settings: new()
        {
            ["Providers:Resilience:AttemptTimeout"] = "00:00:00.300",
            ["Providers:Resilience:MaxRetryAttempts"] = "1",
        });
        host.Server.Given(Request.Create().WithPath(Path).UsingGet()).RespondWith(Response.Create().WithDelay(TimeSpan.FromSeconds(3)).WithStatusCode(200));

        (await LookupAgainst(host).LookupAsync(Upc(), default)).Status.ShouldBe(ProductLookupStatus.Unavailable);
    }

    [Fact]
    public async Task A_spent_quota_is_reported_as_such()
    {
        using var host = ProviderHost.Create(Db, settings: new() { ["Providers:UpcItemDb:DailyLimit"] = "0" });

        (await LookupAgainst(host).LookupAsync(Upc(), default)).Status.ShouldBe(ProductLookupStatus.QuotaExhausted);
    }

    [Fact]
    public async Task The_session_lock_lets_one_holder_in_at_a_time_and_lets_the_next_in_when_released()
    {
        var sessionId = Guid.CreateVersion7();
        await using var first = Db.CreateContext();
        await using var second = Db.CreateContext();

        var held = await new PostgresSessionLock(first).AcquireAsync(sessionId, default);
        var waiting = new PostgresSessionLock(second).AcquireAsync(sessionId, default);

        await Task.Delay(300);
        waiting.IsCompleted.ShouldBeFalse("the second holder must wait for the first");

        await held.DisposeAsync();
        await using var next = await waiting.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Locks_on_different_sessions_do_not_wait_for_each_other()
    {
        await using var first = Db.CreateContext();
        await using var second = Db.CreateContext();

        await using var one = await new PostgresSessionLock(first).AcquireAsync(Guid.CreateVersion7(), default);
        await using var two = await new PostgresSessionLock(second).AcquireAsync(Guid.CreateVersion7(), default).WaitAsync(TimeSpan.FromSeconds(5));
    }
}
