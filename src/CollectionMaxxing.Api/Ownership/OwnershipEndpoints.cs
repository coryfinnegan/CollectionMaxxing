using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Libraries;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Ownership;
using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Api.Ownership;

/// <summary>"Do I own this?" (docs/04, "Items"): used by web search, the offline cache and future Siri and widgets.</summary>
public static class OwnershipEndpoints
{
    public static IServiceCollection AddOwnership(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapOwnershipEndpoints(this IEndpointRouteBuilder app)
    {
        var library = app.MapLibraryGroup();

        library.MapGet("/ownership", (Guid libraryId, [AsParameters] OwnershipQuery query) => StubEndpoints.NotImplemented())
            .WithName("GetOwnership").WithTags("Ownership")
            .Produces<OwnershipDto>()
            .ProducesValidationProblem()
            .RequireLibraryRole(LibraryRole.Viewer).IsStub();

        return app;
    }
}
