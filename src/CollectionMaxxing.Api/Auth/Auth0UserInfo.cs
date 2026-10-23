using System.Net.Http.Headers;
using System.Text.Json;
using CollectionMaxxing.Application.Accounts;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Api.Auth;

/// <summary>
/// Asks Auth0's <c>/userinfo</c> who the caller of a first bearer call is (docs/15, "How the account is created"). Called once per new
/// account, with the caller's own access token, so the name and email come from Auth0 and never from the app's own word. The address is
/// the tenant's <c>userinfo_endpoint</c>, from the discovery document the bearer handler already trusts.
/// </summary>
public sealed partial class Auth0UserInfo(
    IHttpClientFactory clients,
    IOptionsMonitor<JwtBearerOptions> bearer,
    ILogger<Auth0UserInfo> logger)
{
    public const string HttpClientName = "auth0-userinfo";

    /// <summary>
    /// What Auth0 says about the token's owner, or null when it can't be asked (down, refused, or it answered about somebody else). The
    /// caller then creates nothing, and the next call tries again.
    /// </summary>
    public async Task<ExternalIdentity?> GetAsync(string subject, string accessToken, CancellationToken cancellationToken)
    {
        try
        {
            var options = bearer.Get(Auth0Setup.BearerScheme);
            var configuration = options.ConfigurationManager is { } manager
                ? await manager.GetConfigurationAsync(cancellationToken)
                : options.Configuration;
            if (configuration?.UserInfoEndpoint is not { Length: > 0 } endpoint)
            {
                LogUnavailable("no userinfo_endpoint in the discovery document");
                return null;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await clients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                LogUnavailable($"status {(int)response.StatusCode}");
                return null;
            }

            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var root = document.RootElement;

            // The answer must be about the token's own sub (OpenID Connect Core 5.3.2), or it is not believed at all.
            if (root.ValueKind != JsonValueKind.Object || Text(root, "sub") != subject)
            {
                LogUnavailable("the answer was about another subject");
                return null;
            }

            return new ExternalIdentity(subject, Text(root, "email"), Text(root, "name"), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException
            && !cancellationToken.IsCancellationRequested)
        {
            LogUnavailable(ex.GetType().Name);
            return null;
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Auth0's userinfo couldn't be asked about a new account ({Reason}); nothing was created.")]
    private partial void LogUnavailable(string reason);
}
