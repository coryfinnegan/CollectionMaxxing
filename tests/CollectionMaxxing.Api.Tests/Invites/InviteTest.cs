using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Auth;
using CollectionMaxxing.Api.Tests.Pairing;
using CollectionMaxxing.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;

using WebClient = CollectionMaxxing.Api.Tests.Auth.WebClient;

namespace CollectionMaxxing.Api.Tests.Invites;

/// <summary>
/// Base class for the household tests (invites, joining, members): real passkey sign-in for the owner and for each member who
/// joins, a clock the test controls, and a "phone" for the bearer-token side.
/// </summary>
public abstract class InviteTest(ApiFixture api) : PairingTest(api)
{
    protected static string InvitesUrl(Guid libraryId) => $"/api/v1/libraries/{libraryId}/invites";

    protected static string MembersUrl(Guid libraryId) => $"/api/v1/libraries/{libraryId}/members";

    /// <summary>The owner invites someone. Returns the response; <see cref="TokenOf"/> reads the token out of the link.</summary>
    protected static Task<HttpResponseMessage> InviteAsync(WebClient owner, Guid libraryId, string role = "viewer", string name = "Sam") =>
        owner.PostAsync(InvitesUrl(libraryId), new { name, role });

    /// <summary>The owner invites someone and gets the token. Fails the test if the invite wasn't created.</summary>
    protected static async Task<(string Token, JsonElement Created)> CreateInviteAsync(WebClient owner, Guid libraryId, string role = "viewer", string name = "Sam")
    {
        var response = await InviteAsync(owner, libraryId, role, name);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var created = await ReadAsync(response);
        return (TokenOf(created.GetProperty("link").GetString()!), created);
    }

    /// <summary>The token is the last segment of <c>https://host/join/{token}</c>.</summary>
    protected static string TokenOf(string link) => link[(link.LastIndexOf('/') + 1)..];

    /// <summary>The first step of joining: the browser asks for passkey creation options with the invite token.</summary>
    protected static Task<HttpResponseMessage> StartJoinAsync(WebClient browser, string token, string? displayName = "Sam") =>
        browser.PostAsync("/account/passkey/creation-options", new { inviteToken = token, displayName });

    /// <summary>The whole join: options, a new passkey, registration. Returns the new browser and the registration response.</summary>
    protected async Task<(WebClient Browser, HttpResponseMessage Registered)> JoinAsync(string token, string? displayName = "Sam")
    {
        var browser = App.NewBrowser();
        var options = await StartJoinAsync(browser, token, displayName);
        options.StatusCode.ShouldBe(HttpStatusCode.OK, await options.Content.ReadAsStringAsync());
        return (browser, await browser.RegisterAsync(options, invite: token));
    }

    /// <summary>The owner invites someone with <paramref name="role"/> and they join. Returns their signed-in browser.</summary>
    protected async Task<(WebClient Browser, WebClient.SignedIn Member)> AddMemberAsync(
        WebClient owner, Guid libraryId, string role, string name = "Sam")
    {
        var (token, _) = await CreateInviteAsync(owner, libraryId, role, name);
        var (browser, registered) = await JoinAsync(token, name);
        registered.StatusCode.ShouldBe(HttpStatusCode.Created, await registered.Content.ReadAsStringAsync());
        var member = await registered.Content.ReadFromJsonAsync<WebClient.SignedIn>(Json) ?? throw new InvalidOperationException("No body.");
        return (browser, member);
    }

    protected static async Task<JsonElement[]> ListMembersAsync(WebClient browser, Guid libraryId)
    {
        var response = await browser.GetAsync(MembersUrl(libraryId));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return [.. (await ReadAsync(response)).GetProperty("items").EnumerateArray()];
    }

    protected async Task<int> CountMembersAsync(Guid libraryId)
    {
        await using var context = Db.CreateContext();
        return await context.LibraryMembers.IgnoreQueryFilters().CountAsync(m => m.LibraryId == libraryId);
    }

    protected async Task<string> RoleOfAsync(Guid libraryId, Guid userId)
    {
        await using var context = Db.CreateContext();
        var role = await context.LibraryMembers.IgnoreQueryFilters().Where(m => m.LibraryId == libraryId && m.UserId == userId)
            .Select(m => m.Role).SingleAsync();
        return role.ToString().ToLowerInvariant();
    }

    protected static async Task<string> BodyOfAsync(HttpResponseMessage response) => await response.Content.ReadAsStringAsync();
}
