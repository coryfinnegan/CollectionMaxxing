using System.Net;
using CollectionMaxxing.Api.Tests.Support;

namespace CollectionMaxxing.Api.Tests.Pairing;

// LOCKED acceptance tests of ID-2 (issue #21), revoking devices and keeping users apart. Do not edit, weaken, skip or delete: add new tests instead.
public class DeviceSessionTests(ApiFixture api) : PairingTest(api)
{
    [Fact]
    public async Task Revoking_a_device_makes_its_next_api_call_401_immediately_and_its_refresh_fail()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var device = await PairAsync(browser);
        (await AsDeviceAsync(HttpMethod.Get, "/api/v1/me", device.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var session = (await SessionsAsync(browser)).Single();

        var revoked = await browser.SendAsync(HttpMethod.Delete, $"/api/v1/me/sessions/{session.GetProperty("id").GetGuid()}", null);

        revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // The very next call, with an access token that has nine minutes left, is refused.
        var next = await AsDeviceAsync(HttpMethod.Get, "/api/v1/me", device.AccessToken);
        next.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var refresh = await RefreshAsync(device.RefreshToken);
        refresh.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await OAuthErrorAsync(refresh)).ShouldBe("invalid_grant");
        (await SessionsAsync(browser)).Single().GetProperty("status").GetString().ShouldBe("revoked");
    }

    [Fact]
    public async Task Revoking_one_device_leaves_the_others_working()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var phone = await PairAsync(browser, "Phone");
        var tablet = await PairAsync(browser, "Tablet", App.NewBrowser().Http);
        var phoneSession = (await SessionsAsync(browser)).Single(s => s.GetProperty("deviceName").GetString() == "Phone");

