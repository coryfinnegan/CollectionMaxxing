using System.Net;
using CollectionMaxxing.Api.Tests.Support;

namespace CollectionMaxxing.Api.Tests.Auth;

public class RateLimitTests(ApiFixture api) : AuthTest(api)
{
    protected override Dictionary<string, string?>? Settings => new() { ["Auth:RateLimit:PermitLimit"] = "3", ["Auth:RateLimit:WindowSeconds"] = "60" };

    [Fact]
    public async Task Guessing_recovery_codes_is_rate_limited()
    {
        var browser = App.NewBrowser();

        for (var i = 0; i < 3; i++)
        {
            (await browser.StartRecoveryAsync($"WRONG-{i}")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        var limited = await browser.StartRecoveryAsync("WRONG-4");

        await ShouldBeProblemAsync(limited, HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.ShouldNotBeNull();
    }
}
