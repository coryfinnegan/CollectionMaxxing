using System.Net;
using CollectionMaxxing.Api.Tests.Invites;
using CollectionMaxxing.Api.Tests.Pairing;
using CollectionMaxxing.Api.Tests.Support;
using WebClient = CollectionMaxxing.Api.Tests.Auth.WebClient;

namespace CollectionMaxxing.Api.Tests.Security;

// LOCKED acceptance test of QA-1a (issue #166), the first one: a replayed refresh token revokes the device's chain.
// Do not edit, weaken, skip or delete: add new tests instead.
public class RefreshTokenAbuseTests(ApiFixture api) : InviteTest(api)
{
    private async Task ShouldBeDeadAsync(PairedDevice device, string because)
    {
        var refresh = await RefreshAsync(device.RefreshToken);
        refresh.StatusCode.ShouldBe(HttpStatusCode.BadRequest, because);
        (await OAuthErrorAsync(refresh)).ShouldBe("invalid_grant", because);
        (await AsDeviceAsync(HttpMethod.Get, "/api/v1/me", device.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, because);
    }

    [Fact]
    public async Task A_replayed_refresh_token_revokes_the_devices_whole_chain_however_far_the_chain_has_moved_on()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var first = await PairAsync(browser);

        // A -> B -> C -> D: the phone is happily refreshing.
        var b = await ReadDeviceAsync(await RefreshAsync(first.RefreshToken));
        var c = await ReadDeviceAsync(await RefreshAsync(b.RefreshToken));
        var d = await ReadDeviceAsync(await RefreshAsync(c.RefreshToken));
        (await AsDeviceAsync(HttpMethod.Get, "/api/v1/me", d.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // A thief who kept the very first refresh token tries it. It is refused...
        var replay = await RefreshAsync(first.RefreshToken);
        replay.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await OAuthErrorAsync(replay)).ShouldBe("invalid_grant");

        // ...and every other token of the chain dies with it, the live one and the access tokens of each step.
        await ShouldBeDeadAsync(d, "the newest token of the chain");
        await ShouldBeDeadAsync(c, "an earlier token of the chain");
        await ShouldBeDeadAsync(b, "an earlier token of the chain");
        (await SessionsAsync(browser)).Single().GetProperty("status").GetString().ShouldBe("revoked");
    }

    [Fact]
    public async Task Replaying_a_token_revokes_that_device_only_and_not_the_users_other_devices()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var phone = await PairAsync(browser, "Phone");
        var tablet = await PairAsync(browser, "Tablet", App.NewBrowser().Http);
        await ReadDeviceAsync(await RefreshAsync(phone.RefreshToken));

        (await RefreshAsync(phone.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await AsDeviceAsync(HttpMethod.Get, "/api/v1/me", tablet.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadDeviceAsync(await RefreshAsync(tablet.RefreshToken))).RefreshToken.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task A_replay_after_the_chain_was_already_revoked_stays_a_quiet_refusal_and_does_not_throw()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var device = await PairAsync(browser);
        await ReadDeviceAsync(await RefreshAsync(device.RefreshToken));

        for (var i = 0; i < 3; i++)
        {
            var again = await RefreshAsync(device.RefreshToken);
            again.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await OAuthErrorAsync(again)).ShouldBe("invalid_grant");
        }
    }

    [Fact]
    public async Task Two_refreshes_at_the_same_moment_with_one_token_cannot_both_win()
    {
        var (browser, _) = await SetUpOwnerAsync();

        for (var round = 0; round < 4; round++)
        {
            var device = await PairAsync(browser, $"Phone {round}", App.NewBrowser().Http);

            var attempts = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => RefreshAsync(device.RefreshToken, App.NewBrowser().Http))));

            attempts.Count(a => a.StatusCode == HttpStatusCode.OK).ShouldBeLessThanOrEqualTo(1, $"round {round}");
            attempts.ShouldAllBe(a => (int)a.StatusCode < 500, $"round {round}: a race must never be a server error");
            attempts.Where(a => a.StatusCode != HttpStatusCode.OK).ShouldAllBe(a => a.StatusCode == HttpStatusCode.BadRequest);

