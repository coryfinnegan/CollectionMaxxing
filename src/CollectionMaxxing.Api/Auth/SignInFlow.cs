using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Application.Accounts.Devices;
using CollectionMaxxing.Application.Invites;
using CollectionMaxxing.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace CollectionMaxxing.Api.Auth;

/// <summary>
/// The sign-in endpoints of docs/14 and the contract addendum of issue #228: starting an Auth0 sign-in, checking a setup or recovery
/// code first, and signing out. The callback itself is <see cref="OidcCallback"/>.
/// </summary>
public sealed class SignInFlow(
    SignInIntentCookie intents,
    PlanSignIn.Handler plan,
    GetInvitePreview.Handler invites,
    IOptions<AuthOptions> auth,
    IPublicServerUrl server)
{
    /// <summary>
    /// <c>GET /account/login</c>: a browser navigation that ends at Auth0. <paramref name="returnUrl"/> must be a local path.
    /// <c>reauth=1</c> and <c>link=1</c> need the session cookie and make Auth0 ask again (<c>max_age=0</c>, <c>prompt=login</c>);
    /// <c>invite</c> joins a library; <c>intent=setup</c> or <c>intent=recover</c> carries on from <c>POST /setup</c> or
    /// <c>POST /account/recover</c>. Whatever it is travels in the sealed intent cookie, never in Auth0's state alone. An invite that
    /// can't be used is turned away here already, so nobody goes to Auth0 for nothing (it is checked again when they come back).
    /// </summary>
    public async Task<IResult> StartAsync(HttpContext http, string? returnUrl, string? reauth, string? link, string? invite, string? intent)
    {
        var target = string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl;
        if (!IsLocal(target))
        {
            return AuthProblems.BadReturnUrl();
        }

        var bearer = AuthSetup.IsBearerCall(http.Request);
        var userId = bearer ? null : AuthCookie.UserId(http.User);
        var sessionId = bearer ? null : CurrentSession.WebSessionId(http.User);
        var wantsLink = link == "1";
        var wantsReauth = reauth == "1";

        SignInIntent purpose;
        if (!string.IsNullOrEmpty(invite))
        {
            if (!(await invites.HandleAsync(invite, http.RequestAborted)).IsSuccess)
            {
                return Results.Redirect(OidcCallback.LoginPage + "?error=invite_invalid");
            }

            purpose = new SignInIntent(SignInPurpose.Invite, InviteToken: invite);
        }
        else if (wantsLink || wantsReauth)
        {
            if (userId is null || sessionId is null)
            {
                return Results.Redirect(OidcCallback.LoginPage);
            }

            purpose = wantsLink
                ? new SignInIntent(SignInPurpose.Link, UserId: userId, SessionAuthTime: RecentSignInFilter.AuthTime(http.User))
                : new SignInIntent(SignInPurpose.Reauth, UserId: userId);
        }
        else if (intents.Read(http) is { } pending && PendingMatches(pending.Intent.Purpose, intent))
        {
            purpose = pending.Intent;
        }
        else
        {
            purpose = new SignInIntent(SignInPurpose.SignIn);
        }

        var sealedIntent = intents.Write(http, purpose, target, userId, sessionId);
        var properties = new OpenIdConnectChallengeProperties();
        properties.Items[SignInIntentCookie.StateKey] = sealedIntent.Id;
        if (wantsLink || wantsReauth)
        {
            properties.MaxAge = TimeSpan.Zero;
            properties.Prompt = "login";
        }

        return Results.Challenge(properties, [Auth0Setup.OidcScheme]);
    }

    /// <summary><c>POST /setup</c>: the setup code, checked only while there is no owner. Spent when the sign-in comes back.</summary>
    public async Task<IResult> StartSetupAsync(HttpContext http, SetupRequest body, CancellationToken cancellationToken)
    {
        var planned = await plan.ForSetupAsync(body.Code, cancellationToken);
        if (!planned.IsSuccess)
        {
            return AuthProblems.From(planned.Error!);
        }

        intents.Write(http, planned.Value, "/");
        return TypedResults.Ok(new NextStepDto("/account/login?intent=setup"));
    }

    /// <summary><c>POST /account/recover</c>: a recovery code from <c>cm-admin recovery-code</c>. The next sign-in is linked to the owner.</summary>
    public async Task<IResult> StartRecoveryAsync(HttpContext http, RecoverRequest body, CancellationToken cancellationToken)
    {
        var planned = await plan.ForRecoveryAsync(body.Code, cancellationToken);
        if (!planned.IsSuccess)
        {
            return AuthProblems.From(planned.Error!);
        }

        intents.Write(http, planned.Value, "/");
        return TypedResults.Ok(new NextStepDto("/account/login?intent=recover"));
    }

    /// <summary>
    /// <c>POST /account/logout</c>: revokes this browser's session if it has one, forgets the cookie either way, and answers with Auth0's
    /// sign-out address, which the web app navigates to (so the CSP needs nothing new).
    /// </summary>
    public async Task<IResult> LogoutAsync(HttpContext http)
    {
        await AuthCookie.SignOutAsync(http);
        intents.Clear(http);
        var returnTo = Uri.EscapeDataString(server.Value);
        var clientId = Uri.EscapeDataString(auth.Value.Auth0.WebClientId ?? string.Empty);
        var issuer = Auth0Setup.Issuer(auth.Value);
        return TypedResults.Ok(new LogoutDto($"{issuer}v2/logout?client_id={clientId}&returnTo={returnTo}"));
    }

    /// <summary>A path on this site: starts with one <c>/</c>, not <c>//</c> or <c>/\</c>, and has no control characters.</summary>
    public static bool IsLocal(string url) =>
        url.Length > 0
        && url[0] == '/'
        && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'))
        && !url.Any(c => char.IsControl(c) || c == '\\');

    private static bool PendingMatches(SignInPurpose purpose, string? intent) =>
        (purpose, intent) is (SignInPurpose.Setup, "setup") or (SignInPurpose.Recovery, "recover");
}
