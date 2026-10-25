using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Auth;
using CollectionMaxxing.Api.Tests.Support;
using Microsoft.EntityFrameworkCore;

using WebClient = CollectionMaxxing.Api.Tests.Auth.WebClient;

namespace CollectionMaxxing.Api.Tests.Invites;

/// <summary>
/// Base class for the household tests (invites, joining, members): real Auth0 sign-in (the fake tenant) for the owner and for each
/// member who joins, a clock the test controls, and a "phone" for the bearer-token side.
/// </summary>
public abstract class InviteTest(ApiFixture api) : AuthTest(api)
{
    public const string PublicUrl = FakeAuth0.PublicOrigin;

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

    /// <summary>
    /// Joining (docs/15): a new browser signs in (its first sign-in makes the account) and accepts the invite with
    /// <c>POST /api/v1/invites:accept</c>. Returns it and the accept's answer.
    /// </summary>
    protected async Task<(WebClient Browser, HttpResponseMessage Registered)> JoinAsync(string token, string? displayName = "Sam")
    {
        var browser = App.NewBrowser(TestIdentity.Google(displayName));
        return (browser, await browser.JoinAsync(token));
    }

    /// <summary>True when <paramref name="response"/> is the accept's "you're in": 200 with the library, and the browser is signed in.</summary>
    protected static void ShouldBeSignedIn(WebClient browser, HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        browser.HasSessionCookie.ShouldBeTrue();
    }

    /// <summary>The one answer for an invite that can't be used: 404 <c>invite.invalid</c>.</summary>
    protected static async Task ShouldBeInvalidInviteAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, body);
        body.ShouldContain("\"invite.invalid\"");
    }

    /// <summary>The owner invites someone with <paramref name="role"/> and they join. Returns their signed-in browser.</summary>
    protected async Task<(WebClient Browser, WebClient.SignedIn Member)> AddMemberAsync(
        WebClient owner, Guid libraryId, string role, string name = "Sam")
    {
        var (token, _) = await CreateInviteAsync(owner, libraryId, role, name);
        var (browser, registered) = await JoinAsync(token, name);
        ShouldBeSignedIn(browser, registered);
        return (browser, await browser.MeAsync());
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

    protected static string Sha256Hex(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    protected static async Task<string> BodyOfAsync(HttpResponseMessage response) => await response.Content.ReadAsStringAsync();
}