            // A loser used a token that had already been spent: that is a replay, so it takes the winner's tokens down with the chain.
            if (attempts.Any(a => a.StatusCode == HttpStatusCode.BadRequest))
            {
                foreach (var winner in attempts.Where(a => a.StatusCode == HttpStatusCode.OK))
                {
                    await ShouldBeDeadAsync(await ReadDeviceAsync(winner), $"round {round}: the winner of a race the others lost");
                }
            }
        }
    }

    [Fact]
    public async Task Whoever_wins_a_race_on_one_token_cannot_keep_the_chain_alive_because_the_loser_is_a_replay()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var device = await PairAsync(browser);

        var attempts = await Task.WhenAll(
            Task.Run(() => RefreshAsync(device.RefreshToken, App.NewBrowser().Http)),
            Task.Run(() => RefreshAsync(device.RefreshToken, App.NewBrowser().Http)));

        // Either both were refused, or one won and then a later use of the old token (a thief's) takes the winner down too.
        attempts.Count(a => a.StatusCode == HttpStatusCode.OK).ShouldBeLessThanOrEqualTo(1);
        var thief = await RefreshAsync(device.RefreshToken);
        thief.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var winner = attempts.SingleOrDefault(a => a.StatusCode == HttpStatusCode.OK);
        if (winner is not null)
        {
            var won = await ReadDeviceAsync(winner);
            (await RefreshAsync(won.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
    }

    public static TheoryData<string> WaysToEndASession =>
    [
        "unpairing the refresh token with /connect/revoke",
        "unpairing the access token with /connect/revoke",
        "revoking the device in the web app",
        "removing the member who owns the phone",
    ];

    [Theory]
    [MemberData(nameof(WaysToEndASession))]
    public async Task After_the_session_ended_the_refresh_token_is_dead_and_so_is_the_access_token_that_still_had_minutes_left(string how)
    {
        var (owner, ownerAccount) = await SetUpOwnerAsync();
        var libraryId = ownerAccount.LibraryId!.Value;
        var (member, memberAccount) = await AddMemberAsync(owner, libraryId, "editor");
        var phone = await PairAsync(member, "Phone");
        (await AsDeviceAsync(HttpMethod.Get, "/api/v1/me", phone.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        switch (how)
        {
            case "unpairing the refresh token with /connect/revoke":
                (await RevokeAsync(phone.RefreshToken, "refresh_token")).StatusCode.ShouldBe(HttpStatusCode.OK);
                break;
            case "unpairing the access token with /connect/revoke":
                (await RevokeAsync(phone.AccessToken, "access_token")).StatusCode.ShouldBe(HttpStatusCode.OK);
                break;
            case "revoking the device in the web app":
                var id = (await SessionsAsync(member)).Single().GetProperty("id").GetGuid();
                (await member.SendAsync(HttpMethod.Delete, $"/api/v1/me/sessions/{id}", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
                break;
            default:
                (await owner.SendAsync(HttpMethod.Delete, $"{MembersUrl(libraryId)}/{memberAccount.UserId}", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
                break;
        }

        await ShouldBeDeadAsync(phone, how);
    }

    [Fact]
    public async Task Signing_the_web_session_out_does_not_revive_or_extend_a_phones_tokens_and_the_phone_is_not_signed_out_by_it()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var phone = await PairAsync(browser);

        (await browser.PostAsync("/account/logout")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // The phone has its own authorization; logging out the browser is not unpairing it (docs/07).
        (await AsDeviceAsync(HttpMethod.Get, "/api/v1/me", phone.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadDeviceAsync(await RefreshAsync(phone.RefreshToken))).RefreshToken.ShouldNotBeNullOrEmpty();
    }

    private async Task<HttpResponseMessage> RevokeAsync(string token, string hint)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token, ["token_type_hint"] = hint });
        return await Phone.PostAsync("/connect/revoke", form);
    }
}
