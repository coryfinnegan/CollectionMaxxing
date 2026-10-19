using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace CollectionMaxxing.Api.Tests.Auth;

/// <summary>
/// A browser for tests: keeps cookies itself (the test host is plain HTTP, and a real <c>Secure</c> cookie would never be sent),
/// sends <c>X-CSRF: 1</c> like the web app does, and owns one software passkey.
/// </summary>
public sealed class WebClient(HttpClient http) : IDisposable
{
    public const string SessionCookie = "__Host-cm";

    private readonly Dictionary<string, string> _cookies = [];

    public HttpClient Http { get; } = http;

    public SoftwareAuthenticator Authenticator { get; set; } = new();

    /// <summary>Every Set-Cookie header the server has sent, newest last.</summary>
    public List<SetCookieHeaderValue> SetCookies { get; } = [];

    public bool HasSessionCookie => _cookies.ContainsKey(SessionCookie);

    /// <summary>The value of the session cookie the browser holds right now, as a thief who copied it would have it.</summary>
    public string? SessionCookieValue => _cookies.GetValueOrDefault(SessionCookie);

    /// <summary>Puts a session cookie in the jar, as if copied from another browser.</summary>
    public void UseSessionCookie(string value) => _cookies[SessionCookie] = value;

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
            if (string.IsNullOrEmpty(header.Value.Value) || header.Expires < DateTimeOffset.UtcNow.AddYears(-1))
            {
                _cookies.Remove(header.Name.Value!);
            }
            else
            {
                _cookies[header.Name.Value!] = header.Value.Value;
            }
        }
    }

    // ---- the ceremonies, as the web app performs them -------------------------------------------------------------

    /// <summary>First run: enter the setup code and a name. Returns the response carrying the passkey creation options.</summary>
    public Task<HttpResponseMessage> StartSetupAsync(string code, string displayName = "Owner") =>
        PostAsync("/setup", new { code, displayName });

    /// <summary>Recovery: enter the recovery code from the PC's command line.</summary>
    public Task<HttpResponseMessage> StartRecoveryAsync(string code) => PostAsync("/account/recover", new { code });

    /// <summary>Makes a passkey from the options in <paramref name="optionsResponse"/> and finishes the registration.</summary>
    public async Task<HttpResponseMessage> RegisterAsync(HttpResponseMessage optionsResponse, string? invite = null)
    {
        optionsResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await optionsResponse.Content.ReadAsStringAsync());
        var options = (await optionsResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("options");
        var credential = JsonDocument.Parse(Authenticator.Create(options.GetRawText())).RootElement.Clone();
        var url = "/account/passkey/register" + (invite is null ? string.Empty : "?invite=" + Uri.EscapeDataString(invite));
        return await PostAsync(url, new { credential, name = "Test passkey" });
    }

    /// <summary>Tries to register a passkey without ever having been given creation options (no code, token or ceremony).</summary>
    public async Task<HttpResponseMessage> RegisterUnpromptedAsync(string? invite = null)
    {
        var invented = JsonSerializer.Serialize(new
        {
            challenge = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)),
            user = new { id = WebEncoders.Base64UrlEncode(Guid.NewGuid().ToByteArray()) },
        });
        var credential = JsonDocument.Parse(Authenticator.Create(invented)).RootElement.Clone();
        var url = "/account/passkey/register" + (invite is null ? string.Empty : "?invite=" + Uri.EscapeDataString(invite));
        return await PostAsync(url, new { credential, name = "Sneaky passkey" });
    }

    /// <summary>Signs in with the passkey: asks for options, signs the challenge, posts the assertion.</summary>
    public async Task<HttpResponseMessage> LoginAsync()
    {
        var optionsResponse = await PostAsync("/account/passkey/request-options");
        optionsResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var options = (await optionsResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("options");
        var credential = JsonDocument.Parse(Authenticator.Sign(options.GetRawText())).RootElement.Clone();
        return await PostAsync("/account/login", new { credential });
    }

    /// <summary>Does the whole first run and returns who was created.</summary>
    public async Task<SignedIn> SetUpOwnerAsync(string code, string displayName = "Owner")
    {
        var response = await RegisterAsync(await StartSetupAsync(code, displayName));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<SignedIn>() ?? throw new InvalidOperationException("No body.");
    }

    public void Dispose() => Http.Dispose();

    /// <summary>The body of a successful sign-in.</summary>
    public sealed record SignedIn(Guid UserId, string DisplayName, Guid? LibraryId);
}
