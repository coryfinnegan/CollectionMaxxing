using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Net.Http.Headers;

namespace CollectionMaxxing.Api.Tests.Auth;

/// <summary>
/// A browser for tests: keeps cookies itself (the test host is plain HTTP, and a real <c>Secure</c> cookie would never be sent),
/// sends <c>X-CSRF: 1</c> like the web app does, never follows redirects, and signs in through the fake Auth0 as its
/// <see cref="Identity"/> the way a person would: <c>/account/login</c>, Auth0, back to <c>/signin-oidc</c>.
/// </summary>
public sealed class WebClient(HttpClient http, FakeAuth0 auth0, FakeTimeProvider time) : IDisposable
{
    public const string SessionCookie = "__Host-cm";

    private readonly Dictionary<string, string> _cookies = [];

    public HttpClient Http { get; } = http;

    /// <summary>Who this browser signs in as at Auth0.</summary>
    public TestIdentity Identity { get; set; } = TestIdentity.Google();

    /// <summary>Every Set-Cookie header the server has sent, newest last.</summary>
    public List<SetCookieHeaderValue> SetCookies { get; } = [];

    /// <summary>The Auth0 address of the last sign-in this browser started.</summary>
    public Uri? LastChallenge { get; private set; }

    public bool HasSessionCookie => _cookies.ContainsKey(SessionCookie);

    /// <summary>The value of the session cookie the browser holds right now, as a thief who copied it would have it.</summary>
    public string? SessionCookieValue => _cookies.GetValueOrDefault(SessionCookie);

    /// <summary>Puts a session cookie in the jar, as if copied from another browser.</summary>
    public void UseSessionCookie(string value) => _cookies[SessionCookie] = value;

    /// <summary>The value of any cookie the browser holds.</summary>
    public string? Cookie(string name) => _cookies.GetValueOrDefault(name);

    public void UseCookie(string name, string value) => _cookies[name] = value;

    public void ForgetCookie(string name) => _cookies.Remove(name);

    public Task<HttpResponseMessage> GetAsync(string url) => SendAsync(HttpMethod.Get, url, null);

    public Task<HttpResponseMessage> PostAsync(string url, object? body = null, bool csrf = true) => SendAsync(HttpMethod.Post, url, body, csrf);

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body, bool csrf = true, Action<HttpRequestMessage>? tweak = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        if (csrf)
        {
            request.Headers.Add("X-CSRF", "1");
        }

