using System.Globalization;
using System.Security.Claims;

namespace CollectionMaxxing.Api.Auth;

/// <summary>Who a signed-in caller is, read from the principal of either way in (the session cookie or an Auth0 bearer token).</summary>
public static class CurrentSession
{
    /// <summary>The claim naming the <c>user_logins</c> row behind the session: the identity the person signed in with.</summary>
    public const string LoginIdClaim = "cm_login";

    /// <summary>The <c>web_sessions</c> row the session cookie names, or null (a bearer caller, or a cookie that names none).</summary>
    public static Guid? WebSessionId(ClaimsPrincipal? user) =>
        Guid.TryParse(user?.FindFirstValue(AuthCookie.SessionIdClaim), CultureInfo.InvariantCulture, out var id) ? id : null;

    /// <summary>The identity behind this session, or null.</summary>
    public static Guid? LoginId(ClaimsPrincipal? user) =>
        Guid.TryParse(user?.FindFirstValue(LoginIdClaim), CultureInfo.InvariantCulture, out var id) ? id : null;

    /// <summary>
    /// Our user id of a signed-in caller, or null. Only ever the <c>NameIdentifier</c> our own code put there: the cookie's, or the one the
    /// bearer handler adds after finding the token's <c>sub</c> in <c>user_logins</c>. Auth0's <c>sub</c> itself is never a user id.
    /// </summary>
    public static Guid? UserId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), CultureInfo.InvariantCulture, out var id) ? id : null;
}
