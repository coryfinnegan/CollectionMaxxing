using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Libraries;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Api.Security;
using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Catalog.FeaturePrints;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Api.Catalog;

/// <summary>
/// The shared catalogue, cover images and the feature-print backup (docs/04, "Catalogue"). Catalogue reads need a signed-in
/// caller but no library; releases a library created for itself are visible to its members only (the data filter).
/// </summary>
public static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var catalog = app.MapGroup("/api/v1/catalog");

        // Local catalogue first, then providers; merged and scored.
        catalog.MapGet("/search", Search)
            .WithName("SearchCatalog").WithTags("Catalog")
            .Produces<Page<CatalogSearchResultDto>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireAuthorization()
            .WithRateLimitGroup(RateLimitGroup.Search);

        catalog.MapGet("/lookup", Lookup)
            .WithName("LookupCatalogCode").WithTags("Catalog")
            .Produces<CatalogLookupDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireAuthorization()
            .WithRateLimitGroup(RateLimitGroup.Search);

        catalog.MapGet("/releases/{id:guid}", Release)
            .WithName("GetRelease").WithTags("Catalog")
            .Produces<ReleaseDto>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireAuthorization();

        catalog.MapGet("/releases/{id:guid}/artwork", Artwork)
            .WithName("ListReleaseArtwork").WithTags("Catalog")
            .Produces<Page<ArtworkDto>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireAuthorization();

        catalog.MapGet("/platforms", Platforms)
            .WithName("ListPlatforms").WithTags("Catalog")
            .Produces<Page<PlatformDto>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireAuthorization();

        // Cover image proxy with long cache headers. The web <img> path will need a signed URL (decided when it is built).
        app.MapGet("/media/covers/{assetId:guid}", Cover)
            .WithName("GetCover").WithTags("Catalog")
            .Produces(StatusCodes.Status200OK, null, "image/jpeg", "image/png", "image/webp")
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireAuthorization();

        // Backup and restore of your items' Apple feature prints. Opaque to the server.
        var library = app.MapLibraryGroup();

        library.MapGet("/feature-prints", ListFeaturePrintsAsync)
            .WithName("ListFeaturePrints").WithTags("Catalog")
            .Produces<Page<FeaturePrintDto>>()
            .ProducesValidationProblem()
            .RequireLibraryRole(LibraryRole.Viewer);

        // Up to 200 prints of at most 16 KB each, all or nothing. An item or release that isn't this library's is a 404.
        library.MapPut("/feature-prints", PutFeaturePrintsAsync)
            .WithName("PutFeaturePrints").WithTags("Catalog")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireLibraryRole(LibraryRole.Editor);

        return app;
    }

    private static async Task<IResult> Search(
        [AsParameters] CatalogSearchQuery query, SearchCatalog.Handler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new SearchCatalog.Query(query.Q, query.Kind, query.Platform, query.Year, query.Limit, query.Scope), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToProblem();
    }

    private static async Task<IResult> Lookup(
        [AsParameters] CatalogLookupQuery query, LookupCatalog.Handler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new LookupCatalog.Query(query.Code), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToProblem();
    }

    private static async Task<IResult> Release(Guid id, GetRelease.Handler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetRelease.Query(id), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToProblem();
    }

    private static async Task<IResult> Artwork(Guid id, ListReleaseArtwork.Handler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ListReleaseArtwork.Query(id), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToProblem();
    }

    private static async Task<IResult> Platforms(ListPlatforms.Handler handler, CancellationToken cancellationToken) =>
        TypedResults.Ok(await handler.HandleAsync(cancellationToken));

    private static async Task<IResult> ListFeaturePrintsAsync(
        Guid libraryId, [AsParameters] FeaturePrintsQuery query, ListFeaturePrints.Handler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ListFeaturePrints.Query(libraryId, query.Since, query.Limit, query.Cursor), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToProblem();
    }

    private static async Task<IResult> PutFeaturePrintsAsync(
        Guid libraryId, PutFeaturePrintsRequest body, PutFeaturePrints.Handler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new PutFeaturePrints.Command(libraryId, body.Prints), cancellationToken);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error!.ToProblem();
    }

    private static async Task<IResult> Cover(
        Guid assetId, HttpContext http, GetCover.Handler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetCover.Query(assetId), cancellationToken);
        if (!result.IsSuccess)
        {
            return result.Error!.ToProblem();
        }

        var cover = result.Value;
        var headers = http.Response.Headers;
        headers.XContentTypeOptions = "nosniff";

        // A global image is the same for every signed-in user; a library's own image must never sit in a shared cache.
        headers.CacheControl = cover.IsPrivate ? "private, max-age=86400" : "public, max-age=31536000, immutable";
        headers.ETag = cover.ETag;

        if (http.Request.Headers.IfNoneMatch.Contains(cover.ETag))
        {
            await cover.Content.DisposeAsync();
            return TypedResults.StatusCode(StatusCodes.Status304NotModified);
        }

        return TypedResults.Stream(cover.Content, cover.ContentType);
    }
}
