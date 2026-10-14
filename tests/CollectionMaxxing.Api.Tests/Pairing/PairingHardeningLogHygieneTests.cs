using System.Text.RegularExpressions;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Infrastructure.Identity.Pairing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Pairing;

// LOCKED acceptance test of ID-2b (issue #63): the new paths (guessing, forwarded addresses, lockout, cleanup, key ring) leak no token,
// code, hash or key material into the logs. Do not edit, weaken, skip or delete: add new tests instead.
public class PairingHardeningLogHygieneTests(ApiFixture api) : HardeningTest(api)
{
    private readonly string _keys = Path.Combine(Path.GetTempPath(), "cm-keys-" + Guid.NewGuid().ToString("N"));

    protected override Dictionary<string, string?> MoreSettings => new()
    {
        ["Auth:KeyRing:Path"] = _keys,
        ["Auth:RateLimit:TrustedProxies:0"] = "10.0.0.1",
        ["Auth:RateLimit:PermitLimit"] = "8",
    };

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_keys))
        {
            Directory.Delete(_keys, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task No_token_code_hash_or_key_material_appears_in_the_logs()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var offer = await OfferAsync(browser);
        var guesses = WrongCodes(offer.Code, 6);
        var attacker = From("10.0.0.1", "203.0.113.5");
        foreach (var guess in guesses)
        {
            await RedeemCodeAsync(attacker, guess);
        }

        await RedeemTokenAsync(attacker, offer.Token); // refused: the guesser is locked out
        var device = await RedeemTokenAsync(From("10.0.0.1", "203.0.113.9"), offer.Token);
        device.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        for (var i = 0; i < 10; i++)
        {
            await RedeemCodeAsync(From("10.0.0.2", "198.51.100.1"), "99999" + i); // runs into the rate limit
        }

        App.Time.Advance(TimeSpan.FromDays(3));
        using (var scope = App.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDevicePairingCleanup>().PurgeAsync(CancellationToken.None);
        }

        string[] hashes;
        await using (var context = Db.CreateContext())
        {
            hashes = [.. (await context.DevicePairings.AsNoTracking().ToListAsync()).SelectMany(p => new[] { p.TokenHash, p.CodeHash })];
        }

        var keyFiles = Directory.GetFiles(_keys, "*.xml");
        keyFiles.ShouldNotBeEmpty("the key ring should have been written");
        var keyMaterial = keyFiles
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), "<value>([^<]{16,})</value>").Select(m => m.Groups[1].Value))
            .ToList();
        keyMaterial.ShouldNotBeEmpty();

        App.Logs.Lines.Count.ShouldBeGreaterThan(0, "the capture should see the app's log lines");
        foreach (var secret in new[] { offer.Token }.Concat(hashes).Concat(keyMaterial))
        {
            App.Logs.Lines.Where(line => line.Contains(secret, StringComparison.Ordinal)).ShouldBeEmpty($"a log line contains '{secret[..8]}…'");
        }

        foreach (var code in new[] { offer.Code }.Concat(guesses))
        {
            var whole = new Regex($@"(?<!\d){code}(?!\d)");
            App.Logs.Lines.Where(line => whole.IsMatch(line)).ShouldBeEmpty($"a log line contains the code {code}");
        }
    }
}
