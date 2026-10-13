using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Admin;
using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Common;

namespace CollectionMaxxing.Api.Admin;

/// <summary>Operations endpoints for the platform administrator (docs/04, "Admin"). A separate route group and policy, not library roles.</summary>
public static class AdminEndpoints
{
    public static IServiceCollection AddAdmin(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin")
            .RequireAuthorization(Policies.Admin)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // Promote a user-created release to global.
        admin.MapPost("/catalog/releases/{id:guid}:verify", (Guid id) => StubEndpoints.NotImplemented())
            .WithName("VerifyRelease").WithTags("Admin")
            .Produces<ReleaseDto>()
            .ProducesProblem(StatusCodes.Status404NotFound).IsStub();

        // Failed and pending jobs.
        admin.MapGet("/jobs", () => StubEndpoints.NotImplemented())
            .WithName("ListJobs").WithTags("Admin")
            .Produces<Page<AdminJobDto>>().IsStub();

        return app;
    }
}
