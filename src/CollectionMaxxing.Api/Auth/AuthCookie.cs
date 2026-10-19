using System.Security.Claims;
using CollectionMaxxing.Api.Auth.Pairing;
using CollectionMaxxing.Application.Accounts.Devices;
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

    /// <summary>
    /// The claim that names this browser's <c>web_sessions</c> row. A cookie without it (one issued before web sessions existed) is refused,
    /// and so is one whose row is revoked, missing or another user's.
    /// </summary>
    public const string SessionIdClaim = "cm_sid";

    /// <summary>Where the ticket remembers when the session began, so renewals can't stretch it past 30 days.</summary>
    internal const string StartedItem = "cm.started";

    public static readonly TimeSpan IdleLifetime = IWebSessions.IdleLifetime;
    public static readonly TimeSpan AbsoluteLifetime = IWebSessions.AbsoluteLifetime;

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
        options.Events.OnValidatePrincipal = ValidateAsync;
    }

    /// <summary>
    /// Signs the user in with a fresh session: <c>auth_time</c> is now, the 30-day clock starts now, and the browser gets a new
    /// <c>web_sessions</c> row whose id goes into the cookie. A session this browser already held (it signed in again without signing out) is revoked.
    /// </summary>
    public static async Task SignInAsync(HttpContext context, ClaimsPrincipal principal, TimeProvider time)
    {
        var userId = UserId(principal) ?? throw new InvalidOperationException("The user signing in has no id.");
        var sessions = context.RequestServices.GetRequiredService<IWebSessions>();
        if (CurrentSession.WebSessionId(context.User) is { } previous && UserId(context.User) is { } previousUser)
        {
            await sessions.RevokeAsync(previousUser, previous, context.RequestAborted);
        }

        var now = time.GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var identity = (ClaimsIdentity)principal.Identity!;
        foreach (var stale in identity.FindAll(AuthTimeClaim).Concat(identity.FindAll(SessionIdClaim)).ToList())
        {
            identity.RemoveClaim(stale);
        }

        var sessionId = await sessions.StartAsync(userId, BrowserLabel.From(context.Request.Headers.UserAgent.ToString()), context.RequestAborted);
        identity.AddClaim(new Claim(AuthTimeClaim, now, ClaimValueTypes.Integer64));
        identity.AddClaim(new Claim(SessionIdClaim, sessionId.ToString()));

        var properties = new AuthenticationProperties { IsPersistent = true };
        properties.Items[StartedItem] = now;
        await context.SignInAsync(Scheme, principal, properties);
    }

    /// <summary>Signs this browser out: its session row is revoked (so a copy of the cookie is dead too) and the cookie is forgotten.</summary>
    public static async Task SignOutAsync(HttpContext context)
    {
        if (CurrentSession.WebSessionId(context.User) is { } sessionId && UserId(context.User) is { } userId)
        {
            await context.RequestServices.GetRequiredService<IWebSessions>().RevokeAsync(userId, sessionId, context.RequestAborted);
        }

        await context.SignOutAsync(Scheme);
    }

    /// <summary>
    /// Runs on every request that carries the cookie. The ticket must be inside its 30 days, and must name a session of this user that has
    /// not been revoked. A cookie from before web sessions existed names none, so it is refused; the person signs in again.
    /// </summary>
    private static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var time = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>();
        var started = context.Properties.Items.TryGetValue(StartedItem, out var text) && long.TryParse(text, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : (DateTimeOffset?)null;

        var alive = started is not null && time.GetUtcNow() - started.Value <= AbsoluteLifetime;
        if (alive)
        {
            try
            {
                alive = CurrentSession.WebSessionId(context.Principal) is { } sessionId
                    && UserId(context.Principal!) is { } userId
                    && await context.HttpContext.RequestServices.GetRequiredService<IWebSessions>()
                        .TouchAsync(sessionId, userId, context.HttpContext.RequestAborted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The database is down, which says nothing about this session: keep the cookie (do not reject or sign out) and
                // fail this request closed with a 503 (see SessionStoreUnavailableExceptionHandler).
                throw new SessionStoreUnavailableException(ex);
            }
        }

        if (!alive)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(Scheme);
        }
    }

    /// <summary>The user id of a signed-in caller (cookie or bearer token), or null.</summary>
    public static Guid? UserId(ClaimsPrincipal user) => Pairing.CurrentSession.UserId(user);
}
