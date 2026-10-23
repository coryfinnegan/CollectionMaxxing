using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Auth;

/// <summary>Base class for tests that sign in for real: each test gets its own app, clock and log capture.</summary>
public abstract class AuthTest(ApiFixture api) : ApiTest(api)
{
    protected AuthApp App { get; private set; } = null!;

    /// <summary>Extra configuration for this test's app (see <see cref="AuthApp"/> for the defaults).</summary>
    protected virtual Dictionary<string, string?>? Settings => MoreSettings;

    /// <summary>Settings a subclass adds (see <see cref="Settings"/>).</summary>
    protected virtual Dictionary<string, string?>? MoreSettings => null;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        App = new AuthApp(Db.ConnectionString, Settings);
    }

    public override async ValueTask DisposeAsync()
    {
        await App.DisposeAsync();
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    protected async Task<int> CountOwnersAsync()
    {
        await using var context = Db.CreateContext();
        return await context.LibraryMembers.IgnoreQueryFilters().CountAsync(m => m.Role == LibraryRole.Owner);
    }

    protected async Task<int> CountUsersAsync()
    {
        await using var context = Db.CreateContext();
        return await context.Users.CountAsync();
    }

    /// <summary>Sets up the owner and returns their signed-in browser and who they are.</summary>
    protected async Task<(WebClient Browser, WebClient.SignedIn Owner)> SetUpOwnerAsync()
    {
        var browser = App.NewBrowser(TestIdentity.Google("Owner"));
        var owner = await browser.SetUpOwnerAsync(await App.IssueCodeAsync(CollectionMaxxing.Domain.Accounts.SetupCodePurpose.Setup));
        return (browser, owner);
    }

    /// <summary>The phone of the person signed in on <paramref name="browser"/>: an Auth0 access token for the same identity.</summary>
    protected HttpClient PhoneOf(WebClient browser) => App.NewPhone(browser.Identity.Subject);

    /// <summary>
    /// The phone of the person signed in on <paramref name="browser"/> signs in to Auth0 with the same identity and holds an access
    /// token. (Registering phones with the server is AUTH-2.) <paramref name="deviceName"/> is for reading the test.
    /// </summary>
    protected Task<PhoneSignIn> SignInPhoneAsync(WebClient browser, string deviceName = "Cory's iPhone", HttpClient? phone = null) =>
        Task.FromResult(new PhoneSignIn(App.Auth0.AccessToken(browser.Identity.Subject), phone ?? App.NewBrowser().Http));

    /// <summary>An API call the way the phone makes it: its own connection, the access token as a bearer.</summary>
    protected Task<HttpResponseMessage> AsDeviceAsync(HttpMethod method, string url, string accessToken, HttpClient? phone = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        return (phone ?? App.NewBrowser().Http).SendAsync(request);
    }

    protected async Task<int> CountLoginsAsync()
    {
        await using var context = Db.CreateContext();
        return await context.Logins.CountAsync();
    }

    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected static async Task<JsonElement> ReadAsync(HttpResponseMessage response) => await ReadJsonAsync(response);

    protected static async Task ShouldBeProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(status, body);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json", body);
    }
}

/// <summary>A phone signed in to Auth0: its access token, and its connection.</summary>
public sealed record PhoneSignIn(string AccessToken, HttpClient Http);
