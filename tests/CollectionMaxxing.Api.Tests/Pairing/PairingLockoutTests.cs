using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Pairing;

// LOCKED acceptance tests of ID-2b (issue #63), the pairing lockout rule. Do not edit, weaken, skip or delete: add new tests instead.
//
// The rule: wrong codes are counted against the CALLER (its address), never against the pairing. A caller with five wrong guesses
// in the window is refused outright (token or code, 400 invalid_grant) until the window passes. The pairing itself stays redeemable
// by everyone else, so a stranger cannot burn the owner's open pairing by guessing.
public class PairingLockoutTests(ApiFixture api) : HardeningTest(api)
{
    private const string Attacker = "10.9.9.9";
    private const string Owner = "10.1.1.1";

    [Fact]
    public async Task An_attacker_who_sends_five_wrong_codes_cannot_stop_the_owner_pairing_with_the_real_token()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var offer = await OfferAsync(browser);
        await GuessFiveTimesAsync(offer.Code);

        var response = await RedeemTokenAsync(From(Owner), offer.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_attacker_who_sends_five_wrong_codes_cannot_stop_the_owner_pairing_with_the_real_code()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var offer = await OfferAsync(browser);
        await GuessFiveTimesAsync(offer.Code);

        var response = await RedeemCodeAsync(From(Owner), offer.Code);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Many_wrong_guesses_from_many_strangers_still_leave_the_pairing_redeemable_by_its_token()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var offer = await OfferAsync(browser);
        for (var stranger = 0; stranger < 4; stranger++)
        {
            var phone = From($"10.9.8.{stranger}");
            foreach (var guess in WrongCodes(offer.Code, 5))
            {
                await RedeemCodeAsync(phone, guess);
            }
        }

        (await RedeemTokenAsync(From(Owner), offer.Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_guesser_itself_is_refused_after_five_wrong_codes_even_with_the_right_code_or_token()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var offer = await OfferAsync(browser);
        var attacker = From(Attacker);
        foreach (var guess in WrongCodes(offer.Code, 5))
        {
            (await RedeemCodeAsync(attacker, guess)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        var byCode = await RedeemCodeAsync(attacker, offer.Code);
        var byToken = await RedeemTokenAsync(attacker, offer.Token);

        byCode.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        byToken.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorAsync(byCode)).ShouldBe("invalid_grant");
    }

    [Fact]
    public async Task A_guesser_who_has_used_up_its_guesses_may_try_again_after_the_window()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var offer = await OfferAsync(browser);
        var attacker = From(Attacker);
        foreach (var guess in WrongCodes(offer.Code, 5))
        {
            await RedeemCodeAsync(attacker, guess);
        }

        (await RedeemCodeAsync(attacker, offer.Code)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        App.Time.Advance(TimeSpan.FromSeconds(61));

        (await RedeemCodeAsync(attacker, offer.Code)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Four_wrong_guesses_do_not_lock_the_guesser_out()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var offer = await OfferAsync(browser);
        var phone = From(Owner);
        foreach (var guess in WrongCodes(offer.Code, 4))
        {
            await RedeemCodeAsync(phone, guess);
        }

        (await RedeemCodeAsync(phone, offer.Code)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Wrong_guesses_of_one_stranger_are_still_tallied_on_the_pairing_row_but_do_not_gate_it()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var offer = await OfferAsync(browser);
        await GuessFiveTimesAsync(offer.Code);
        await RedeemTokenAsync(From(Owner), offer.Token);

        await using var context = Db.CreateContext();
        var row = await context.DevicePairings.SingleAsync();
        row.Attempts.ShouldBe(5);
        row.UsedAt.ShouldNotBeNull();
    }

    private async Task GuessFiveTimesAsync(string rightCode)
    {
        var attacker = From(Attacker);
        foreach (var guess in WrongCodes(rightCode, 5))
        {
            (await RedeemCodeAsync(attacker, guess)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
    }

    private static async Task<string?> ErrorAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
    }
}
