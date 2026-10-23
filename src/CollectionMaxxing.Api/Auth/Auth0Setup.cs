using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using CollectionMaxxing.Application.Accounts.Devices;
using CollectionMaxxing.Application.Accounts.IdentityProvider;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace CollectionMaxxing.Api.Auth;

/// <summary>
/// Auth0 as the way in (ADR-025, docs/14 "Target design"), with Microsoft's own handlers and no Auth0 SDK:
/// <list type="bullet">
/// <item><b>JwtBearer</b> checks the iPhone's access tokens: issuer <c>https://{Domain}/</c>, our audience, RS256 only. The token's
/// <c>sub</c> is then looked up in <c>user_logins</c>; an unlinked one gets 403 <c>account.not_linked</c> (see <see cref="CurrentUserMiddleware"/>).</item>
/// <item><b>OpenID Connect</b> runs the web's code flow (with PKCE) for challenges only. It never signs anyone in: its sign-in scheme
/// refuses, and <see cref="OidcCallback"/> decides after the callback and calls <see cref="AuthCookie.SignInAsync"/> itself.</item>
/// </list>
/// </summary>
public static class Auth0Setup
{
    public const string BearerScheme = JwtBearerDefaults.AuthenticationScheme;
    public const string OidcScheme = OpenIdConnectDefaults.AuthenticationScheme;

    /// <summary>The scheme the OpenID Connect handler would sign in with. It throws: only our callback code issues a session.</summary>
    public const string NoSignInScheme = "cm-no-sign-in";

    /// <summary>Where Auth0 sends the browser back to (on <c>Auth:PublicOrigin</c>).</summary>
    public const string CallbackPath = "/signin-oidc";

    /// <summary>Auth0 tokens and ours may disagree on the time by this much.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    /// <summary>Used in Development (and tests) when Auth0 isn't configured, so the app still starts; signing in then fails.</summary>
    private const string UnconfiguredDomain = "auth0.invalid";

    public static AuthenticationBuilder AddAuth0(this AuthenticationBuilder authentication)
    {
        var services = authentication.Services;
        services.AddOptions<AuthOptions>().ValidateOnStart(); // bound by AddIdentityAccounts
        services.AddSingleton<IValidateOptions<AuthOptions>, Auth0SettingsValidator>();

        authentication
            .AddJwtBearer(BearerScheme, _ => { })
            .AddOpenIdConnect(OidcScheme, _ => { })
            .AddScheme<AuthenticationSchemeOptions, RefuseSignInHandler>(NoSignInScheme, _ => { });

        services.AddOptions<JwtBearerOptions>(BearerScheme).Configure<IOptions<AuthOptions>>((options, auth) =>
        {
            var auth0 = auth.Value.Auth0;
            options.Authority = Issuer(auth.Value);
            options.Audience = auth0.Audience;
            options.MapInboundClaims = false; // keep "sub" as "sub": it is Auth0's, never our user id
            options.TokenValidationParameters.ValidAlgorithms = [SecurityAlgorithms.RsaSha256];
            options.TokenValidationParameters.ValidIssuer = Issuer(auth.Value);
            options.TokenValidationParameters.ValidAudience = auth0.Audience;
            options.TokenValidationParameters.ClockSkew = ClockSkew;
            options.TokenValidationParameters.RequireExpirationTime = true;
            options.TokenValidationParameters.RequireSignedTokens = true;
            options.Events = new JwtBearerEvents { OnTokenValidated = LinkBearerAsync };
        });

        services.AddOptions<OpenIdConnectOptions>(OidcScheme).Configure<IOptions<AuthOptions>, IPublicServerUrl>((options, auth, server) =>
        {
            var auth0 = auth.Value.Auth0;
            options.Authority = Issuer(auth.Value);
            options.ClientId = string.IsNullOrWhiteSpace(auth0.WebClientId) ? "unconfigured" : auth0.WebClientId;
            options.ClientSecret = auth0.WebClientSecret;
            options.ResponseType = OpenIdConnectResponseType.Code;
            options.ResponseMode = OpenIdConnectResponseMode.Query; // a top-level GET back, so the SameSite=Lax intent cookie comes with it
            options.UsePkce = true;
            options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
            options.Scope.Clear();
            options.Scope.Add("openid");
            options.Scope.Add("profile");
            options.Scope.Add("email");
            options.SaveTokens = false; // the ID token is checked and thrown away; the browser never holds a token
            options.GetClaimsFromUserInfoEndpoint = false;
            options.MapInboundClaims = false;
            options.CallbackPath = CallbackPath;
            options.SignInScheme = NoSignInScheme;
            options.SignOutScheme = NoSignInScheme;
            options.TokenValidationParameters.ValidAlgorithms = [SecurityAlgorithms.RsaSha256];
            options.TokenValidationParameters.ClockSkew = ClockSkew;
            options.Events = new OpenIdConnectEvents
            {
                // The callback address comes from configuration, never from the Host header of the request.
                OnRedirectToIdentityProvider = context =>
                {
                    context.ProtocolMessage.RedirectUri = server.Value + CallbackPath;
                    return Task.CompletedTask;
                },
                OnTicketReceived = OidcCallback.HandleAsync,
                OnAccessDenied = OidcCallback.FailAsync,
                OnRemoteFailure = OidcCallback.FailAsync,
            };
        });

        // The handlers' own logs can print tokens and whole requests at Debug; nothing below Warning is worth that risk (docs/08).
        services.AddLogging(logging => logging
            .AddFilter("Microsoft.AspNetCore.Authentication.OpenIdConnect", LogLevel.Warning)
            .AddFilter("Microsoft.AspNetCore.Authentication.JwtBearer", LogLevel.Warning)
            .AddFilter("Microsoft.IdentityModel", LogLevel.Warning));
        return authentication;
    }

