using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Pairing;

// LOCKED acceptance tests of ID-2 (issue #21), redeeming a pairing. Do not edit, weaken, skip or delete: add new tests instead.
public class PairingRedemptionTests(ApiFixture api) : PairingTest(api)
{
    [Fact]
    public async Task A_pairing_token_works_once_and_the_second_redemption_fails()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var offer = await OfferAsync(browser);

        var first = await RedeemTokenAsync(offer.Token);
        var second = await RedeemTokenAsync(offer.Token);

        (await ReadDeviceAsync(first)).AccessToken.ShouldNotBeNullOrEmpty();
        second.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await OAuthErrorAsync(second)).ShouldBe("invalid_grant");
    }

    [Fact]
    public async Task A_pairing_token_redeemed_by_two_phones_at_the_same_moment_works_for_exactly_one()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var offer = await OfferAsync(browser);
        var phones = Enumerable.Range(0, 8).Select(_ => App.NewBrowser().Http).ToList();

        var responses = await Task.WhenAll(phones.Select(phone => RedeemTokenAsync(offer.Token, "Racer", phone)));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.BadRequest).ShouldBe(7);
        (await SessionsAsync(browser)).Length.ShouldBe(1);
    }

    [Fact]
    public async Task A_six_digit_code_redeemed_by_two_phones_at_the_same_moment_works_for_exactly_one()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var offer = await OfferAsync(browser);
        var phones = Enumerable.Range(0, 8).Select(_ => App.NewBrowser().Http).ToList();

        var responses = await Task.WhenAll(phones.Select(phone => RedeemCodeAsync(offer.Code, "Racer", phone)));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        (await SessionsAsync(browser)).Length.ShouldBe(1);
    }

    [Fact]
    public async Task A_pairing_cannot_be_redeemed_by_its_token_and_then_again_by_its_code()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var offer = await OfferAsync(browser);

        (await RedeemTokenAsync(offer.Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var again = await RedeemCodeAsync(offer.Code);

        again.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_six_digit_code_pairs_a_phone_just_like_the_token_does()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var offer = await OfferAsync(browser);

        var device = await ReadDeviceAsync(await RedeemCodeAsync(offer.Code, "Fallback phone"));

        (await AsDeviceAsync(HttpMethod.Get, "/api/v1/me", device.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SessionsAsync(browser)).Single().GetProperty("deviceName").GetString().ShouldBe("Fallback phone");
    }

    [Fact]
    public async Task A_pairing_still_works_1_minute_59_seconds_after_it_was_made()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var offer = await OfferAsync(browser);
        App.Time.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(59));

        (await RedeemTokenAsync(offer.Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_pairing_2_minutes_and_1_second_old_fails_by_token_and_by_code()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var offer = await OfferAsync(browser);
        App.Time.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));

        var byToken = await RedeemTokenAsync(offer.Token);
        var byCode = await RedeemCodeAsync(offer.Code);

        byToken.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await OAuthErrorAsync(byToken)).ShouldBe("invalid_grant");
        byCode.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SessionsAsync(await ReloginAsync(browser))).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_token_cannot_be_redeemed_with_a_different_pairings_code()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var first = await OfferAsync(browser);
        var second = await OfferAsync(browser);

        var mixed = await FormAsync(
            Phone,
            ("grant_type", "urn:cm:device_pairing"),
            ("pairing_token", first.Token),
            ("pairing_code", second.Code),
            ("device_name", "Mixed up"));

        mixed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SessionsAsync(browser)).ShouldBeEmpty();

        // Neither pairing was burned by the mix-up.
        (await RedeemCodeAsync(second.Code)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RedeemTokenAsync(first.Token, phone: App.NewBrowser().Http)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_token_and_its_own_code_together_work()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var offer = await OfferAsync(browser);

        var both = await FormAsync(
            Phone,
            ("grant_type", "urn:cm:device_pairing"),
            ("pairing_token", offer.Token),
            ("pairing_code", offer.Code),
            ("device_name", "Both"));

        (await ReadDeviceAsync(both)).RefreshToken.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task The_fifth_wrong_guess_of_the_code_locks_the_pairing_and_the_right_code_then_fails_too()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var offer = await OfferAsync(browser);
        var wrong = WrongCodes(offer.Code, 5);

        foreach (var guess in wrong)
        {
            (await RedeemCodeAsync(guess)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        (await RedeemCodeAsync(offer.Code)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await RedeemTokenAsync(offer.Token)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SessionsAsync(browser)).ShouldBeEmpty();
        await using var context = Db.CreateContext();
        (await context.DevicePairings.SingleAsync()).Attempts.ShouldBe(5);
    }

    [Fact]
    public async Task Four_wrong_guesses_do_not_lock_the_pairing()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var offer = await OfferAsync(browser);

        foreach (var guess in WrongCodes(offer.Code, 4))
        {
            (await RedeemCodeAsync(guess)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        (await RedeemCodeAsync(offer.Code)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_request_with_no_token_and_no_code_is_refused()
    {
        var (_, _) = await SetUpOwnerAsync();

        var response = await FormAsync(Phone, ("grant_type", "urn:cm:device_pairing"), ("device_name", "Nothing"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Redeeming_makes_a_device_named_by_the_phone()
    {
        var (browser, _) = await SetUpOwnerAsync();

        await PairAsync(browser, "Cory's iPhone 17");

        var device = (await SessionsAsync(browser)).Single();
        device.GetProperty("deviceName").GetString().ShouldBe("Cory's iPhone 17");
        device.GetProperty("kind").GetString().ShouldBe("device");
        device.GetProperty("isCurrent").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task A_phone_that_gives_no_name_still_pairs()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var offer = await OfferAsync(browser);

        var response = await FormAsync(Phone, ("grant_type", "urn:cm:device_pairing"), ("pairing_token", offer.Token));

        (await ReadDeviceAsync(response)).AccessToken.ShouldNotBeNullOrEmpty();
        (await SessionsAsync(browser)).Single().GetProperty("deviceName").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>After the clock has moved on the old cookie may be gone: sign in again with the passkey.</summary>
    private static async Task<Auth.WebClient> ReloginAsync(Auth.WebClient browser)
    {
        (await browser.LoginAsync()).StatusCode.ShouldBe(HttpStatusCode.OK);
        return browser;
    }

    private static string[] WrongCodes(string right, int count) =>
        [.. Enumerable.Range(0, 20).Select(i => (100000 + (i * 7919)).ToString(System.Globalization.CultureInfo.InvariantCulture)).Where(c => c != right).Take(count)];
}
