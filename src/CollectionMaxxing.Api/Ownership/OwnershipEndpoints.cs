using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Libraries;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Ownership;
using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Api.Ownership;

/// <summary>"Do I own this?" (docs/04, "Items"): used by web search, the offline cache and future Siri and widgets.</summary>
public static class OwnershipEndpoints
{
    public static IServiceCollection AddOwnership(this IServiceCollection services)
    {
        services.AddScoped<OwnershipQuery>();
        services.AddScoped<GetOwnership.Handler>();
        services.AddScoped<GetOwnershipSnapshot.Handler>();
        return services;
    }

    public static IEndpointRouteBuilder MapOwnershipEndpoints(this IEndpointRouteBuilder app)
    {
        var library = app.MapLibraryGroup();

        library.MapGet("/ownership", Get)
            .WithName("GetOwnership").WithTags("Ownership")
            .Produces<OwnershipDto>()
            .ProducesValidationProblem()
            .RequireLibraryRole(LibraryRole.Viewer);

        // Added for the phone's offline cache (APP-3a): a paged delta of owned items, with tombstones. No covers.
        library.MapGet("/ownership/snapshot", Snapshot)
            .WithName("GetOwnershipSnapshot").WithTags("Ownership")
            .Produces<OwnershipSnapshotDto>()
            .ProducesValidationProblem()
            .RequireLibraryRole(LibraryRole.Viewer);

        return app;
    }

    private static async Task<IResult> Get(
        Guid libraryId, [AsParameters] OwnershipRequest query, GetOwnership.Handler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetOwnership.Query(libraryId, query.Code, query.ReleaseId, query.WorkId), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToProblem();
    }

    private static async Task<IResult> Snapshot(
        Guid libraryId, [AsParameters] OwnershipSnapshotRequest query, GetOwnershipSnapshot.Handler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetOwnershipSnapshot.Query(libraryId, query.Since, query.Limit), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToProblem();
    }
}
