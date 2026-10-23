using CollectionMaxxing.Application.Accounts;
using Microsoft.AspNetCore.Mvc;

namespace CollectionMaxxing.Api.Auth;

/// <summary>
/// Auth0 sign-in, recovery and sign-out (docs/14, docs/15, issue #228). These are the endpoints that answer anonymous callers, and each one
/// is on the allow-list in the authorization matrix. The browser navigations (<c>/account/login</c>, <c>/join/{token}</c>) are left out of
/// the OpenAPI document. Every endpoint that takes a code or starts a sign-in is rate-limited per client. The Auth0 callback
/// (<c>/signin-oidc</c>) is the OpenID Connect handler's, not an endpoint: a first sign-in there makes the account (ADR-026).
/// </summary>
public static class SignInEndpoints
{
    public static IServiceCollection AddSignIn(this IServiceCollection services) => SignInServices.Add(services);

    public static IEndpointRouteBuilder MapSignInEndpoints(this IEndpointRouteBuilder app)
    {
        // Sign in: off to Auth0, and back through /signin-oidc.
        app.MapGet(
                "/account/login",
                (HttpContext http, SignInFlow flow, string? returnUrl, string? reauth, string? link, string? intent) =>
                    flow.Start(http, returnUrl, reauth, link, intent))
            .WithName("Login").WithTags("Sign-in")
            .ExcludeFromDescription().AllowAnonymous().RequireRateLimiting(RateLimiting.PolicyName);

        // Sign out. Works without a session too ("Use another account" signs out of Auth0), but always needs X-CSRF: 1.
        app.MapPost("/account/logout", (HttpContext http, SignInFlow flow) => flow.LogoutAsync(http))
            .WithName("Logout").WithTags("Sign-in")
            .Produces<LogoutDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .AllowAnonymous().AddEndpointFilter<RequireCsrfHeaderFilter>();

        // Recovery after losing every sign-in: a one-time code from the host's command line links the next identity to the named account.
        app.MapPost("/account/recover", (RecoverRequest body, HttpContext http, SignInFlow flow, CancellationToken cancellationToken) =>
                flow.StartRecoveryAsync(http, body, cancellationToken))
            .WithName("Recover").WithTags("Sign-in")
            .Produces<NextStepDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status410Gone)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .AllowAnonymous().RequireRateLimiting(RateLimiting.PolicyName);

        // The page someone opens from an invite link. The SPA asks GET /api/v1/invites/{token} what the invite is for. Signed out, it
        // signs in first (/account/login?returnUrl=/join/{token}); signed in, it joins with POST /api/v1/invites:accept.
        app.MapGet("/join/{token}", (SpaPage spa) => spa.Index())
            .WithName("JoinPage").WithTags("Sign-in")
            .ExcludeFromDescription().AllowAnonymous();

        return app;
    }
}

/// <summary>
/// <c>X-CSRF: 1</c> on this endpoint whether or not a session cookie came with it (<see cref="CsrfMiddleware"/> asks only cookie
/// callers). Sign-out has to answer anonymous callers, and a page elsewhere must still not be able to post it.
/// </summary>
public sealed class RequireCsrfHeaderFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        context.HttpContext.Request.Headers[CsrfMiddleware.HeaderName] == CsrfMiddleware.HeaderValue
            ? next(context)
            : ValueTask.FromResult<object?>(AuthProblems.CsrfRequired());
}
