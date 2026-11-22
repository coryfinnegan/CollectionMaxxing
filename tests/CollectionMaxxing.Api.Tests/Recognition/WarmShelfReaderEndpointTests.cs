using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Recognition;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CollectionMaxxing.Api.Tests.Recognition;

/// <summary>
/// <c>POST /api/v1/recognition/shelf-reader:warm</c>: wakes the shelf reader when the scanner opens. Who may call it is a row in the authorization
/// matrix; these tests are about what it answers and what it starts. The reader itself is a fake that counts.
/// </summary>
public sealed class WarmShelfReaderEndpointTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private const string Url = "/api/v1/recognition/shelf-reader:warm";

    private sealed class CountingWarmer : IShelfReaderWarmer
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void RequestWarm() => Interlocked.Increment(ref _calls);
    }

    private HttpClient Client(CountingWarmer warmer, string? readerEndpoint)
    {
        var factory = api.Factory.WithWebHostBuilder(builder =>
        {
            if (readerEndpoint is not null)
            {
                builder.UseSetting("ShelfReader:Endpoint", readerEndpoint);
            }

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IShelfReaderWarmer>();
                services.AddSingleton<IShelfReaderWarmer>(warmer);
            });
        });
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());
        return client;
    }

    [Fact]
    public async Task With_a_reader_set_up_the_answer_is_202_and_one_warm_up_is_asked_for()
    {
        var warmer = new CountingWarmer();
        using var client = Client(warmer, "http://127.0.0.1:1");

        (await client.PostAsync(Url, null)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        warmer.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task With_no_reader_set_up_the_answer_is_204_and_nothing_is_asked_for()
    {
        var warmer = new CountingWarmer();
        using var client = Client(warmer, readerEndpoint: "");

        (await client.PostAsync(Url, null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        warmer.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task An_anonymous_caller_gets_401_and_nothing_is_asked_for()
    {
        var warmer = new CountingWarmer();
        using var client = Client(warmer, "http://127.0.0.1:1");
        client.DefaultRequestHeaders.Remove(TestAuthHandler.UserHeader);

        (await client.PostAsync(Url, null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        warmer.Calls.ShouldBe(0);
    }
}
