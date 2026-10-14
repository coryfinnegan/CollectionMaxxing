using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Auth.Pairing;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Application.Accounts.Devices;
using CollectionMaxxing.Application.Common;

namespace CollectionMaxxing.Api.Accounts;

public static class AccountEndpoints
{
    public static IServiceCollection AddAccounts(this IServiceCollection services)
    {
        services.AddScoped<GetMe.Handler>();
        services.AddDevicePairing();
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

        app.MapDelete("/api/v1/me", () => StubEndpoints.NotImplemented())
            .WithName("DeleteMe").WithTags("Account")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireAuthorization().RequireRecentPasskey().IsStub();

        // Paired devices (ID-2). The phones are OpenIddict authorizations; revoking one ends its tokens at once.
        app.MapGet("/api/v1/me/sessions", async (ICurrentUser user, HttpContext http, ListSessions.Handler handler, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handler.HandleAsync(user.UserId!.Value, CurrentSession.Id(http.User), cancellationToken)))
            .WithName("ListSessions").WithTags("Account")
            .Produces<Page<SessionDto>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireAuthorization();

        app.MapDelete("/api/v1/me/sessions/{id:guid}", async (Guid id, ICurrentUser user, RevokeSession.Handler handler, CancellationToken cancellationToken) =>
            {
                var result = await handler.HandleAsync(user.UserId!.Value, id, cancellationToken);
                return result.IsSuccess ? Results.NoContent() : result.Error!.ToProblem();
            })
            .WithName("RevokeSession").WithTags("Account")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireAuthorization();

        app.MapPost("/api/v1/me/devices/pair", async (ICurrentUser user, CreatePairing.Handler handler, CancellationToken cancellationToken) =>
            {
                var result = await handler.HandleAsync(user.UserId!.Value, cancellationToken);
                return result.IsSuccess
                    ? Results.Created((string?)null, result.Value)
                    : ProblemResults.Problem(StatusCodes.Status401Unauthorized, "Unauthorized", result.Error!.Message, result.Error.Code);
            })
            .WithName("PairDevice").WithTags("Account")
            .Produces<PairDeviceDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireAuthorization().RequireRecentPasskey();

        app.MapGet("/api/v1/me/export", () => StubEndpoints.NotImplemented())
            .WithName("StartExport").WithTags("Account")
            .Produces<ExportDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireAuthorization().IsStub();

        return app;
    }
}
