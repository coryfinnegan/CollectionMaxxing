using System.Net;
using CollectionMaxxing.Api.Tests.Support;

namespace CollectionMaxxing.Api.Tests.Pairing;

// LOCKED acceptance tests of ID-2 (issue #21), refresh rotation and lifetimes. Do not edit, weaken, skip or delete: add new tests instead.
public class RefreshRotationTests(ApiFixture api) : PairingTest(api)
{
    [Fact]
    public async Task Using_refresh_token_A_gives_B_and_A_is_dead_and_replaying_A_revokes_the_whole_chain()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var paired = await PairAsync(browser);
        var a = paired.RefreshToken;

        var rotated = await ReadDeviceAsync(await RefreshAsync(a));
        var b = rotated.RefreshToken;

        b.ShouldNotBe(a);
        (await AsDeviceAsync(HttpMethod.Get, "/api/v1/me", rotated.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // A is spent.
        var replay = await RefreshAsync(a);
        replay.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await OAuthErrorAsync(replay)).ShouldBe("invalid_grant");

        // And replaying it took B down with it, along with the device.
        var useB = await RefreshAsync(b);
        useB.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await OAuthErrorAsync(useB)).ShouldBe("invalid_grant");
        (await AsDeviceAsync(HttpMethod.Get, "/api/v1/me", rotated.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SessionsAsync(browser)).Single().GetProperty("status").GetString().ShouldBe("revoked");
    }

    [Fact]
    public async Task A_rotated_chain_keeps_working_while_each_new_token_is_used_once()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var current = await PairAsync(browser);

        for (var i = 0; i < 5; i++)
        {
            var next = await ReadDeviceAsync(await RefreshAsync(current.RefreshToken));
            next.RefreshToken.ShouldNotBe(current.RefreshToken);
            current = next;
        }

        (await AsDeviceAsync(HttpMethod.Get, "/api/v1/me", current.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var device = (await SessionsAsync(browser)).Single();
        device.GetProperty("status").GetString().ShouldBe("active");
    }

    [Fact]
    public async Task An_access_token_is_not_a_refresh_token()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var device = await PairAsync(browser);

        var response = await RefreshAsync(device.AccessToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Access_tokens_last_10_minutes()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var device = await PairAsync(browser);
        device.ExpiresIn.ShouldBe(600);

        App.Time.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59));
        (await AsDeviceAsync(HttpMethod.Get, "/api/v1/me", device.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        App.Time.Advance(TimeSpan.FromSeconds(2));
        (await AsDeviceAsync(HttpMethod.Get, "/api/v1/me", device.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // The refresh token is what gets a new one.
        var renewed = await ReadDeviceAsync(await RefreshAsync(device.RefreshToken));
        (await AsDeviceAsync(HttpMethod.Get, "/api/v1/me", renewed.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_refresh_token_unused_for_90_days_and_a_second_is_dead()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var device = await PairAsync(browser);

        App.Time.Advance(TimeSpan.FromDays(90) + TimeSpan.FromSeconds(1));
        var response = await RefreshAsync(device.RefreshToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await OAuthErrorAsync(response)).ShouldBe("invalid_grant");
    }

    [Fact]
    public async Task A_refresh_token_used_after_89_days_still_works()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var device = await PairAsync(browser);

        App.Time.Advance(TimeSpan.FromDays(89));

        (await ReadDeviceAsync(await RefreshAsync(device.RefreshToken))).RefreshToken.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Every_refresh_slides_the_90_days_forward_so_a_phone_in_use_never_has_to_pair_again()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var device = await PairAsync(browser);

        // Four refreshes 60 days apart: 240 days after pairing, far past the first token's 90.
        for (var i = 0; i < 4; i++)
        {
            App.Time.Advance(TimeSpan.FromDays(60));
            device = await ReadDeviceAsync(await RefreshAsync(device.RefreshToken));
        }

        (await AsDeviceAsync(HttpMethod.Get, "/api/v1/me", device.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // ...and the newest token is good for 90 days from its own refresh, not a day longer.
        App.Time.Advance(TimeSpan.FromDays(90) + TimeSpan.FromSeconds(1));
        (await RefreshAsync(device.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_refresh_token_made_up_by_a_stranger_is_refused()
    {
        await SetUpOwnerAsync();

        var response = await RefreshAsync("not-a-token");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await OAuthErrorAsync(response)).ShouldBe("invalid_grant");
    }

    [Fact]
    public async Task A_refresh_for_a_user_who_has_been_deleted_is_refused()
    {
        var (browser, owner) = await SetUpOwnerAsync();
        var device = await PairAsync(browser);
        await using (var context = Db.CreateContext())
        {
            var user = await context.Users.FindAsync(owner.UserId);
            user!.DeletedAt = App.Time.GetUtcNow();
            await context.SaveChangesAsync();
        }

        var response = await RefreshAsync(device.RefreshToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await OAuthErrorAsync(response)).ShouldBe("invalid_grant");
    }
}
