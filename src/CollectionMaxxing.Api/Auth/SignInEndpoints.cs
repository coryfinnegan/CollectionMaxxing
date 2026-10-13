using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Accounts;
using Microsoft.AspNetCore.Mvc;

namespace CollectionMaxxing.Api.Auth;

/// <summary>
/// First-run setup, passkey sign-in and recovery, and the OpenIddict token endpoints (docs/07, "Flows"). These are the
/// only endpoints that answer anonymous callers, and each one is on the allow-list in the authorization matrix. Sign-in
/// is built by the ID stream; the pages (<c>GET /setup</c>, <c>/account/login</c>, <c>/join/{token}</c>) are for browsers and
/// the <c>/connect/*</c> endpoints belong to OpenIddict, so those are left out of the OpenAPI document.
/// </summary>
public static class SignInEndpoints
{
    public static IServiceCollection AddSignIn(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapSignInEndpoints(this IEndpointRouteBuilder app)
    {
        // OpenIddict's token endpoints (iPhone): grants urn:cm:device_pairing and refresh_token; revocation.
        app.MapPost("/connect/token", () => StubEndpoints.NotImplemented())
            .WithName("Token").WithTags("Sign-in")
            .ExcludeFromDescription().AllowAnonymous().IsStub();
        app.MapPost("/connect/revoke", () => StubEndpoints.NotImplemented())
            .WithName("RevokeToken").WithTags("Sign-in")
            .ExcludeFromDescription().AllowAnonymous().IsStub();

        // Setup exists only while there is no owner; afterwards it answers 404.
        app.MapGet("/setup", () => StubEndpoints.NotImplemented())
            .WithName("SetupPage").WithTags("Sign-in")
            .ExcludeFromDescription().AllowAnonymous().IsStub();
        app.MapPost("/setup", (SetupRequest body) => StubEndpoints.NotImplemented())
            .WithName("Setup").WithTags("Sign-in")
            .Produces<PasskeyOptionsDto>()
            .ProducesValidationProblem()
            .AllowAnonymous().IsStub();

        // Passkey sign-in and sign-out for the web app.
        app.MapGet("/account/login", () => StubEndpoints.NotImplemented())
            .WithName("LoginPage").WithTags("Sign-in")
            .ExcludeFromDescription().AllowAnonymous().IsStub();
        app.MapPost("/account/login", (PasskeyLoginRequest body) => StubEndpoints.NotImplemented())
            .WithName("Login").WithTags("Sign-in")
            .Produces<SignedInDto>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .AllowAnonymous().IsStub();
        app.MapPost("/account/logout", () => StubEndpoints.NotImplemented())
            .WithName("Logout").WithTags("Sign-in")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireAuthorization().IsStub();

        // Passkey ceremonies: while setting up, joining by invite, recovering or adding a passkey from Settings.
        app.MapPost("/account/passkey/creation-options", (PasskeyCreationOptionsRequest body) => StubEndpoints.NotImplemented())
            .WithName("PasskeyCreationOptions").WithTags("Sign-in")
            .Produces<PasskeyOptionsDto>()
            .ProducesValidationProblem()
            .AllowAnonymous().IsStub();
        app.MapPost("/account/passkey/request-options", () => StubEndpoints.NotImplemented())
            .WithName("PasskeyRequestOptions").WithTags("Sign-in")
            .Produces<PasskeyOptionsDto>()
            .AllowAnonymous().IsStub();
        app.MapPost(
                "/account/passkey/register",
                ([FromQuery(Name = "invite")] string? invite, PasskeyRegistrationRequest body) => StubEndpoints.NotImplemented())
            .WithName("PasskeyRegister").WithTags("Sign-in")
            .Produces<SignedInDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .AllowAnonymous().IsStub();

        // Recovery after losing every passkey: a one-time code from the PC's command line.
        app.MapPost("/account/recover", (RecoverRequest body) => StubEndpoints.NotImplemented())
            .WithName("Recover").WithTags("Sign-in")
            .Produces<PasskeyOptionsDto>()
            .ProducesValidationProblem()
            .AllowAnonymous().IsStub();

        // The page a family member opens from an invite link.
        app.MapGet("/join/{token}", (string token) => StubEndpoints.NotImplemented())
            .WithName("JoinPage").WithTags("Sign-in")
            .ExcludeFromDescription().AllowAnonymous().IsStub();

        return app;
    }
}
