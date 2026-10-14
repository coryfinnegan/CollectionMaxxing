using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Auth;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Accounts;
using CollectionMaxxing.TestSupport;
using WebClient = CollectionMaxxing.Api.Tests.Auth.WebClient;

namespace CollectionMaxxing.Api.Tests.Pairing;

/// <summary>What the web app shows in the "Add a device" dialog (the body of <c>POST /api/v1/me/devices/pair</c>).</summary>
public sealed record PairingOffer(string ServerUrl, string Token, string Code, DateTimeOffset ExpiresAt);

/// <summary>A paired phone: its tokens and the lifetime the server announced for the access token.</summary>
public sealed record PairedDevice(string AccessToken, string RefreshToken, int ExpiresIn);

/// <summary>
/// Base class for the device-pairing tests: real passkey sign-in on the web side (<see cref="AuthTest"/>), and a "phone" that
/// speaks only HTTP to <c>/connect/token</c> and sends bearer tokens, like the iPhone app does.
/// </summary>
public abstract class PairingTest(ApiFixture api) : AuthTest(api)
{
    public const string PublicUrl = "https://pc.tailnet.ts.net";

    private HttpClient? _phone;

    protected override Dictionary<string, string?>? Settings
    {
        get
        {
            var settings = new Dictionary<string, string?> { ["Server:PublicUrl"] = PublicUrl };
            foreach (var (key, value) in MoreSettings ?? [])
            {
                settings[key] = value;
            }

            return settings;
        }
    }

    protected virtual Dictionary<string, string?>? MoreSettings => null;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        // Token lifetimes are whole seconds. Starting the clock on a whole second makes "10 minutes" exactly 600, not 599.4.
        var fraction = TimeSpan.FromTicks(App.Time.GetUtcNow().Ticks % TimeSpan.TicksPerSecond);
        App.Time.Advance(TimeSpan.FromSeconds(1) - fraction);
    }

    /// <summary>The phone's connection: no cookies, ever.</summary>
    protected HttpClient Phone => _phone ??= App.NewBrowser().Http;

    protected static string Sha256Hex(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>The web app asks for a pairing: the QR code payload and the 6-digit fallback.</summary>
    protected static async Task<PairingOffer> OfferAsync(WebClient browser)
    {
        var response = await browser.PostAsync("/api/v1/me/devices/pair");
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<PairingOffer>(Json) ?? throw new InvalidOperationException("No body.");
    }

    protected Task<HttpResponseMessage> RedeemTokenAsync(string token, string deviceName = "Cory's iPhone", HttpClient? phone = null) =>
        FormAsync(phone ?? Phone, ("grant_type", "urn:cm:device_pairing"), ("pairing_token", token), ("device_name", deviceName));

    protected Task<HttpResponseMessage> RedeemCodeAsync(string code, string deviceName = "Cory's iPhone", HttpClient? phone = null) =>
        FormAsync(phone ?? Phone, ("grant_type", "urn:cm:device_pairing"), ("pairing_code", code), ("device_name", deviceName));

    protected Task<HttpResponseMessage> RefreshAsync(string refreshToken, HttpClient? phone = null) =>
        FormAsync(phone ?? Phone, ("grant_type", "refresh_token"), ("refresh_token", refreshToken));

    protected static async Task<HttpResponseMessage> FormAsync(HttpClient http, params (string Key, string Value)[] fields)
    {
        using var form = new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));
        return await http.PostAsync("/connect/token", form);
    }

    protected static async Task<PairedDevice> ReadDeviceAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        return new PairedDevice(root.GetProperty("access_token").GetString()!, root.GetProperty("refresh_token").GetString()!, root.GetProperty("expires_in").GetInt32());
    }

    /// <summary>The whole happy path: the web app makes a pairing, the phone redeems the token.</summary>
    protected async Task<PairedDevice> PairAsync(WebClient browser, string deviceName = "Cory's iPhone", HttpClient? phone = null)
    {
        var offer = await OfferAsync(browser);
        return await ReadDeviceAsync(await RedeemTokenAsync(offer.Token, deviceName, phone));
    }

    /// <summary>An API call the way the phone makes it.</summary>
    protected Task<HttpResponseMessage> AsDeviceAsync(HttpMethod method, string url, string accessToken, HttpClient? phone = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return (phone ?? Phone).SendAsync(request);
    }

    protected static async Task<JsonElement[]> SessionsAsync(WebClient browser)
    {
        var response = await browser.GetAsync("/api/v1/me/sessions");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return [.. (await ReadAsync(response)).GetProperty("items").EnumerateArray()];
    }

    /// <summary>The error code of an OAuth error response (<c>invalid_grant</c>, …).</summary>
    protected static async Task<string?> OAuthErrorAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
    }

    /// <summary>
    /// Another user's pairing, put in the database directly (there is no second sign-in in these tests): returns the user and the
    /// token and code a phone would have been given. Hashes are the plain hex SHA-256 of the text, which is what the server stores.
    /// </summary>
    protected async Task<(Guid UserId, string Token, string Code)> SeedPairingForAnotherUserAsync()
    {
        var userId = await TestData.CreateUserAsync(Db);
        var token = "seeded-token-" + Guid.NewGuid().ToString("N");
        var code = Random.Shared.Next(100000, 999999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var now = App.Time.GetUtcNow();
        await using var context = Db.CreateContext();
        context.DevicePairings.Add(new DevicePairing
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            TokenHash = Sha256Hex(token),
            CodeHash = Sha256Hex(code),
            CreatedAt = now,
            ExpiresAt = now + TimeSpan.FromMinutes(2),
        });
        await context.SaveChangesAsync();
        return (userId, token, code);
    }

    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
