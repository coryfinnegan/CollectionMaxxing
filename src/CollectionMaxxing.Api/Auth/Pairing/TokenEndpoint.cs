using System.Globalization;
using System.Security.Claims;
using CollectionMaxxing.Application.Accounts.Devices;
using CollectionMaxxing.Domain.Common;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace CollectionMaxxing.Api.Auth.Pairing;

/// <summary>
/// <c>POST /connect/token</c> for the iPhone (docs/07, "iPhone: pair once with a QR code"). OpenIddict has already read and
/// validated the request, and, for a refresh token, has checked it against the database (valid, unused, in date, its device not
/// revoked; a replayed one revokes the whole chain). What is left is ours: the pairing grant, and the user check on refresh. Signing
/// in a principal makes OpenIddict issue the tokens, rotate the refresh token and slide its 90 days.
/// </summary>
public static class TokenEndpoint
{
    private const string Scheme = OpenIddictServerAspNetCoreDefaults.AuthenticationScheme;

    public static async Task<IResult> HandleAsync(
        HttpContext http,
        RedeemPairing.Handler redeem,
        IDeviceAuthorizations devices,
        IOpenIddictTokenManager tokens,
        IOpenIddictAuthorizationManager authorizations,
        PairingAttemptLimiter limiter,
        ClientAddressResolver clients,
        CancellationToken cancellationToken)
    {
        var request = http.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenIddict request can't be retrieved: is the token endpoint passthrough enabled?");

        if (request.GrantType == OpenIddictSetup.DevicePairingGrant)
        {
            return await PairAsync(http, request, redeem, limiter, clients, cancellationToken);
        }

        if (request.IsRefreshTokenGrantType())
        {
            return await RefreshAsync(http, devices, tokens, authorizations, cancellationToken);
        }

        return Refuse(Errors.UnsupportedGrantType, "That grant type isn't supported.");
    }

    private static async Task<IResult> PairAsync(
        HttpContext http,
        OpenIddictRequest request,
        RedeemPairing.Handler redeem,
        PairingAttemptLimiter limiter,
        ClientAddressResolver clients,
        CancellationToken cancellationToken)
    {
        var caller = clients.Resolve(http);
        if (!limiter.TryAcquire(caller, out var retryAfter))
        {
            http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            return AuthProblems.TooManyAttempts();
        }

        // Five wrong codes lock this caller out, whatever it sends next. The pairing itself is untouched (see PairingAttemptLimiter).
        if (limiter.IsLockedOut(caller))
        {
            return RefuseForPairing(PairingErrors.Invalid);
        }

        var result = await redeem.HandleAsync(
            new RedeemPairing.Command(
                request.GetParameter("pairing_token")?.ToString(),
                request.GetParameter("pairing_code")?.ToString(),
                request.GetParameter("device_name")?.ToString()),
            cancellationToken);
        if (!result.IsSuccess)
        {
            if (!string.IsNullOrWhiteSpace(request.GetParameter("pairing_code")?.ToString()))
            {
                limiter.RecordWrongCode(caller);
            }

            return RefuseForPairing(result.Error!);
        }

        var identity = new ClaimsIdentity(Scheme, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, result.Value.UserId.ToString(null, CultureInfo.InvariantCulture));
        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(OpenIddictSetup.ApiScope, Scopes.OfflineAccess);
        principal.SetAuthorizationId(result.Value.AuthorizationId.ToString(null, CultureInfo.InvariantCulture));
        return SignIn(principal);
    }

    private static async Task<IResult> RefreshAsync(
        HttpContext http,
        IDeviceAuthorizations devices,
        IOpenIddictTokenManager tokens,
        IOpenIddictAuthorizationManager authorizations,
        CancellationToken cancellationToken)
    {
        // The principal OpenIddict rebuilt from the refresh token it just validated.
        var principal = (await http.AuthenticateAsync(Scheme)).Principal;
        if (principal is null || !Guid.TryParse(principal.GetClaim(Claims.Subject), out var userId))
        {
            return Refuse(Errors.InvalidGrant, "The refresh token is no longer valid.");
        }

        // A deleted user's phone must not keep working just because its refresh token has not expired.
        if (!await devices.UserIsActiveAsync(userId, cancellationToken))
        {
            return Refuse(Errors.InvalidGrant, "The refresh token is no longer valid.");
        }

        // A refresh token works once, even when two requests bring it at the same moment (see ClaimRefreshTokenAsync).
        if (!await ClaimRefreshTokenAsync(principal, tokens, authorizations, cancellationToken))
        {
            return Refuse(Errors.InvalidGrant, "The refresh token is no longer valid.");
        }

        return SignIn(principal);
    }

    /// <summary>
    /// Spends the refresh token, atomically, before new tokens are issued. OpenIddict marks the token as redeemed only after the answer
    /// is built and, for a refresh grant, deliberately ignores a failure to do so ("that may be caused by concurrent requests"). So
    /// by itself two requests that arrive together both pass its checks and both get a new pair: a stolen token and the phone's own
    /// copy would each hold a live chain, and no later replay of an old token would tell them apart. Redeeming it here, where a
    /// second claim fails, means exactly one request wins; the others used a spent token, which is a replay, so the device's whole
    /// chain is revoked (docs/07, "Refresh rotation").
    /// </summary>
    private static async Task<bool> ClaimRefreshTokenAsync(
        ClaimsPrincipal principal,
        IOpenIddictTokenManager tokens,
        IOpenIddictAuthorizationManager authorizations,
        CancellationToken cancellationToken)
    {
        var token = principal.GetTokenId() is { } tokenId ? await tokens.FindByIdAsync(tokenId, cancellationToken) : null;
        if (token is not null && await tokens.TryRedeemAsync(token, cancellationToken))
        {
            return true;
        }

        if (principal.GetAuthorizationId() is { } authorizationId)
        {
            if (await authorizations.FindByIdAsync(authorizationId, cancellationToken) is { } authorization)
            {
                await authorizations.TryRevokeAsync(authorization, cancellationToken);
            }

            await tokens.RevokeByAuthorizationIdAsync(authorizationId, cancellationToken);
        }

        return false;
    }

    private static IResult SignIn(ClaimsPrincipal principal)
    {
        // The access token is for this API and no other (the validation side accepts only tokens that name it).
        principal.SetResources(OpenIddictSetup.ApiAudience);

        // Everything we know about the phone goes into the access token (and, as always, the refresh token carries it on).
        principal.SetDestinations(_ => [Destinations.AccessToken]);
        return Results.SignIn(principal, authenticationScheme: Scheme);
    }

    private static IResult RefuseForPairing(DomainError error) =>
        error == PairingErrors.Missing
            ? Refuse(Errors.InvalidRequest, error.Message)
            : Refuse(Errors.InvalidGrant, error.Message);

    /// <summary>An OAuth error: 400 with <c>{ "error": …, "error_description": … }</c>, which is what a token client expects.</summary>
    private static IResult Refuse(string error, string description) =>
        Results.Forbid(
            new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
                [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
            }),
            [Scheme]);
}
