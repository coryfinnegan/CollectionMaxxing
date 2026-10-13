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
    protected virtual Dictionary<string, string?>? Settings => null;

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
        var browser = App.NewBrowser();
        var owner = await browser.SetUpOwnerAsync(await App.IssueCodeAsync(CollectionMaxxing.Domain.Accounts.SetupCodePurpose.Setup));
        return (browser, owner);
    }

    protected static async Task<JsonElement> ReadAsync(HttpResponseMessage response) => await ReadJsonAsync(response);

    protected static async Task ShouldBeProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(status, body);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json", body);
    }
}
