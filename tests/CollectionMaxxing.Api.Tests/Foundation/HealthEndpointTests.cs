using System.Net;
using CollectionMaxxing.Api.Tests.Support;

namespace CollectionMaxxing.Api.Tests.Foundation;

public class HealthEndpointTests(ApiFixture api) : ApiTest(api)
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_checks_answer_without_signing_in(string path)
    {
        var response = await Api.CreateClient().GetAsync(path);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Readiness_fails_but_liveness_still_passes_when_the_database_is_unreachable()
    {
        await using var factory = new TestApiFactory("Host=127.0.0.1;Port=1;Database=none;Timeout=1;Command Timeout=1");
        using var client = factory.CreateClient();

        (await client.GetAsync("/health/live")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/health/ready")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }
}
