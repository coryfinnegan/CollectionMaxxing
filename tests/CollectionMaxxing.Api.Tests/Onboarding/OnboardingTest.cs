using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Tests.Auth;
using CollectionMaxxing.Api.Tests.Invites;
using CollectionMaxxing.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;

using WebClient = CollectionMaxxing.Api.Tests.Auth.WebClient;

namespace CollectionMaxxing.Api.Tests.Onboarding;

/// <summary>
/// Base class for the onboarding tests of ONB-1 (docs/15): every sign-in is an account, and people create libraries or join them with an
/// invite link or code. Real sign-in throughout (the fake Auth0 tenant on loopback), real Postgres, a clock the test controls.
/// </summary>
public abstract class OnboardingTest(ApiFixture api) : InviteTest(api)
{
    public const string LibrariesUrl = "/api/v1/libraries";
    public const string PreviewUrl = "/api/v1/invites:preview";
    public const string AcceptUrl = "/api/v1/invites:accept";

    protected static string LibraryUrl(Guid libraryId) => $"{LibrariesUrl}/{libraryId}";

    protected static string MembershipUrl(Guid libraryId) => $"{LibrariesUrl}/{libraryId}/membership";

    /// <summary>A browser that has signed in once (so the account exists), and who it is.</summary>
    protected async Task<(WebClient Browser, WebClient.SignedIn Account)> NewAccountAsync(string name = "Ana")
    {
        var browser = App.NewBrowser(TestIdentity.Google(name));
        var response = await browser.SignInAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        response.Headers.Location?.ToString().ShouldBe("/");
        return (browser, await browser.MeAsync());
    }

    /// <summary><c>POST /api/v1/libraries</c>.</summary>
    protected static Task<HttpResponseMessage> CreateLibraryAsync(WebClient browser, string? name = "Ana's library") =>
        browser.PostAsync(LibrariesUrl, new { name });

    /// <summary>Creates a library and returns its id. Fails the test if it wasn't created.</summary>
    protected static async Task<Guid> CreatedLibraryAsync(WebClient browser, string name = "Ana's library")
    {
        var response = await CreateLibraryAsync(browser, name);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await ReadAsync(response)).GetProperty("id").GetGuid();
    }

    /// <summary>The invite code the owner was shown when the invite was made (<c>ABCD-EFGH-JK</c>).</summary>
    protected static string CodeOf(JsonElement created) => created.GetProperty("code").GetString()!;

    protected static Task<HttpResponseMessage> PreviewAsync(WebClient browser, string invite) => browser.PostAsync(PreviewUrl, new { invite });

    protected static Task<HttpResponseMessage> AcceptAsync(WebClient browser, string invite) => browser.PostAsync(AcceptUrl, new { invite });

    protected static async Task<Guid[]> LibraryIdsAsync(WebClient browser)
    {
        var response = await browser.GetAsync(LibrariesUrl);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return [.. (await ReadAsync(response)).GetProperty("items").EnumerateArray().Select(l => l.GetProperty("id").GetGuid())];
    }

    protected static async Task ShouldBeCodeAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        await ShouldBeProblemAsync(response, status);
        (await ReadAsync(response)).GetProperty("code").GetString().ShouldBe(code);
    }

    /// <summary>Runs <c>cm-admin recovery-code {email}</c>. Returns the exit code and the code it printed (if any).</summary>
    protected async Task<(int ExitCode, string? Code, string Error)> RecoveryCodeForAsync(string email)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await AdminCommands.RunRecoveryCodeAsync(App.Services, email, output, error, CancellationToken.None);
        var match = System.Text.RegularExpressions.Regex.Match(output.ToString(), "Recovery code: (?<code>[A-Z0-9-]+)");
        return (exit, match.Success ? match.Groups["code"].Value : null, error.ToString());
    }

    protected async Task<int> CountRecoveryCodesAsync()
    {
        await using var context = Db.CreateContext();
        return await context.SetupCodes.CountAsync();
    }

    protected async Task<(string DisplayName, string? Email)> UserAsync(Guid userId)
    {
        await using var context = Db.CreateContext();
        var user = await context.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        return (user.DisplayName, user.Email);
    }
}
