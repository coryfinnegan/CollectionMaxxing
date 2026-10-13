using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Application.Common;

namespace CollectionMaxxing.Api.Accounts;

public static class AccountEndpoints
{
    public static IServiceCollection AddAccounts(this IServiceCollection services)
    {
        services.AddScoped<GetMe.Handler>();
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

        app.MapGet("/api/v1/me/sessions", () => StubEndpoints.NotImplemented())
            .WithName("ListSessions").WithTags("Account")
            .Produces<Page<SessionDto>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireAuthorization().IsStub();

        app.MapDelete("/api/v1/me/sessions/{id:guid}", (Guid id) => StubEndpoints.NotImplemented())
            .WithName("RevokeSession").WithTags("Account")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireAuthorization().IsStub();

        app.MapPost("/api/v1/me/devices/pair", () => StubEndpoints.NotImplemented())
            .WithName("PairDevice").WithTags("Account")
            .Produces<PairDeviceDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireAuthorization().RequireRecentPasskey().IsStub();

        app.MapGet("/api/v1/me/export", () => StubEndpoints.NotImplemented())
            .WithName("StartExport").WithTags("Account")
            .Produces<ExportDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireAuthorization().IsStub();

        return app;
    }
}
