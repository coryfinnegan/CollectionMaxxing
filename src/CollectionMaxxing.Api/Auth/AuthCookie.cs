using System.Globalization;
using System.Security.Claims;
using CollectionMaxxing.Application.Accounts.Devices;
using CollectionMaxxing.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;

namespace CollectionMaxxing.Api.Auth;

/// <summary>
/// The web session (docs/14, "Web: Auth0 signs you in, our cookie keeps you in"): one <c>__Host-</c> cookie, so it's bound to the exact
/// host, has no Domain, is Secure-only and HttpOnly, and SameSite=Strict. 30 days sliding, one year absolute (D-2). Only our own code
/// issues it, after the Auth0 callback has been checked (<see cref="SignInAsync"/>); the OpenID Connect handler never does.
/// </summary>
public static class AuthCookie
{
    public const string Name = "__Host-cm";

    /// <summary>The scheme of the session cookie (Identity's application scheme, so Identity's own sign-in code agrees with ours).</summary>
    public static readonly string Scheme = IdentityConstants.ApplicationScheme;

    /// <summary>The claim that says when the user last signed in at Auth0 (the ID token's <c>auth_time</c>, seconds since 1970).</summary>
    public const string AuthTimeClaim = "auth_time";

    /// <summary>
    /// The claim that names this browser's <c>web_sessions</c> row. A cookie without it (one issued before web sessions existed) is refused,
    /// and so is one whose row is revoked, missing or another user's.
    /// </summary>
    public const string SessionIdClaim = "cm_sid";

    /// <summary>Where the ticket remembers when the session began, so renewals can't stretch it past a year.</summary>
    internal const string StartedItem = "cm.started";

    /// <summary>A ticket this old is re-issued on its next use, so the idle window is measured from (about) the last visit.</summary>
    public static readonly TimeSpan RenewAfter = TimeSpan.FromDays(1);

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
    /// Signs the user in with a fresh session: the cookie carries the user, the identity they used (<paramref name="loginId"/>) and when
    /// they signed in at Auth0 (<paramref name="authTime"/>, if Auth0 said), the absolute clock starts now, and the browser gets a new
    /// <c>web_sessions</c> row. Sessions this browser already held (it signed in again without signing out) are revoked.
    /// </summary>
    public static async Task SignInAsync(HttpContext context, Guid userId, Guid loginId, DateTimeOffset? authTime, IEnumerable<(Guid UserId, Guid SessionId)> replaces)
    {
        var services = context.RequestServices;
        var sessions = services.GetRequiredService<IWebSessions>();
        var previous = replaces.ToList();
        if (CurrentSession.WebSessionId(context.User) is { } current && UserId(context.User) is { } currentUser)
        {
            previous.Add((currentUser, current));
        }

        foreach (var (owner, oldSession) in previous.Distinct())
        {
            await sessions.RevokeAsync(owner, oldSession, context.RequestAborted);
        }

        var user = await services.GetRequiredService<UserManager<AppUser>>().FindByIdAsync(userId.ToString())
            ?? throw new InvalidOperationException("The user signing in is gone.");
        var principal = await services.GetRequiredService<IUserClaimsPrincipalFactory<AppUser>>().CreateAsync(user);
        var identity = (ClaimsIdentity)principal.Identity!;
        var time = services.GetRequiredService<TimeProvider>();
        var now = time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        var sessionId = await sessions.StartAsync(userId, BrowserLabel.From(context.Request.Headers.UserAgent.ToString()), context.RequestAborted);
        if (authTime is { } signedInAt)
        {
            identity.AddClaim(new Claim(AuthTimeClaim, signedInAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64));
        }

        identity.AddClaim(new Claim(SessionIdClaim, sessionId.ToString()));
        identity.AddClaim(new Claim(CurrentSession.LoginIdClaim, loginId.ToString()));

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
    /// Runs on every request that carries the cookie. The ticket must be inside its year, and must name a session of this user that has
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
        else if (context.Properties.IssuedUtc is not { } issued || time.GetUtcNow() - issued >= RenewAfter)
        {
            // A true 30-day idle window (D-2): ASP.NET's sliding expiration renews only once half the lifetime has passed, which would make the
            // real limit 15 to 30 days. Renewing once the ticket is about a day old keeps it at 30 days after the last use. The absolute
            // year is untouched: it is measured from StartedItem, which a renewal keeps.
            context.ShouldRenew = true;
        }
    }

    /// <summary>The user id of a signed-in caller (cookie or bearer token), or null.</summary>
    public static Guid? UserId(ClaimsPrincipal user) => CurrentSession.UserId(user);
}
