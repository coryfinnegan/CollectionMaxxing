using System.Text.RegularExpressions;
using CollectionMaxxing.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Pairing;

// LOCKED acceptance test of ID-2 (issue #21): no token, code or hash appears in logs. Do not edit, weaken, skip or delete: add new tests instead.
public class PairingLogHygieneTests(ApiFixture api) : PairingTest(api)
{
    [Fact]
    public async Task No_pairing_token_code_hash_or_oauth_token_appears_in_the_logs()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var offer = await OfferAsync(browser);
        var guess = "918273";
        await RedeemCodeAsync(guess);
        var device = await ReadDeviceAsync(await RedeemTokenAsync(offer.Token, "Logged phone"));
        var rotated = await ReadDeviceAsync(await RefreshAsync(device.RefreshToken));
        await RefreshAsync(device.RefreshToken); // a replay: the noisiest thing that can happen
        await RefreshAsync("made-up-refresh-token-value");
        await RedeemTokenAsync("made-up-pairing-token-value");
        await AsDeviceAsync(HttpMethod.Get, "/api/v1/me", rotated.AccessToken);
        var id = (await SessionsAsync(browser)).Single().GetProperty("id").GetGuid();
        await browser.SendAsync(HttpMethod.Delete, $"/api/v1/me/sessions/{id}", null);

        string[] hashes;
        await using (var context = Db.CreateContext())
        {
            var row = await context.DevicePairings.AsNoTracking().SingleAsync();
            hashes = [row.TokenHash, row.CodeHash];
        }

        List<string> secrets =
        [
            offer.Token, device.AccessToken, device.RefreshToken, rotated.AccessToken, rotated.RefreshToken,
            "made-up-refresh-token-value", "made-up-pairing-token-value", .. hashes,
        ];
        App.Logs.Lines.Count.ShouldBeGreaterThan(0, "the capture should see the app's log lines");
        foreach (var secret in secrets)
        {
            App.Logs.Lines.Where(line => line.Contains(secret, StringComparison.Ordinal)).ShouldBeEmpty($"a log line contains '{secret[..8]}…'");
        }

        // The 6-digit codes are short, so look for them as whole numbers rather than as part of some other number.
        foreach (var code in new[] { offer.Code, guess })
        {
            var whole = new Regex($@"(?<!\d){code}(?!\d)");
            App.Logs.Lines.Where(line => whole.IsMatch(line)).ShouldBeEmpty($"a log line contains the code {code}");
        }
    }
}
