using CollectionMaxxing.Application.Accounts.Devices;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;
using static OpenIddict.Server.OpenIddictServerHandlers.Revocation;

namespace CollectionMaxxing.Api.Auth.Pairing;

/// <summary>
/// <c>POST /connect/revoke</c> is OpenIddict's own endpoint. It revokes the one token it is given. The app calls it to unpair, and
/// "unpair" means the whole phone: the refresh token, the access token it made and every other token of that device. So after
/// OpenIddict has dealt with the token, this revokes the device's authorization (and with it every token) as well.
/// </summary>
public sealed class RevokeDeviceOnRevocation(IDeviceAuthorizations devices) : IOpenIddictServerHandler<HandleRevocationRequestContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
        OpenIddictServerHandlerDescriptor.CreateBuilder<HandleRevocationRequestContext>()
            .UseScopedHandler<RevokeDeviceOnRevocation>()
            .SetOrder(RevokeToken.Descriptor.Order + 1_000)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    public async ValueTask HandleAsync(HandleRevocationRequestContext context)
    {
        var principal = context.GenericTokenPrincipal;
        var authorizationId = principal?.GetAuthorizationId();
        var subject = principal?.GetClaim(Claims.Subject);
        if (Guid.TryParse(authorizationId, out var device) && Guid.TryParse(subject, out var user))
        {
            await devices.RevokeAsync(user, device, context.CancellationToken);
        }
    }
}