        (await browser.SendAsync(HttpMethod.Delete, $"/api/v1/me/sessions/{phoneSession.GetProperty("id").GetGuid()}", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await AsDeviceAsync(HttpMethod.Get, "/api/v1/me", phone.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await AsDeviceAsync(HttpMethod.Get, "/api/v1/me", tablet.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RefreshAsync(tablet.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_device_can_revoke_itself_and_is_marked_as_the_current_session_while_it_does()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var device = await PairAsync(browser);

        var own = await AsDeviceAsync(HttpMethod.Get, "/api/v1/me/sessions", device.AccessToken);
        var sessions = (await ReadAsync(own)).GetProperty("items").EnumerateArray().ToArray();
        var current = sessions.Single();
        current.GetProperty("isCurrent").GetBoolean().ShouldBeTrue();

        var revoke = await AsDeviceAsync(HttpMethod.Delete, $"/api/v1/me/sessions/{current.GetProperty("id").GetGuid()}", device.AccessToken);

        revoke.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await AsDeviceAsync(HttpMethod.Get, "/api/v1/me", device.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Revoking_a_device_twice_is_not_an_error()
    {
        var (browser, _) = await SetUpOwnerAsync();
        await PairAsync(browser);
        var id = (await SessionsAsync(browser)).Single().GetProperty("id").GetGuid();

        (await browser.SendAsync(HttpMethod.Delete, $"/api/v1/me/sessions/{id}", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await browser.SendAsync(HttpMethod.Delete, $"/api/v1/me/sessions/{id}", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task The_list_tells_devices_apart_by_name_and_says_when_each_was_made_and_last_seen()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var created = App.Time.GetUtcNow();
        var phone = await PairAsync(browser, "Phone");
        await PairAsync(browser, "Tablet", App.NewBrowser().Http);
        App.Time.Advance(TimeSpan.FromHours(1));
        await RefreshAsync(phone.RefreshToken);

        var sessions = await SessionsAsync(browser);

        sessions.Select(s => s.GetProperty("deviceName").GetString()).Order().ShouldBe(["Phone", "Tablet"]);
        var seen = sessions.Single(s => s.GetProperty("deviceName").GetString() == "Phone");
        seen.GetProperty("createdAt").GetDateTimeOffset().ShouldBe(created, TimeSpan.FromSeconds(2));
        seen.GetProperty("lastSeenAt").GetDateTimeOffset().ShouldBe(created + TimeSpan.FromHours(1), TimeSpan.FromSeconds(2));
        sessions.ShouldAllBe(s => s.GetProperty("isCurrent").GetBoolean() == false);
    }

    [Fact]
    public async Task A_user_cannot_list_another_users_devices()
    {
        var (browser, _) = await SetUpOwnerAsync();
        await PairAsync(browser, "Owner phone");
        var (_, token, _) = await SeedPairingForAnotherUserAsync();
        var theirs = await ReadDeviceAsync(await RedeemTokenAsync(token, "Their phone", App.NewBrowser().Http));

        var seenByOwner = await SessionsAsync(browser);
        var seenByThem = (await ReadAsync(await AsDeviceAsync(HttpMethod.Get, "/api/v1/me/sessions", theirs.AccessToken)))
            .GetProperty("items").EnumerateArray().ToArray();

        seenByOwner.Select(s => s.GetProperty("deviceName").GetString()).ShouldBe(["Owner phone"]);
        seenByThem.Select(s => s.GetProperty("deviceName").GetString()).ShouldBe(["Their phone"]);
    }

    [Fact]
    public async Task A_user_cannot_revoke_another_users_device_and_is_told_404_not_403()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var ownerDevice = await PairAsync(browser, "Owner phone");
        var ownerSessionId = (await SessionsAsync(browser)).Single().GetProperty("id").GetGuid();
        var (_, token, _) = await SeedPairingForAnotherUserAsync();
        var theirPhone = App.NewBrowser().Http;
        var theirs = await ReadDeviceAsync(await RedeemTokenAsync(token, "Their phone", theirPhone));
        var theirSessionId = (await ReadAsync(await AsDeviceAsync(HttpMethod.Get, "/api/v1/me/sessions", theirs.AccessToken, theirPhone)))
            .GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid();

        // They try to revoke the owner's phone; the owner tries to revoke theirs.
        var byThem = await AsDeviceAsync(HttpMethod.Delete, $"/api/v1/me/sessions/{ownerSessionId}", theirs.AccessToken, theirPhone);
        var byOwner = await browser.SendAsync(HttpMethod.Delete, $"/api/v1/me/sessions/{theirSessionId}", null);

        await ShouldBeProblemAsync(byThem, HttpStatusCode.NotFound);
        await ShouldBeProblemAsync(byOwner, HttpStatusCode.NotFound);
        (await AsDeviceAsync(HttpMethod.Get, "/api/v1/me", ownerDevice.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await AsDeviceAsync(HttpMethod.Get, "/api/v1/me", theirs.AccessToken, theirPhone)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Revoking_an_id_that_is_not_a_device_at_all_is_404()
    {
        var (browser, _) = await SetUpOwnerAsync();

        var response = await browser.SendAsync(HttpMethod.Delete, $"/api/v1/me/sessions/{Guid.NewGuid()}", null);

        await ShouldBeProblemAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Anonymous_callers_can_neither_list_nor_revoke_devices()
    {
        await SetUpOwnerAsync();
        using var stranger = App.NewBrowser();

        (await stranger.GetAsync("/api/v1/me/sessions")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await stranger.SendAsync(HttpMethod.Delete, $"/api/v1/me/sessions/{Guid.NewGuid()}", null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Unpairing_with_connect_revoke_kills_the_refresh_token_and_the_access_token_too()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var device = await PairAsync(browser);

        var revoke = await FormToAsync("/connect/revoke", ("token", device.RefreshToken), ("token_type_hint", "refresh_token"));

        revoke.StatusCode.ShouldBe(HttpStatusCode.OK);
        var refresh = await RefreshAsync(device.RefreshToken);
        refresh.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AsDeviceAsync(HttpMethod.Get, "/api/v1/me", device.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SessionsAsync(browser)).Single().GetProperty("status").GetString().ShouldBe("revoked");
    }

    [Fact]
    public async Task Revoking_a_token_nobody_knows_is_a_quiet_success()
    {
        await SetUpOwnerAsync();

        var revoke = await FormToAsync("/connect/revoke", ("token", "not-a-token"));

        revoke.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task<HttpResponseMessage> FormToAsync(string url, params (string Key, string Value)[] fields)
    {
        using var form = new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));
        return await Phone.PostAsync(url, form);
    }
}
