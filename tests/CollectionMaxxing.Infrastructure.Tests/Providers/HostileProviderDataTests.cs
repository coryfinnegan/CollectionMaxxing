using CollectionMaxxing.TestSupport;
using Shouldly;
using WireMock.ResponseBuilders;
using Xunit;

namespace CollectionMaxxing.Infrastructure.Tests.Providers;

/// <summary>API10 (unsafe consumption of APIs): what a provider sends is untrusted. A body that is too big, or the wrong shape, is dropped, never cached and never an exception.</summary>
public class HostileProviderDataTests(PostgresFixture db) : DatabaseTest(db)
{
    [Theory]
    [MemberData(nameof(ProviderCase.Names), MemberType = typeof(ProviderCase))]
    public async Task A_ten_megabyte_answer_is_refused_and_not_kept(string name)
    {
        using var host = ProviderHost.Create(Db);
        var provider = ProviderCase.Named(name);
        if (name == "igdb")
        {
            host.StubIgdbToken();
        }

        host.Server.Given(provider.Request()).RespondWith(
            Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("{\"title\":\"" + new string('A', 10 * 1024 * 1024) + "\"}"));

        (await provider.Ask(host)).ShouldBe(0);
        (await provider.Ask(host)).ShouldBe(0);

        host.Requests(provider.Path).ShouldBe(2); // the oversized body was not cached: the second ask went out again
    }

    [Theory]
    [MemberData(nameof(ProviderCase.Names), MemberType = typeof(ProviderCase))]
    public async Task Fields_of_the_wrong_type_are_ignored_not_thrown_on(string name)
    {
        using var host = ProviderHost.Create(Db);
        var provider = ProviderCase.Named(name);
        if (name == "igdb")
        {
            host.StubIgdbToken();
        }

        var hostile = "{\"title\":123,\"name\":[1,2],\"items\":{\"x\":1},\"docs\":\"nope\",\"results\":true,\"data\":null,\"volumeInfo\":7,"
            + "\"ISBN:9780140328721\":\"<script>alert(1)</script>\",\"images\":\"x\",\"cover\":5}";
        host.Server.Given(provider.Request()).RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(hostile));

        Should.NotThrow(async () => await provider.Ask(host));
    }
}
