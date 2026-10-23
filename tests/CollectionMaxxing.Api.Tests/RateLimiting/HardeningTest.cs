using CollectionMaxxing.Api.Tests.Auth;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Accounts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using WebClient = CollectionMaxxing.Api.Tests.Auth.WebClient;

namespace CollectionMaxxing.Api.Tests.RateLimiting;

/// <summary>
/// Base class for the rate-limit tests: the real app (real Auth0 sign-in through the fake tenant) in which each caller can come from
/// its own network address (<see cref="From"/>), because the limits are about who is calling.
/// </summary>
public abstract class HardeningTest(ApiFixture api) : ApiTest(api)
{
    protected AuthApp App { get; private set; } = null!;

    protected virtual Dictionary<string, string?> MoreSettings => [];

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        App = new AuthApp(Db.ConnectionString, MoreSettings, services => services.AddSingleton<IStartupFilter, PeerFilter>());
    }

    public override async ValueTask DisposeAsync()
    {
        await App.DisposeAsync();
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>A client (no cookies) whose connection comes from <paramref name="peer"/>, optionally carrying an <c>X-Forwarded-For</c>.</summary>
    protected HttpClient From(string peer, string? forwardedFor = null) => Browser(peer, forwardedFor).Http;

    /// <summary>A browser whose connection comes from <paramref name="peer"/>, optionally carrying an <c>X-Forwarded-For</c>.</summary>
    protected WebClient Browser(string peer, string? forwardedFor = null, TestIdentity? identity = null)
    {
        var browser = App.NewBrowser(identity);
        browser.Http.DefaultRequestHeaders.Add(PeerFilter.Header, peer);
        if (forwardedFor is not null)
        {
            browser.Http.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedFor);
        }

        return browser;
    }

    protected async Task<(WebClient Browser, WebClient.SignedIn Owner)> SetUpOwnerAsync(WebClient? browser = null)
    {
        browser ??= App.NewBrowser(TestIdentity.Google("Owner"));
        var owner = await browser.SetUpOwnerAsync(await App.IssueCodeAsync(SetupCodePurpose.Setup));
        return (browser, owner);
    }
}
