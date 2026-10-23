using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CollectionMaxxing.Application.Accounts.IdentityProvider;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Infrastructure.Identity.Auth0;

/// <summary>
/// Auth0's Management API with client credentials (docs/14, "Devices and revocation"): <c>POST https://{Domain}/oauth/token</c> for a
/// machine-to-machine token, then <c>DELETE https://{Domain}/api/v2/...</c>. Two calls only, and the app's scopes are exactly
/// <c>delete:grants</c> and <c>delete:users</c> (the Free plan refuses the refresh-token endpoints):
/// <list type="bullet">
/// <item><c>DELETE /api/v2/grants?user_id=…</c> ends every refresh token of the identity;</item>
/// <item><c>DELETE /api/v2/users/{id}</c> deletes the identity.</item>
/// </list>
/// 204 and 404 both mean done. The token is cached until five minutes before it expires. Nothing here logs a token, a secret or a subject.
/// </summary>
internal sealed class Auth0IdentityProviderAdmin(
    IHttpClientFactory httpClients,
    IOptions<AuthOptions> options,
    TimeProvider time) : IIdentityProviderAdmin, IDisposable
{
    public void Dispose() => _tokenLock.Dispose();

    private static readonly TimeSpan RefreshBefore = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenValidUntil;

    public Task RevokeRefreshTokensAsync(string subject, CancellationToken cancellationToken) =>
        DeleteAsync($"/api/v2/grants?user_id={Uri.EscapeDataString(subject)}", cancellationToken);

    public Task DeleteUserAsync(string subject, CancellationToken cancellationToken) =>
        DeleteAsync($"/api/v2/users/{Uri.EscapeDataString(subject)}", cancellationToken);

    private async Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        // A 401 means the cached token was refused (revoked at Auth0, say): forget it and try once with a fresh one.
        for (var attempt = 0; ; attempt++)
        {
            var token = await TokenAsync(cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(BaseAddress() + path));
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var response = await SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                Forget(token);
                continue;
            }

            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
            {
                return;
            }

            throw new IdentityProviderException($"The Management API answered {(int)response.StatusCode}.");
        }
    }

    private async Task<string> TokenAsync(CancellationToken cancellationToken)
    {
        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (_token is not null && time.GetUtcNow() < _tokenValidUntil)
            {
                return _token;
            }

            var auth0 = options.Value.Auth0;
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseAddress() + "/oauth/token"))
            {
                Content = JsonContent.Create(new TokenRequest(auth0.ManagementClientId!, auth0.ManagementClientSecret!, BaseAddress() + "/api/v2/")),
            };
            using var response = await SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new IdentityProviderException($"Auth0 refused the Management API sign-in ({(int)response.StatusCode}).");
            }

            TokenResponse? body;
            try
            {
                body = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
            }
            catch (System.Text.Json.JsonException exception)
            {
                throw new IdentityProviderException("Auth0's Management API sign-in answered with something unreadable.", exception);
            }

            if (string.IsNullOrEmpty(body?.AccessToken))
            {
                throw new IdentityProviderException("Auth0's Management API sign-in gave no token.");
            }

            _token = body.AccessToken;
            _tokenValidUntil = time.GetUtcNow() + TimeSpan.FromSeconds(Math.Max(body.ExpiresIn, 0)) - RefreshBefore;
            return _token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private void Forget(string token)
    {
        _tokenLock.Wait();
        try
        {
            if (_token == token)
            {
                _token = null;
            }
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await httpClients.CreateClient(Auth0Management.HttpClientName).SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // The message of an HttpRequestException can carry the address; the type is enough to know what happened.
            throw new IdentityProviderException($"The Management API could not be reached ({exception.GetType().Name}).");
        }
    }

    private string BaseAddress() => "https://" + options.Value.Auth0.Domain!.Trim().TrimEnd('/');

    private sealed record TokenRequest(
        [property: JsonPropertyName("client_id")] string ClientId,
        [property: JsonPropertyName("client_secret")] string ClientSecret,
        [property: JsonPropertyName("audience")] string Audience)
    {
        [JsonPropertyName("grant_type")]
        public string GrantType { get; init; } = "client_credentials";
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}

/// <summary>Used while the Management API's credentials are not configured (Development, tests): nothing is asked of Auth0, and the log says so.</summary>
internal sealed class UnconfiguredIdentityProviderAdmin(ILogger<UnconfiguredIdentityProviderAdmin> logger) : IIdentityProviderAdmin
{
    public Task RevokeRefreshTokensAsync(string subject, CancellationToken cancellationToken) => Skip();

    public Task DeleteUserAsync(string subject, CancellationToken cancellationToken) => Skip();

    private Task Skip()
    {
        logger.LogWarning("Auth:Auth0:ManagementClientId/ManagementClientSecret are not set, so nothing was changed at Auth0.");
        return Task.CompletedTask;
    }
}
