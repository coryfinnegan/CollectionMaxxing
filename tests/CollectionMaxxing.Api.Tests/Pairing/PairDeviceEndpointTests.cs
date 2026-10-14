using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Pairing;

public class PairDeviceEndpointTests(ApiFixture api) : PairingTest(api)
{
    [Fact]
    public async Task A_pairing_has_a_token_a_six_digit_code_and_two_minutes_to_live()
    {
        var (browser, _) = await SetUpOwnerAsync();

        var offer = await OfferAsync(browser);

        offer.Token.Length.ShouldBeGreaterThanOrEqualTo(32);
        offer.Code.Length.ShouldBe(6);
        offer.Code.ShouldAllBe(c => char.IsAsciiDigit(c));
        offer.ExpiresAt.ShouldBe(App.Time.GetUtcNow() + TimeSpan.FromMinutes(2), TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task The_server_url_comes_from_configuration_and_never_from_the_host_header()
    {
        var (browser, _) = await SetUpOwnerAsync();

        var response = await browser.SendAsync(HttpMethod.Post, "/api/v1/me/devices/pair", null, tweak: request =>
        {
            request.Headers.Host = "evil.example";
            request.Headers.Add("X-Forwarded-Host", "evil.example");
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var offer = (await ReadAsync(response)).GetProperty("serverUrl").GetString();
        offer.ShouldBe(PublicUrl);
    }

    [Fact]
    public async Task Every_pairing_has_its_own_token_and_code()
    {
        var (browser, _) = await SetUpOwnerAsync();

        var first = await OfferAsync(browser);
        var second = await OfferAsync(browser);

        second.Token.ShouldNotBe(first.Token);
        await using var context = Db.CreateContext();
        (await context.DevicePairings.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task Without_a_recent_passkey_it_is_401_reauth_required_and_nothing_is_made()
    {
        var (browser, _) = await SetUpOwnerAsync();
        App.Time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        var response = await browser.PostAsync("/api/v1/me/devices/pair");

        await ShouldBeProblemAsync(response, HttpStatusCode.Unauthorized);
        (await ReadAsync(response)).GetProperty("type").GetString().ShouldBe("https://collectionmaxxing.app/problems/reauth_required");
        await using var context = Db.CreateContext();
        (await context.DevicePairings.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task A_paired_phone_cannot_make_pairings_for_more_phones()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var device = await PairAsync(browser);

        var response = await AsDeviceAsync(HttpMethod.Post, "/api/v1/me/devices/pair", device.AccessToken);

        await ShouldBeProblemAsync(response, HttpStatusCode.Unauthorized);
        (await ReadAsync(response)).GetProperty("reauth").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Anonymous_callers_cannot_make_pairings()
    {
        await SetUpOwnerAsync();
        using var stranger = App.NewBrowser();

        (await stranger.PostAsync("/api/v1/me/devices/pair")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_web_app_must_send_the_csrf_header_to_make_a_pairing()
    {
        var (browser, _) = await SetUpOwnerAsync();

        var response = await browser.PostAsync("/api/v1/me/devices/pair", csrf: false);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Old_pairings_of_the_same_user_are_tidied_away_when_a_new_one_is_made()
    {
        var (browser, _) = await SetUpOwnerAsync();
        await OfferAsync(browser);
        App.Time.Advance(TimeSpan.FromMinutes(10));
        await browser.LoginAsync();

        await OfferAsync(browser);

        await using var context = Db.CreateContext();
        (await context.DevicePairings.CountAsync()).ShouldBe(1);
    }
}

public class PairDeviceWithoutPublicUrlTests(ApiFixture api) : PairingTest(api)
{
    protected override Dictionary<string, string?>? Settings => new() { ["Auth:RelyingPartyId"] = "localhost" };

    [Fact]
    public async Task With_no_public_url_configured_the_server_url_is_https_on_the_relying_party_host()
    {
        var (browser, _) = await SetUpOwnerAsync();

        var offer = await OfferAsync(browser);

        offer.ServerUrl.ShouldBe("https://localhost");
    }
}
