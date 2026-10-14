using CollectionMaxxing.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Pairing;

// LOCKED acceptance test of ID-2 (issue #21): only hashes of the token and the code are stored. Do not edit or weaken.
public class PairingStorageTests(ApiFixture api) : PairingTest(api)
{
    [Fact]
    public async Task Only_hashes_of_the_pairing_token_and_code_are_stored()
    {
        var (browser, owner) = await SetUpOwnerAsync();

        var offer = await OfferAsync(browser);

        await using var context = Db.CreateContext();
        var row = await context.DevicePairings.AsNoTracking().SingleAsync();
        row.UserId.ShouldBe(owner.UserId);
        row.TokenHash.ShouldNotBe(offer.Token);
        row.TokenHash.ShouldNotContain(offer.Token);
        row.CodeHash.ShouldNotBe(offer.Code);
        row.CodeHash.ShouldNotContain(offer.Code);
        row.TokenHash.ShouldNotBe(row.CodeHash);
        row.TokenHash.Length.ShouldBeGreaterThanOrEqualTo(32);
        row.CodeHash.Length.ShouldBeGreaterThanOrEqualTo(32);
        row.ExpiresAt.ShouldBe(row.CreatedAt + TimeSpan.FromMinutes(2));
        await AssertNothingElseKeepsThePlaintextAsync(context, offer.Token, offer.Code);
    }

    [Fact]
    public async Task Nothing_in_the_database_holds_a_refresh_token_or_an_access_token_in_the_clear()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var offer = await OfferAsync(browser);

        var device = await ReadDeviceAsync(await RedeemTokenAsync(offer.Token));
        var rotated = await ReadDeviceAsync(await RefreshAsync(device.RefreshToken));

        await using var context = Db.CreateContext();
        foreach (var secret in new[] { offer.Token, device.AccessToken, device.RefreshToken, rotated.AccessToken, rotated.RefreshToken })
        {
            await AssertNothingElseKeepsThePlaintextAsync(context, secret, null);
        }

        var row = await context.DevicePairings.AsNoTracking().SingleAsync();
        row.UsedAt.ShouldNotBeNull();
        row.AuthorizationId.ShouldNotBeNull();
    }

    private static async Task AssertNothingElseKeepsThePlaintextAsync(DbContext context, string secret, string? alsoThis)
    {
        var tokenRows = await context.Database
            .SqlQuery<int>($"select count(*)::int as \"Value\" from openiddict_tokens where position({secret} in coalesce(payload, '')) > 0 or reference_id = {secret}")
            .SingleAsync();
        tokenRows.ShouldBe(0);

        var pairingRows = await context.Database
            .SqlQuery<int>($"select count(*)::int as \"Value\" from device_pairings where token_hash = {secret} or code_hash = {alsoThis ?? secret} or position({secret} in coalesce(device_name, '')) > 0")
            .SingleAsync();
        pairingRows.ShouldBe(0);
    }
}
