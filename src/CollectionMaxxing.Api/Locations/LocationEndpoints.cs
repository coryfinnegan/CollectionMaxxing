using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Libraries;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Locations;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Locations;

namespace CollectionMaxxing.Api.Locations;

/// <summary>
/// THE REFERENCE SLICE for endpoints. Copy its shape: a route group, one small method per endpoint that maps
/// the request to a command, calls one application handler and maps the result to HTTP. No business rules here.
/// </summary>
public static class LocationEndpoints
{
    /// <summary>Registers this feature's handlers. Called from <c>Program.cs</c>.</summary>
    public static IServiceCollection AddLocations(this IServiceCollection services)
    {
        services.AddScoped<ListLocations.Handler>();
        services.AddScoped<CreateLocation.Handler>();
        services.AddScoped<UpdateLocation.Handler>();
        services.AddScoped<DeleteLocation.Handler>();
        return services;
    }

    /// <summary>Maps this feature's routes. Called from <c>Program.cs</c>.</summary>
    public static IEndpointRouteBuilder MapLocationEndpoints(this IEndpointRouteBuilder app)
    {
        var library = app.MapLibraryGroup();

        library.MapGet("/locations", List)
            .WithName("ListLocations").WithTags("Locations")
            .Produces<Page<LocationDto>>()
            .RequireLibraryRole(LibraryRole.Viewer);

        library.MapPost("/locations", Create)
            .WithName("CreateLocation").WithTags("Locations")
            .Produces<LocationDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireLibraryRole(LibraryRole.Editor);

        library.MapPatch("/locations/{id:guid}", Update)
            .WithName("UpdateLocation").WithTags("Locations")
            .Produces<LocationDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireLibraryRole(LibraryRole.Editor);

        library.MapDelete("/locations/{id:guid}", Delete)
            .WithName("DeleteLocation").WithTags("Locations")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireLibraryRole(LibraryRole.Editor);

        return app;
    }

    private static async Task<IResult> List(Guid libraryId, ListLocations.Handler handler, CancellationToken cancellationToken) =>
        TypedResults.Ok(await handler.HandleAsync(new ListLocations.Query(libraryId), cancellationToken));

    private static async Task<IResult> Create(
        Guid libraryId, CreateLocationRequest body, CreateLocation.Handler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new CreateLocation.Command(libraryId, body.ParentId, body.Name, body.SortOrder), cancellationToken);

        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/libraries/{libraryId}/locations/{result.Value.Id}", result.Value)
            : result.Error!.ToProblem();
    }

    private static async Task<IResult> Update(
        Guid libraryId, Guid id, UpdateLocationRequest body, UpdateLocation.Handler handler, CancellationToken cancellationToken)
    {
        var patch = new LocationPatch(body.Name, body.ParentIdSpecified, body.ParentId, body.SortOrder);
        var result = await handler.HandleAsync(new UpdateLocation.Command(libraryId, id, patch), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToProblem();
    }

    private static async Task<IResult> Delete(
        Guid libraryId, Guid id, DeleteLocation.Handler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new DeleteLocation.Command(libraryId, id), cancellationToken);

        return result.IsSuccess ? TypedResults.NoContent() : result.Error!.ToProblem();
    }
}