        if (_cookies.Count > 0)
        {
            request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(c => $"{c.Key}={c.Value}")));
        }

        tweak?.Invoke(request);
        var response = await Http.SendAsync(request);
        Remember(response);
        return response;
    }

    /// <summary>Forgets the cookies, as if the browser had been closed and cleared.</summary>
    public void ClearCookies() => _cookies.Clear();

    private void Remember(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var headers))
        {
            return;
        }

        foreach (var header in SetCookieHeaderValue.ParseList(headers.ToList()))
        {
            SetCookies.Add(header);
            if (string.IsNullOrEmpty(header.Value.Value) || header.Expires < DateTimeOffset.UtcNow.AddYears(-1) || header.MaxAge <= TimeSpan.Zero)
            {
                _cookies.Remove(header.Name.Value!);
            }
            else
            {
                _cookies[header.Name.Value!] = header.Value.Value;
            }
        }
    }

    // ---- signing in, as the web app does it --------------------------------------------------------------------------

    /// <summary>
    /// Navigates to <paramref name="loginUrl"/>; if that sends the browser to Auth0, signs in there as <paramref name="who"/> (this
    /// browser's <see cref="Identity"/> by default) and follows Auth0's redirect back to <c>/signin-oidc</c>. Returns the last answer:
    /// the callback's 302, or whatever <paramref name="loginUrl"/> said if it didn't go to Auth0.
    /// </summary>
    /// <param name="loginUrl">Where the sign-in starts.</param>
    /// <param name="who">The Auth0 identity.</param>
    /// <param name="authTime">The ID token's <c>auth_time</c>: now on the app's clock unless given.</param>
    /// <param name="omitAuthTime">Leave <c>auth_time</c> out of the ID token.</param>
    /// <param name="tweak">Changes the ID token's claims.</param>
    public async Task<HttpResponseMessage> SignInAsync(
        string loginUrl = "/account/login",
        TestIdentity? who = null,
        DateTimeOffset? authTime = null,
        bool omitAuthTime = false,
        Action<Dictionary<string, object>>? tweak = null)
    {
        return await FinishAsync(await GetAsync(loginUrl), who, authTime, omitAuthTime, tweak);
    }

    /// <summary>
    /// Finishes a sign-in that <paramref name="started"/> began: if it sent the browser to Auth0, signs in there and comes back to
    /// <c>/signin-oidc</c>. Otherwise returns <paramref name="started"/> itself.
    /// </summary>
    public async Task<HttpResponseMessage> FinishAsync(
        HttpResponseMessage started,
        TestIdentity? who = null,
        DateTimeOffset? authTime = null,
        bool omitAuthTime = false,
        Action<Dictionary<string, object>>? tweak = null)
    {
        if (Challenge(started) is not { } query)
        {
            return started;
        }

        var code = Guid.NewGuid().ToString("N");
        auth0.ExpectCode(code, query["nonce"]!, who ?? Identity, omitAuthTime ? null : authTime ?? time.GetUtcNow(), tweak);
        return await GetAsync($"/signin-oidc?code={code}&state={Uri.EscapeDataString(query["state"]!)}");
    }

    /// <summary>When <paramref name="response"/> sends the browser to Auth0's authorize page, its query; otherwise null.</summary>
    public Dictionary<string, string?>? Challenge(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.Found || response.Headers.Location is not { } location
            || !location.ToString().StartsWith(FakeAuth0.Issuer + "authorize", StringComparison.Ordinal))
        {
            return null;
        }

        LastChallenge = location;
        return QueryHelpers.ParseQuery(location.Query).ToDictionary(p => p.Key, p => (string?)p.Value.ToString());
    }

    /// <summary>Signs in with this browser's identity (it must be linked already).</summary>
    public Task<HttpResponseMessage> LoginAsync() => SignInAsync();

    /// <summary>First run: enter the setup code.</summary>
    public Task<HttpResponseMessage> StartSetupAsync(string code) => PostAsync("/setup", new { code });

    /// <summary>Recovery: enter the recovery code from the PC's command line.</summary>
    public Task<HttpResponseMessage> StartRecoveryAsync(string code) => PostAsync("/account/recover", new { code });

    /// <summary>Follows the <c>next</c> of a <c>POST /setup</c> or <c>POST /account/recover</c> answer through Auth0.</summary>
    public async Task<HttpResponseMessage> ContinueAsync(HttpResponseMessage started, TestIdentity? who = null)
    {
        started.StatusCode.ShouldBe(HttpStatusCode.OK, await started.Content.ReadAsStringAsync());
        var next = (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("next").GetString()!;
        return await SignInAsync(next, who);
    }

    /// <summary>Joins by invite: <c>/account/login?invite=…</c> through Auth0.</summary>
    public Task<HttpResponseMessage> JoinAsync(string token, TestIdentity? who = null) =>
        SignInAsync("/account/login?invite=" + Uri.EscapeDataString(token), who);

    /// <summary>Signs in again (<c>reauth=1</c>), so the session counts as recent.</summary>
    public Task<HttpResponseMessage> ReauthAsync(DateTimeOffset? authTime = null) => SignInAsync("/account/login?reauth=1", authTime: authTime);

    /// <summary>Adds <paramref name="other"/> as a second sign-in method (<c>link=1</c>).</summary>
    public Task<HttpResponseMessage> LinkAsync(TestIdentity other) => SignInAsync("/account/login?link=1", other);

    /// <summary>Does the whole first run and returns who was created.</summary>
    public async Task<SignedIn> SetUpOwnerAsync(string code)
    {
        var done = await ContinueAsync(await StartSetupAsync(code));
        done.StatusCode.ShouldBe(HttpStatusCode.Found);
        done.Headers.Location?.ToString().ShouldBe("/");
        return await MeAsync();
    }

    /// <summary>Who this browser is signed in as, from <c>GET /api/v1/me</c>.</summary>
    public async Task<SignedIn> MeAsync()
    {
        var response = await GetAsync("/api/v1/me");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var me = await response.Content.ReadFromJsonAsync<JsonElement>();
        var memberships = me.GetProperty("memberships").EnumerateArray().ToList();
        Guid? library = memberships.Count == 0 ? null : memberships[0].GetProperty("libraryId").GetGuid();
        return new SignedIn(me.GetProperty("id").GetGuid(), library);
    }

    public void Dispose() => Http.Dispose();

    /// <summary>Who a browser is signed in as.</summary>
    public sealed record SignedIn(Guid UserId, Guid? LibraryId);
}
