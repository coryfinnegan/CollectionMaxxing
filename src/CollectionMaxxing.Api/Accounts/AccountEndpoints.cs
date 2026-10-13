using CollectionMaxxing.Application.Accounts;

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

        return app;
    }
}
