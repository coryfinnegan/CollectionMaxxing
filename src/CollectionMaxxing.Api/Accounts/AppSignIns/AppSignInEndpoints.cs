using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Accounts.Devices;
using CollectionMaxxing.Application.Common;
using Microsoft.AspNetCore.Mvc;

namespace CollectionMaxxing.Api.Accounts.AppSignIns;

/// <summary>The body of <c>POST /api/v1/me/app-sign-ins</c>.</summary>
public sealed record RegisterAppSignInRequest(string? DeviceName);

/// <summary>
/// The iPhones signed in with Auth0 (docs/14, "Devices and revocation"): the app registers itself after signing in so Settings can list it,
/// and "sign out my iPhones" ends all of them together. Every route is for any signed-in person, about their own phones only.
/// </summary>
public static class AppSignInEndpoints
{
    public const string InstallIdHeader = "X-Install-Id";

    public static IServiceCollection AddAppSignIns(this IServiceCollection services)
    {
        services.AddScoped<RegisterAppSignIn.Handler>();
        services.AddScoped<ListAppSignIns.Handler>();
        services.AddScoped<SignOutAppSignIns.Handler>();
        return services;
    }

    public static IEndpointRouteBuilder MapAppSignIns(this IEndpointRouteBuilder app)
    {
        // Bearer only: a browser has no install id and is listed under web sessions. A phone registers on every launch; the first time is 201.
        app.MapPost("/api/v1/me/app-sign-ins", async (
                RegisterAppSignInRequest? body,
                HttpContext http,
                ICurrentUser user,
                RegisterAppSignIn.Handler handler,
                CancellationToken cancellationToken) =>
            {
                if (CurrentSession.WebSessionId(http.User) is not null)
                {
                    return ProblemResults.Problem(
                        StatusCodes.Status403Forbidden,
                        "Forbidden",
                        "Only the iPhone app registers itself, with its access token.",
                        "app_sign_in.bearer_only");
                }

                if (!Guid.TryParse(http.Request.Headers[InstallIdHeader].ToString(), out var installId))
                {
                    return AppSignInErrors.InstallIdInvalid.ToProblem();
                }

                var result = await handler.HandleAsync(new RegisterAppSignIn.Command(user.UserId!.Value, installId, body?.DeviceName), cancellationToken);
                if (!result.IsSuccess)
                {
                    return result.Error!.ToProblem();
                }

                var registered = result.Value!;
                return registered.Created
                    ? Results.Created($"/api/v1/me/app-sign-ins/{registered.SignIn.Id}", registered.SignIn)
                    : Results.Ok(registered.SignIn);
            })
            .WithName("RegisterAppSignIn").WithTags("Account")
            .Accepts<RegisterAppSignInRequest>("application/json")
            .Produces<AppSignInDto>()
            .Produces<AppSignInDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .RequireAuthorization();

        app.MapGet("/api/v1/me/app-sign-ins", async (ICurrentUser user, ListAppSignIns.Handler handler, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handler.HandleAsync(user.UserId!.Value, cancellationToken)))
            .WithName("ListAppSignIns").WithTags("Account")
            .Produces<Page<AppSignInDto>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireAuthorization();

        // Signs out every iPhone of this person: access tokens issued before now are refused at once, then Auth0 ends the refresh tokens.
        app.MapDelete("/api/v1/me/app-sign-ins", async (ICurrentUser user, SignOutAppSignIns.Handler handler, CancellationToken cancellationToken) =>
            {
                await handler.HandleAsync(user.UserId!.Value, cancellationToken);
                return Results.NoContent();
            })
            .WithName("SignOutAppSignIns").WithTags("Account")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireAuthorization();

        return app;
    }
}
