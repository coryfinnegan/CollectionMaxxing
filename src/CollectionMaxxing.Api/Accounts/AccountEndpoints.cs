using CollectionMaxxing.Api.Accounts.Deletion;
using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Exports;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Application.Accounts.Devices;
using CollectionMaxxing.Application.Common;
using Microsoft.AspNetCore.Authentication;

namespace CollectionMaxxing.Api.Accounts;

public static class AccountEndpoints
{
    public static IServiceCollection AddAccounts(this IServiceCollection services)
    {
        services.AddScoped<GetMe.Handler>();
        services.AddScoped<ListWebSessions.Handler>();
        services.AddScoped<RevokeWebSession.Handler>();
        services.AddScoped<RevokeWebSessions.Handler>();
        services.AddAccountDeletion();
        services.AddExports(); // LIB-2b; the lead may move this line to Program.cs
        return services;
    }

    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/me", (GetMe.Handler handler) => TypedResults.Ok(handler.Handle()))
            .WithName("GetMe")
            .WithTags("Account")
            .Produces<MeResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireAuthorization();

        // Everything below is part of the v1 contract but not built yet (ID stream). Replace the stub body and drop .IsStub().
        app.MapPatch("/api/v1/me", (UpdateMeRequest body) => StubEndpoints.NotImplemented())
            .WithName("UpdateMe").WithTags("Account")
            .Produces<MeResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireAuthorization().IsStub();

        // Delete account (LIB-2c): a sign-in from the last five minutes, then soft delete; the purge job follows in 30 days.
        app.MapDeleteAccount();

        // Sign-in methods: the Auth0 identities linked to the caller (Google, Apple). Removing one needs a recent sign-in, and the last one stays.
        app.MapGet("/api/v1/me/sign-in-methods", async (ICurrentUser user, HttpContext http, ListSignInMethods.Handler handler, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handler.HandleAsync(user.UserId!.Value, CurrentSession.LoginId(http.User), cancellationToken)))
            .WithName("ListSignInMethods").WithTags("Account")
            .Produces<Page<SignInMethodDto>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireAuthorization();

        app.MapDelete("/api/v1/me/sign-in-methods/{id:guid}", async (Guid id, ICurrentUser user, RemoveSignInMethod.Handler handler, CancellationToken cancellationToken) =>
            {
                var result = await handler.HandleAsync(user.UserId!.Value, id, cancellationToken);
                return result.IsSuccess ? Results.NoContent() : result.Error!.ToProblem();
            })
            .WithName("RemoveSignInMethod").WithTags("Account")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireAuthorization().RequireRecentSignIn();

        // Signed-in browsers (the web session cookie): listed in Settings next to the phones, and each can be signed out.
        app.MapGet("/api/v1/me/web-sessions", async (ICurrentUser user, HttpContext http, ListWebSessions.Handler handler, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handler.HandleAsync(user.UserId!.Value, CurrentSession.WebSessionId(http.User), cancellationToken)))
            .WithName("ListWebSessions").WithTags("Account")
            .Produces<Page<SessionDto>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireAuthorization();

        app.MapDelete("/api/v1/me/web-sessions/{id:guid}", async (Guid id, ICurrentUser user, HttpContext http, RevokeWebSession.Handler handler, CancellationToken cancellationToken) =>
            {
                var result = await handler.HandleAsync(user.UserId!.Value, id, cancellationToken);
                if (!result.IsSuccess)
                {
                    return result.Error!.ToProblem();
                }

                if (id == CurrentSession.WebSessionId(http.User))
                {
                    await http.SignOutAsync(AuthCookie.Scheme); // the browser signed itself out: tell it to forget the cookie
                }

                return Results.NoContent();
            })
            .WithName("RevokeWebSession").WithTags("Account")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireAuthorization();

        // "Sign out everywhere" for the web: every browser of the caller, this one included.
        app.MapDelete("/api/v1/me/web-sessions", async (ICurrentUser user, HttpContext http, RevokeWebSessions.Handler handler, CancellationToken cancellationToken) =>
            {
                await handler.HandleAsync(user.UserId!.Value, cancellationToken);
                await http.SignOutAsync(AuthCookie.Scheme); // this browser's cookie is dead already; tell the browser to forget it
                return Results.NoContent();
            })
            .WithName("RevokeWebSessions").WithTags("Account")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireAuthorization();

        // Export my data (LIB-2b): the start route plus its status and download siblings.
        app.MapExportEndpoints();

        return app;
    }
}
