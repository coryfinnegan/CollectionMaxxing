using System.Net;
using CollectionMaxxing.Api.Tests.Support;

namespace CollectionMaxxing.Api.Tests.Pairing;

/// <summary>
/// A paired phone refreshes its token about six times an hour and once per cold start. The hourly limit on the sign-in endpoints must never
/// count that: a refused refresh looks like "revoked" and would un-pair the owner's own phone.
/// </summary>
public class RefreshNotLimitedTests(ApiFixture api) : PairingTest(api)
{
    private const int HourlyLimit = 8;

    protected override Dictionary<string, string?>? MoreSettings => new() { ["Security:Limits:AuthHourly:Permits"] = HourlyLimit.ToString(System.Globalization.CultureInfo.InvariantCulture) };

    [Fact]
    public async Task A_paired_phone_can_refresh_far_more_often_than_the_hourly_auth_limit_and_stays_signed_in()
    {
        var (browser, _) = await SetUpOwnerAsync();
        var device = await PairAsync(browser);

        for (var refresh = 0; refresh < HourlyLimit * 2; refresh++)
        {
            var response = await RefreshAsync(device.RefreshToken);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, $"refresh {refresh + 1}: {await response.Content.ReadAsStringAsync()}");
            device = await ReadDeviceAsync(response);
        }

        (await AsDeviceAsync(HttpMethod.Get, "/api/v1/me", device.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