    /// <summary><c>https://{Domain}/</c>, exactly as Auth0 writes it in <c>iss</c>.</summary>
    public static string Issuer(AuthOptions auth)
    {
        var domain = auth.Auth0.Domain?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(domain))
        {
            domain = UnconfiguredDomain;
        }

        return $"https://{domain}/";
    }

    /// <summary>
    /// After the token checks out: find who its <c>sub</c> is linked to, and build the principal from scratch, so nothing in the token
    /// but <c>sub</c> can say who the caller is. An unlinked <c>sub</c> keeps only <c>sub</c>; the next middleware answers 403. A phone
    /// carries no platform role: the admin routes are for the web session (as they were with paired phones).
    /// </summary>
    private static async Task LinkBearerAsync(Microsoft.AspNetCore.Authentication.JwtBearer.TokenValidatedContext context)
    {
        var subject = context.Principal?.FindFirstValue("sub");
        if (string.IsNullOrEmpty(subject))
        {
            // Validly signed, with the right issuer and audience, but about nobody: not a sign-in at all.
            context.Fail("The token has no sub.");
            return;
        }

        var services = context.HttpContext.RequestServices;
        var claims = new List<Claim> { new("sub", subject) };
        var login = await services.GetRequiredService<IAppDbContext>().Logins.AsNoTracking()
            .Where(l => l.Subject == subject)
            .Select(l => new { l.Id, l.UserId })
            .FirstOrDefaultAsync(context.HttpContext.RequestAborted);
        if (login is not null)
        {
            // Signing the phones out, removing a member and deleting an account set a cutoff: a token issued before it is refused at once,
            // however much of its hour is left. A token issued in the same second as the cutoff, or later, is a new sign-in and works.
            var issuedAt = long.TryParse(context.Principal!.FindFirstValue("iat"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                ? seconds
                : (long?)null;
            var cutoff = await services.GetRequiredService<ITokenCutoffs>().GetAsync(login.UserId, context.HttpContext.RequestAborted);
            if (TokenCutoff.Refuses(cutoff, issuedAt))
            {
                context.Fail("The token was issued before this person was signed out.");
                return;
            }

            claims.Add(new Claim(ClaimTypes.NameIdentifier, login.UserId.ToString()));
            claims.Add(new Claim(CurrentSession.LoginIdClaim, login.Id.ToString()));
        }

        context.Principal = new ClaimsPrincipal(new ClaimsIdentity(claims, BearerScheme, ClaimTypes.Name, ClaimTypes.Role));
    }

    /// <summary>Outside Development the API won't start without the Auth0 settings and its public origin (issue #226).</summary>
    private sealed class Auth0SettingsValidator(IHostEnvironment environment) : IValidateOptions<AuthOptions>
    {
        public ValidateOptionsResult Validate(string? name, AuthOptions options)
        {
            if (environment.IsDevelopment())
            {
                return ValidateOptionsResult.Success;
            }

            var missing = new List<string>();
            Require(options.Auth0.Domain, "Auth:Auth0:Domain");
            Require(options.Auth0.Audience, "Auth:Auth0:Audience");
            Require(options.Auth0.WebClientId, "Auth:Auth0:WebClientId");
            Require(options.Auth0.WebClientSecret, "Auth:Auth0:WebClientSecret");
            Require(options.PublicOrigin, "Auth:PublicOrigin");
            if (missing.Count > 0)
            {
                return ValidateOptionsResult.Fail("Sign-in needs these settings outside Development: " + string.Join(", ", missing) + ".");
            }

            return Uri.TryCreate(options.PublicOriginOrNull, UriKind.Absolute, out var origin) && origin.Scheme == Uri.UriSchemeHttps
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail("Auth:PublicOrigin must be an https address, for example https://pc.tailnet.ts.net.");

            void Require(string? value, string key)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    missing.Add(key);
                }
            }
        }
    }

    /// <summary>The sign-in scheme the OpenID Connect handler is given. Using it is a bug: only <see cref="OidcCallback"/> issues sessions.</summary>
    private sealed class RefuseSignInHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : SignInAuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());

        protected override Task HandleSignInAsync(ClaimsPrincipal user, AuthenticationProperties? properties) =>
            throw new InvalidOperationException("The OpenID Connect handler must never sign anyone in; OidcCallback decides who gets a session.");

        protected override Task HandleSignOutAsync(AuthenticationProperties? properties) => Task.CompletedTask;
    }
}
