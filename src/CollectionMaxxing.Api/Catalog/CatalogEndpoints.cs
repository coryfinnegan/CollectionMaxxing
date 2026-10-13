using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Libraries;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Api.Catalog;

/// <summary>
/// The shared catalogue, cover images and the feature-print backup (docs/04, "Catalogue"). Catalogue reads need a signed-in
/// caller but no library; releases a library created for itself are visible to its members only (the data filter).
/// </summary>
public static class CatalogEndpoints
{
    public static IServiceCollection AddCatalog(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var catalog = app.MapGroup("/api/v1/catalog");

        // Local catalogue first, then providers; merged and scored.
        catalog.MapGet("/search", ([AsParameters] CatalogSearchQuery query) => StubEndpoints.NotImplemented())
            .WithName("SearchCatalog").WithTags("Catalog")
            .Produces<Page<CatalogSearchResultDto>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireAuthorization().IsStub();

        catalog.MapGet("/lookup", ([AsParameters] CatalogLookupQuery query) => StubEndpoints.NotImplemented())
            .WithName("LookupCatalogCode").WithTags("Catalog")
            .Produces<CatalogLookupDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireAuthorization().IsStub();

        catalog.MapGet("/releases/{id:guid}", (Guid id) => StubEndpoints.NotImplemented())
            .WithName("GetRelease").WithTags("Catalog")
            .Produces<ReleaseDto>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireAuthorization().IsStub();

        catalog.MapGet("/releases/{id:guid}/artwork", (Guid id) => StubEndpoints.NotImplemented())
            .WithName("ListReleaseArtwork").WithTags("Catalog")
            .Produces<Page<ArtworkDto>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireAuthorization().IsStub();

        catalog.MapGet("/platforms", () => StubEndpoints.NotImplemented())
            .WithName("ListPlatforms").WithTags("Catalog")
            .Produces<Page<PlatformDto>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireAuthorization().IsStub();

        // Cover image proxy with long cache headers. The web <img> path will need a signed URL (decided when it is built).
        app.MapGet("/media/covers/{assetId:guid}", (Guid assetId) => StubEndpoints.NotImplemented())
            .WithName("GetCover").WithTags("Catalog")
            .Produces(StatusCodes.Status200OK, null, "image/jpeg", "image/png", "image/webp")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireAuthorization().IsStub();

        // Backup and restore of your items' Apple feature prints. Opaque to the server.
        var library = app.MapLibraryGroup();

        library.MapGet("/feature-prints", (Guid libraryId, [AsParameters] FeaturePrintsQuery query) => StubEndpoints.NotImplemented())
            .WithName("ListFeaturePrints").WithTags("Catalog")
            .Produces<Page<FeaturePrintDto>>()
            .RequireLibraryRole(LibraryRole.Viewer).IsStub();

        library.MapPut("/feature-prints", (Guid libraryId, PutFeaturePrintsRequest body) => StubEndpoints.NotImplemented())
            .WithName("PutFeaturePrints").WithTags("Catalog")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .RequireLibraryRole(LibraryRole.Editor).IsStub();

        return app;
    }
}
