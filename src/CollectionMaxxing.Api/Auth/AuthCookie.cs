using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;

namespace CollectionMaxxing.Api.Auth;

/// <summary>
/// The web session (docs/07, "Web: passkeys + same-origin cookie"): one <c>__Host-</c> cookie, so it's bound to the exact host,
/// has no Domain, is Secure-only and HttpOnly, and SameSite=Strict. 8 hours sliding, 30 days absolute.
/// </summary>
public static class AuthCookie
{
    public const string Name = "__Host-cm";

    /// <summary>The scheme of the session cookie (Identity's application scheme, so Identity's own sign-in code agrees with ours).</summary>
    public static readonly string Scheme = IdentityConstants.ApplicationScheme;

    /// <summary>The claim that says when the user last proved themselves with a passkey (OIDC's <c>auth_time</c>, seconds since 1970).</summary>
    public const string AuthTimeClaim = "auth_time";

    /// <summary>Where the ticket remembers when the session began, so renewals can't stretch it past 30 days.</summary>
    internal const string StartedItem = "cm.started";

    public static readonly TimeSpan IdleLifetime = TimeSpan.FromHours(8);
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromDays(30);

    public static void Configure(CookieAuthenticationOptions options)
    {
        options.Cookie.Name = Name;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always; // behind Tailscale Serve the hop to Kestrel is plain HTTP; the cookie is still Secure
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.Path = "/";
        options.Cookie.Domain = null;
        options.ExpireTimeSpan = IdleLifetime;
        options.SlidingExpiration = true;

        // An API answers with status codes, never redirects to a login page.
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
        options.Events.OnValidatePrincipal = RejectAfterAbsoluteLifetimeAsync;
    }

    /// <summary>Signs the user in with a fresh session: <c>auth_time</c> is now, and the 30-day clock starts now.</summary>
    public static async Task SignInAsync(HttpContext context, ClaimsPrincipal principal, TimeProvider time)
    {
        var now = time.GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var identity = (ClaimsIdentity)principal.Identity!;
        foreach (var stale in identity.FindAll(AuthTimeClaim).ToList())
        {
            identity.RemoveClaim(stale);
        }

        identity.AddClaim(new Claim(AuthTimeClaim, now, ClaimValueTypes.Integer64));

        var properties = new AuthenticationProperties { IsPersistent = true };
        properties.Items[StartedItem] = now;
        await context.SignInAsync(Scheme, principal, properties);
    }

    private static async Task RejectAfterAbsoluteLifetimeAsync(CookieValidatePrincipalContext context)
    {
        var time = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>();
        var started = context.Properties.Items.TryGetValue(StartedItem, out var text) && long.TryParse(text, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : (DateTimeOffset?)null;

        if (started is null || time.GetUtcNow() - started.Value > AbsoluteLifetime)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(Scheme);
        }
    }

    /// <summary>The user id of a signed-in caller (cookie or bearer token), or null.</summary>
    public static Guid? UserId(ClaimsPrincipal user) => Pairing.CurrentSession.UserId(user);
}
