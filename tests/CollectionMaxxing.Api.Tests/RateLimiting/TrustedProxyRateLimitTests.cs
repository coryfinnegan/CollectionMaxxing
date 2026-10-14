using System.Net;
using System.Net.Http.Json;
using CollectionMaxxing.Api.Tests.Pairing;
using CollectionMaxxing.Api.Tests.Support;

namespace CollectionMaxxing.Api.Tests.RateLimiting;

// LOCKED acceptance tests of ID-2b (issue #63): the limiter keys on the forwarded client address only when the connection comes from
// a configured trusted proxy, never on a header an arbitrary caller sends. Do not edit, weaken, skip or delete: add new tests instead.
public abstract class RateLimitTestBase(ApiFixture api) : HardeningTest(api)
{
    protected override Dictionary<string, string?> MoreSettings => new()
    {
        ["Auth:RateLimit:PermitLimit"] = "3",
        ["Auth:RateLimit:WindowSeconds"] = "60",
        ["Auth:RateLimit:TrustedProxies:0"] = "10.0.0.1",
        ["Auth:RateLimit:TrustedProxies:1"] = "192.168.0.0/24",
    };

    protected static Task<HttpResponseMessage> PairingAttemptAsync(HttpClient phone, int i) => RedeemCodeAsync(phone, "00000" + (i % 10));

    protected static Task<HttpResponseMessage> SetupAttemptAsync(HttpClient phone, int i) =>
        phone.PostAsJsonAsync("/setup", new { code = "WRONG-" + i, displayName = "x" });
}

public class UntrustedPeerRateLimitTests(ApiFixture api) : RateLimitTestBase(api)
{
    [Fact]
    public async Task A_forged_forwarded_for_from_an_untrusted_peer_does_not_dodge_the_pairing_limit()
    {
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            statuses.Add((await PairingAttemptAsync(From("10.0.0.2", $"203.0.113.{i + 10}"), i)).StatusCode);
        }

        statuses.Take(3).ShouldAllBe(s => s == HttpStatusCode.BadRequest);
        statuses.Skip(3).ShouldAllBe(s => s == HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task A_forged_forwarded_for_does_not_dodge_the_sign_in_limit_either()
    {
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            statuses.Add((await SetupAttemptAsync(From("10.0.0.2", $"203.0.113.{i + 10}"), i)).StatusCode);
        }

        statuses.Take(3).ShouldAllBe(s => s == HttpStatusCode.BadRequest);
        statuses.Skip(3).ShouldAllBe(s => s == HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task A_forged_forwarded_for_cannot_reset_a_limit_already_used_up()
    {
        var plain = From("10.0.0.2");
        for (var i = 0; i < 3; i++)
        {
            await PairingAttemptAsync(plain, i);
        }

        (await PairingAttemptAsync(From("10.0.0.2", "198.51.100.77"), 7)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task A_forged_forwarded_for_cannot_blame_another_address()
    {
        // 203.0.113.50 is somebody else. The forger spends only its own allowance, not theirs.
        for (var i = 0; i < 5; i++)
        {
            await PairingAttemptAsync(From("10.0.0.2", "203.0.113.50"), i);
        }

        (await PairingAttemptAsync(From("10.0.0.3", "203.0.113.50"), 1)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}

public class TrustedProxyRateLimitTests(ApiFixture api) : RateLimitTestBase(api)
{
    [Fact]
    public async Task Behind_the_trusted_proxy_each_forwarded_client_has_its_own_pairing_allowance()
    {
        for (var i = 0; i < 3; i++)
        {
            (await PairingAttemptAsync(From("10.0.0.1", "203.0.113.5"), i)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        var limited = await PairingAttemptAsync(From("10.0.0.1", "203.0.113.5"), 4);
        var someoneElse = await PairingAttemptAsync(From("10.0.0.1", "203.0.113.6"), 5);

        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.ShouldNotBeNull();
        someoneElse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Behind_the_trusted_proxy_each_forwarded_client_has_its_own_sign_in_allowance()
    {
        for (var i = 0; i < 3; i++)
        {
            (await SetupAttemptAsync(From("10.0.0.1", "203.0.113.5"), i)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        var limited = await SetupAttemptAsync(From("10.0.0.1", "203.0.113.5"), 4);
        var someoneElse = await SetupAttemptAsync(From("10.0.0.1", "203.0.113.6"), 5);

        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.ShouldNotBeNull();
        someoneElse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Entries_a_client_prepends_to_the_forwarded_for_chain_do_not_dodge_the_limit()
    {
        // The proxy appends the real client to whatever the client sent, so the real one is the right-most untrusted entry.
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            statuses.Add((await PairingAttemptAsync(From("10.0.0.1", $"198.51.100.{i}, 203.0.113.5"), i)).StatusCode);
        }

        statuses.Skip(3).ShouldAllBe(s => s == HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task A_proxy_in_a_trusted_range_is_trusted_too()
    {
        for (var i = 0; i < 3; i++)
        {
            await PairingAttemptAsync(From("192.168.0.77", "203.0.113.5"), i);
        }

        (await PairingAttemptAsync(From("192.168.0.77", "203.0.113.5"), 4)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await PairingAttemptAsync(From("192.168.0.77", "203.0.113.6"), 5)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_garbled_forwarded_for_from_the_trusted_proxy_falls_back_to_the_proxy_itself()
    {
        for (var i = 0; i < 3; i++)
        {
            await PairingAttemptAsync(From("10.0.0.1", "not-an-address"), i);
        }

        (await PairingAttemptAsync(From("10.0.0.1", "also-garbage"), 4)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task A_wrong_code_guesser_behind_the_proxy_does_not_lock_out_the_owner_behind_the_same_proxy()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var offer = await OfferAsync(browser);
        var attacker = From("10.0.0.1", "203.0.113.5");
        foreach (var guess in WrongCodes(offer.Code, 3))
        {
            await RedeemCodeAsync(attacker, guess);
        }

        (await RedeemCodeAsync(From("10.0.0.1", "203.0.113.6"), offer.Code)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
