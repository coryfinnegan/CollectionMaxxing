using System.Diagnostics;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Api.Auth;

/// <summary>The problem responses of sign-in, setup and re-authentication.</summary>
public static class AuthProblems
{
    /// <summary>The <c>type</c> of the 401 that asks the web app to run a passkey sign-in again.</summary>
    public const string ReauthRequiredType = "https://collectionmaxxing.app/problems/reauth_required";

    /// <summary>401 with <c>reauth: true</c>: the action needs a passkey assertion from the last five minutes.</summary>
    public static IResult ReauthRequired() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status401Unauthorized,
            title: "Confirm it's you",
            detail: "This needs a passkey sign-in from the last 5 minutes. Sign in with your passkey again.",
            type: ReauthRequiredType,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "reauth_required",
                ["reauth"] = true,
                ["traceId"] = Activity.Current?.Id,
            });

    /// <summary>403: a cookie-authenticated write without the <c>X-CSRF: 1</c> header.</summary>
    public static IResult CsrfRequired() =>
        ProblemResults.Problem(
            StatusCodes.Status403Forbidden,
            "Forbidden",
            "Writes from the web app must send the header X-CSRF: 1.",
            "csrf_required");

    /// <summary>429, with <c>Retry-After</c> set by the caller.</summary>
    public static IResult TooManyAttempts() =>
        ProblemResults.Problem(
            StatusCodes.Status429TooManyRequests,
            "Too many attempts",
            "Too many attempts. Wait a minute and try again.",
            "rate_limited");

    /// <summary>401 for a passkey (or session) the server doesn't accept. Deliberately says nothing about why.</summary>
    public static IResult NotSignedIn(string detail = "That passkey wasn't accepted.") =>
        ProblemResults.Problem(StatusCodes.Status401Unauthorized, "Unauthorized", detail, "passkey_rejected");

    /// <summary>The ceremony didn't start with a code, token or recent sign-in, or it timed out.</summary>
    public static IResult CeremonyRequired() =>
        ProblemResults.Problem(
            StatusCodes.Status400BadRequest,
            "Start again",
            "Passkeys can only be created after entering a setup code, recovery code or invite, or while signed in. Start again.",
            "passkey_ceremony_required");

    /// <summary>
    /// Business failures of the sign-in use cases: a wrong code is a 400, a code that expired or was used is 410, and
    /// a closed setup is a 404 (the setup endpoints do not exist once there is an owner).
    /// </summary>
    public static IResult From(DomainError error) => error.Code switch
    {
        "code.expired" or "code.used" => ProblemResults.Problem(StatusCodes.Status410Gone, "Gone", error.Message, error.Code),
        _ => error.ToProblem(),
    };
}
