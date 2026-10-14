using CollectionMaxxing.Api.Auth.Pairing;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Accounts;
using Microsoft.AspNetCore.Mvc;

namespace CollectionMaxxing.Api.Auth;

/// <summary>
/// First-run setup, passkey sign-in and recovery, and the OpenIddict token endpoints (docs/07, "Flows"). These are the
/// only endpoints that answer anonymous callers, and each one is on the allow-list in the authorization matrix. The pages
/// (<c>GET /setup</c>, <c>/account/login</c>, <c>/join/{token}</c>) are for browsers and the <c>/connect/*</c> endpoints belong
/// to OpenIddict, so those are left out of the OpenAPI document. Every endpoint that takes a code or a passkey is rate-limited.
/// </summary>
public static class SignInEndpoints
{
    public static IServiceCollection AddSignIn(this IServiceCollection services) => SignInServices.Add(services);

    public static IEndpointRouteBuilder MapSignInEndpoints(this IEndpointRouteBuilder app)
    {
        // OpenIddict's token endpoint (iPhone): grants urn:cm:device_pairing and refresh_token. OpenIddict reads and validates the
        // request and passes it through to here; Auth/Pairing builds the answer. The pairing grant limits its own attempts (the grant
        // type is in the body, which the ASP.NET rate limiter can't see).
        app.MapPost("/connect/token", TokenEndpoint.HandleAsync)
            .WithName("Token").WithTags("Sign-in")
            .ExcludeFromDescription().AllowAnonymous();

        // Revocation is OpenIddict's too, and it answers before the request gets here (see RevokeDeviceOnRevocation). This route
        // exists so the endpoint is on the allow-list and in the matrix; a request that does arrive wasn't a revocation request.
        app.MapPost("/connect/revoke", () => ProblemResults.Problem(StatusCodes.Status400BadRequest, "Bad request", "Send a form-encoded revocation request with a token."))
            .WithName("RevokeToken").WithTags("Sign-in")
            .ExcludeFromDescription().AllowAnonymous();

        // Setup exists only while there is no owner; afterwards it answers 404.
        app.MapGet("/setup", async (GetSetupStatus.Handler status, SpaPage spa, CancellationToken cancellationToken) =>
                await status.IsOpenAsync(cancellationToken) ? spa.Index() : Results.NotFound())
            .WithName("SetupPage").WithTags("Sign-in")
            .ExcludeFromDescription().AllowAnonymous();
        app.MapPost("/setup", (SetupRequest body, HttpContext http, PasskeyCeremonies ceremonies, CancellationToken cancellationToken) =>
                ceremonies.StartSetupAsync(http, body, cancellationToken))
            .WithName("Setup").WithTags("Sign-in")
            .Produces<PasskeyOptionsDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status410Gone)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .AllowAnonymous().RequireRateLimiting(RateLimiting.PolicyName);

        // Passkey sign-in and sign-out for the web app.
        app.MapGet("/account/login", (SpaPage spa) => spa.Index())
            .WithName("LoginPage").WithTags("Sign-in")
            .ExcludeFromDescription().AllowAnonymous();
        app.MapPost("/account/login", (PasskeyLoginRequest body, HttpContext http, PasskeyCeremonies ceremonies) =>
                ceremonies.LoginAsync(http, body))
            .WithName("Login").WithTags("Sign-in")
            .Produces<SignedInDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .AllowAnonymous().RequireRateLimiting(RateLimiting.PolicyName);
        app.MapPost("/account/logout", (Func<HttpContext, Task<IResult>>)PasskeyCeremonies.LogoutAsync)
            .WithName("Logout").WithTags("Sign-in")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireAuthorization();

        // Passkey ceremonies: while setting up, joining by invite, recovering or adding a passkey from Settings.
        app.MapPost(
                "/account/passkey/creation-options",
                (PasskeyCreationOptionsRequest body, HttpContext http, PasskeyCeremonies ceremonies, CancellationToken cancellationToken) =>
                    ceremonies.CreationOptionsAsync(http, body, cancellationToken))
            .WithName("PasskeyCreationOptions").WithTags("Sign-in")
            .Produces<PasskeyOptionsDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status410Gone)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .AllowAnonymous().RequireRateLimiting(RateLimiting.PolicyName);
        app.MapPost("/account/passkey/request-options", (HttpContext http, PasskeyCeremonies ceremonies) => ceremonies.RequestOptionsAsync(http))
            .WithName("PasskeyRequestOptions").WithTags("Sign-in")
            .Produces<PasskeyOptionsDto>()
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .AllowAnonymous().RequireRateLimiting(RateLimiting.PolicyName);
        app.MapPost(
                "/account/passkey/register",
                ([FromQuery(Name = "invite")] string? invite, PasskeyRegistrationRequest body, HttpContext http, PasskeyCeremonies ceremonies, CancellationToken cancellationToken) =>
                    ceremonies.RegisterAsync(http, invite, body, cancellationToken))
            .WithName("PasskeyRegister").WithTags("Sign-in")
            .Produces<SignedInDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound) // an invite that can't be used (ID-3a)
            .ProducesProblem(StatusCodes.Status410Gone)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .AllowAnonymous().RequireRateLimiting(RateLimiting.PolicyName);

        // Recovery after losing every passkey: a one-time code from the PC's command line.
        app.MapPost("/account/recover", (RecoverRequest body, HttpContext http, PasskeyCeremonies ceremonies, CancellationToken cancellationToken) =>
                ceremonies.StartRecoveryAsync(http, body, cancellationToken))
            .WithName("Recover").WithTags("Sign-in")
            .Produces<PasskeyOptionsDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status410Gone)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .AllowAnonymous().RequireRateLimiting(RateLimiting.PolicyName);

        // The page a family member opens from an invite link. It is the same page for every token; the SPA asks GET /api/v1/invites/{token}
        // whether the invite is usable, and the invite is only spent when they register (ID-3a).
        app.MapGet("/join/{token}", (SpaPage spa) => spa.Index())
            .WithName("JoinPage").WithTags("Sign-in")
            .ExcludeFromDescription().AllowAnonymous();

        return app;
    }
}
