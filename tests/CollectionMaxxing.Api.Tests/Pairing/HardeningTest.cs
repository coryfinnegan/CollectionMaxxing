using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Auth;
using CollectionMaxxing.Api.Tests.RateLimiting;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Accounts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using WebClient = CollectionMaxxing.Api.Tests.Auth.WebClient;

namespace CollectionMaxxing.Api.Tests.Pairing;

/// <summary>
/// Base class for the ID-2b tests: the real app (real passkey sign-in) in which each "phone" can come from its own network
/// address (<see cref="From"/>), because the pairing and rate-limit rules are about who is calling.
/// </summary>
public abstract class HardeningTest(ApiFixture api) : ApiTest(api)
{
    public const string PublicUrl = "https://pc.tailnet.ts.net";

    protected AuthApp App { get; private set; } = null!;

    protected virtual Dictionary<string, string?> MoreSettings => [];

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        var settings = new Dictionary<string, string?> { ["Server:PublicUrl"] = PublicUrl };
        foreach (var (key, value) in MoreSettings)
        {
            settings[key] = value;
        }

        App = new AuthApp(Db.ConnectionString, settings, services => services.AddSingleton<IStartupFilter, PeerFilter>());
    }

    public override async ValueTask DisposeAsync()
    {
        await App.DisposeAsync();
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>A phone (no cookies) whose connection comes from <paramref name="peer"/>, optionally carrying an <c>X-Forwarded-For</c>.</summary>
    protected HttpClient From(string peer, string? forwardedFor = null)
    {
        var http = App.NewBrowser().Http;
        http.DefaultRequestHeaders.Add(PeerFilter.Header, peer);
        if (forwardedFor is not null)
        {
            http.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedFor);
        }

        return http;
    }

    protected async Task<(WebClient Browser, WebClient.SignedIn Owner)> SetUpOwnerAsync()
    {
        var browser = App.NewBrowser();
        var owner = await browser.SetUpOwnerAsync(await App.IssueCodeAsync(SetupCodePurpose.Setup));
        return (browser, owner);
    }

    protected static async Task<PairingOffer> OfferAsync(WebClient browser)
    {
        var response = await browser.PostAsync("/api/v1/me/devices/pair");
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<PairingOffer>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("No body.");
    }

    protected static Task<HttpResponseMessage> RedeemTokenAsync(HttpClient phone, string token, string deviceName = "Cory's iPhone") =>
        FormAsync(phone, ("grant_type", "urn:cm:device_pairing"), ("pairing_token", token), ("device_name", deviceName));

    protected static Task<HttpResponseMessage> RedeemCodeAsync(HttpClient phone, string code, string deviceName = "Cory's iPhone") =>
        FormAsync(phone, ("grant_type", "urn:cm:device_pairing"), ("pairing_code", code), ("device_name", deviceName));

    protected static async Task<HttpResponseMessage> FormAsync(HttpClient http, params (string Key, string Value)[] fields)
    {
        using var form = new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));
        return await http.PostAsync("/connect/token", form);
    }

    /// <summary>Six-digit guesses that are certainly not <paramref name="right"/>.</summary>
    protected static string[] WrongCodes(string right, int count) =>
        [.. Enumerable.Range(0, 30).Select(i => (100000 + (i * 7919)).ToString(System.Globalization.CultureInfo.InvariantCulture)).Where(c => c != right).Take(count)];
}
