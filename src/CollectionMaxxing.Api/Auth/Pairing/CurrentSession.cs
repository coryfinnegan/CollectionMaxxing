using System.Globalization;
using System.Security.Claims;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace CollectionMaxxing.Api.Auth.Pairing;

/// <summary>Who a caller is when they come with a bearer token (the iPhone) rather than the session cookie.</summary>
public static class CurrentSession
{
    /// <summary>
    /// The paired device the request came from: the id of the authorization its access token hangs from. Null for the cookie, whose
    /// session is <see cref="WebSessionId"/>.
    /// </summary>
    public static Guid? Id(ClaimsPrincipal user) =>
        Guid.TryParse(user.GetAuthorizationId(), CultureInfo.InvariantCulture, out var id) ? id : null;

    /// <summary>The <c>web_sessions</c> row the session cookie names, or null (a bearer caller, or a cookie that names none).</summary>
    public static Guid? WebSessionId(ClaimsPrincipal? user) =>
        Guid.TryParse(user?.FindFirstValue(AuthCookie.SessionIdClaim), CultureInfo.InvariantCulture, out var id) ? id : null;

    /// <summary>
    /// The user id of a signed-in caller: the <c>NameIdentifier</c> of the cookie, or the <c>sub</c> of an access token (OpenIddict
    /// keeps the short claim names), or null.
    /// </summary>
    public static Guid? UserId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue(Claims.Subject), CultureInfo.InvariantCulture, out var id)
            ? id
            : null;
}
