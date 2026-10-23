using System.Globalization;
using System.Security.Claims;
using CollectionMaxxing.Application.Accounts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.WebUtilities;

namespace CollectionMaxxing.Api.Auth;

/// <summary>
/// What happens at <c>/signin-oidc</c> once the OpenID Connect handler has checked Auth0's answer (state, nonce, the ID token's
/// signature, issuer and audience). The handler is stopped there (<c>HandleResponse</c>), so it never signs anyone in; this code reads
/// the sealed intent, lets <see cref="CompleteSignIn"/> decide, and only then issues the session cookie. The answer is always a 302
/// (contract addendum, issue #228).
/// </summary>
public static class OidcCallback
{
    public const string NotLinkedPage = "/account/not-linked";
    public const string LoginPage = "/login";

    public static async Task HandleAsync(TicketReceivedContext context)
    {
        context.HandleResponse(); // from here on, our code decides; the handler's own sign-in never runs
        var http = context.HttpContext;
        var cookie = http.RequestServices.GetRequiredService<SignInIntentCookie>();
        var sealedIntent = cookie.Read(http);
        cookie.Clear(http);

        // The intent must be the one this very challenge was started with: Auth0's state names it, the sealed cookie holds it.
        if (sealedIntent is null
            || context.Properties?.Items.TryGetValue(SignInIntentCookie.StateKey, out var expected) != true
            || !string.Equals(expected, sealedIntent.Id, StringComparison.Ordinal)
            || context.Principal?.FindFirstValue("sub") is not { Length: > 0 } subject)
        {
            Redirect(http, LoginPage + "?error=signin_failed");
            return;
        }

        var identity = new ExternalIdentity(
            subject,
            context.Principal.FindFirstValue("email"),
            context.Principal.FindFirstValue("name"),
            long.TryParse(context.Principal.FindFirstValue("auth_time"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null);

        var outcome = await http.RequestServices.GetRequiredService<CompleteSignIn.Handler>()
            .HandleAsync(sealedIntent.Intent, identity, http.RequestAborted);
        var returnUrl = sealedIntent.ReturnUrl;
        switch (outcome.Kind)
        {
            case SignInOutcomeKind.SignedIn:
                List<(Guid, Guid)> replaces = [];
                if (sealedIntent.UserId is { } previousUser && sealedIntent.WebSessionId is { } previousSession)
                {
                    replaces.Add((previousUser, previousSession));
                }

                await AuthCookie.SignInAsync(http, outcome.UserId!.Value, outcome.LoginId!.Value, identity.AuthTime, replaces);
                Redirect(http, returnUrl);
                break;
            case SignInOutcomeKind.Linked:
                Redirect(http, returnUrl); // the person's own session goes on as it was
                break;
            case SignInOutcomeKind.NotLinked:
                Redirect(http, NotLinkedPage);
                break;
            case SignInOutcomeKind.InviteInvalid:
                Redirect(http, LoginPage + "?error=invite_invalid");
                break;
            case SignInOutcomeKind.SetupClosed:
                Redirect(http, LoginPage + "?error=setup_closed");
                break;
            case SignInOutcomeKind.AlreadyLinked:
                Redirect(http, QueryHelpers.AddQueryString(returnUrl, "error", "already_linked"));
                break;
            case SignInOutcomeKind.ReauthFailed:
                Redirect(http, QueryHelpers.AddQueryString(returnUrl, "error", "reauth_failed"));
                break;
            default:
                Redirect(http, LoginPage + "?error=signin_failed");
                break;
        }
    }

    /// <summary>Auth0 said no, the person cancelled, or the state, nonce or token didn't check out: no session, back to the sign-in page.</summary>
    public static Task FailAsync(HandleRequestContext<RemoteAuthenticationOptions> context)
    {
        context.HandleResponse();
        context.HttpContext.RequestServices.GetRequiredService<SignInIntentCookie>().Clear(context.HttpContext);
        Redirect(context.HttpContext, LoginPage + "?error=signin_failed");
        return Task.CompletedTask;
    }

    private static void Redirect(HttpContext http, string location)
    {
        http.Response.StatusCode = StatusCodes.Status302Found;
        http.Response.Headers.Location = location;
    }
}
