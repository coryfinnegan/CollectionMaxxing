using System.Diagnostics;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Api.Auth;

/// <summary>The problem responses of sign-in, setup and re-authentication.</summary>
public static class AuthProblems
{
    /// <summary>The <c>type</c> of the 401 that asks the web app to send the person through a fresh sign-in.</summary>
    public const string ReauthRequiredType = "https://collectionmaxxing.app/problems/reauth_required";

    /// <summary>401 with <c>reauth: true</c>: the action needs a sign-in from the last five minutes.</summary>
    public static IResult ReauthRequired() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status401Unauthorized,
            title: "Confirm it's you",
            detail: "This needs a sign-in from the last 5 minutes. Sign in again to continue.",
            type: ReauthRequiredType,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "reauth_required",
                ["reauth"] = true,
                ["traceId"] = Activity.Current?.Id,
            });

    /// <summary>403: a cookie-authenticated write (or a sign-out) without the <c>X-CSRF: 1</c> header.</summary>
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

    /// <summary>403: a valid Auth0 token whose identity isn't linked to anyone here. Nothing was created for it.</summary>
    public static IResult NotLinked() =>
        ProblemResults.Problem(
            StatusCodes.Status403Forbidden,
            "Forbidden",
            "This account isn't part of a library. Ask the owner for an invite.",
            "account.not_linked");

    /// <summary>400: a <c>returnUrl</c> that would leave this site.</summary>
    public static IResult BadReturnUrl() =>
        ProblemResults.Problem(
            StatusCodes.Status400BadRequest,
            "Bad request",
            "The return address must be a page of this site.",
            "account.bad_return_url");

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
