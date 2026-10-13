using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Accounts;

namespace CollectionMaxxing.Api.Tests.Auth;

public class RateLimitTests(ApiFixture api) : AuthTest(api)
{
    protected override Dictionary<string, string?>? Settings => new() { ["Auth:RateLimit:PermitLimit"] = "3", ["Auth:RateLimit:WindowSeconds"] = "60" };

    [Fact]
    public async Task Guessing_setup_codes_is_rate_limited()
    {
        await App.IssueCodeAsync(SetupCodePurpose.Setup);
        var browser = App.NewBrowser();

        for (var i = 0; i < 3; i++)
        {
            (await browser.StartSetupAsync($"WRONG-{i}")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        var limited = await browser.StartSetupAsync("WRONG-4");

        await ShouldBeProblemAsync(limited, HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.ShouldNotBeNull();
    }
}
