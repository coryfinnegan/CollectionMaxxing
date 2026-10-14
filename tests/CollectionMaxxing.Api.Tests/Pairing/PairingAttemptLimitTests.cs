using System.Net;
using CollectionMaxxing.Api.Tests.Support;

namespace CollectionMaxxing.Api.Tests.Pairing;

// LOCKED acceptance test of ID-2 (issue #21): pairing attempts are rate limited. Do not edit, weaken, skip or delete: add new tests instead.
public class PairingAttemptLimitTests(ApiFixture api) : PairingTest(api)
{
    protected override Dictionary<string, string?>? MoreSettings => new() { ["Auth:RateLimit:PermitLimit"] = "3", ["Auth:RateLimit:WindowSeconds"] = "60" };

    [Fact]
    public async Task Guessing_pairing_codes_is_rate_limited_with_429_and_retry_after()
    {
        await SetUpOwnerAsync();

        for (var i = 0; i < 3; i++)
        {
            (await RedeemCodeAsync("00000" + i)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        var limited = await RedeemCodeAsync("000009");

        await ShouldBeProblemAsync(limited, HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.ShouldNotBeNull();
        limited.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ShouldBeInRange(1, 60);
    }

    [Fact]
    public async Task A_limited_caller_cannot_pair_even_with_the_right_token()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var offer = await OfferAsync(browser);
        for (var i = 0; i < 3; i++)
        {
            await RedeemCodeAsync("00000" + i);
        }

        var response = await RedeemTokenAsync(offer.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task The_limit_opens_again_after_the_window()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var offer = await OfferAsync(browser);
        for (var i = 0; i < 4; i++)
        {
            await RedeemCodeAsync("00000" + i);
        }

        App.Time.Advance(TimeSpan.FromSeconds(61));
        var response = await RedeemTokenAsync(offer.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Refreshing_tokens_is_not_counted_as_a_pairing_attempt()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var device = await PairAsync(browser);

        for (var i = 0; i < 8; i++)
        {
            device = await ReadDeviceAsync(await RefreshAsync(device.RefreshToken));
        }

        (await AsDeviceAsync(HttpMethod.Get, "/api/v1/me", device.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
