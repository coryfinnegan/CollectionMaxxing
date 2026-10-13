using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Libraries;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Tags;
using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Api.Tags;

/// <summary>Tags (docs/04, "Locations, tags, import/export"). Copies the shape of <c>LocationEndpoints</c>.</summary>
public static class TagEndpoints
{
    /// <summary>Registers this feature's handlers. Called from <c>Program.cs</c>.</summary>
    public static IServiceCollection AddTags(this IServiceCollection services)
    {
        services.AddScoped<ListTags.Handler>();
        services.AddScoped<CreateTag.Handler>();
        services.AddScoped<DeleteTag.Handler>();
        return services;
    }

    /// <summary>Maps this feature's routes. Called from <c>Program.cs</c>.</summary>
    public static IEndpointRouteBuilder MapTagEndpoints(this IEndpointRouteBuilder app)
    {
        var library = app.MapLibraryGroup();

        library.MapGet("/tags", List)
            .WithName("ListTags").WithTags("Tags")
            .Produces<Page<TagDto>>()
            .RequireLibraryRole(LibraryRole.Viewer);

        library.MapPost("/tags", Create)
            .WithName("CreateTag").WithTags("Tags")
            .Produces<TagDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireLibraryRole(LibraryRole.Editor);

        library.MapDelete("/tags/{id:guid}", Delete)
            .WithName("DeleteTag").WithTags("Tags")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireLibraryRole(LibraryRole.Editor);

        return app;
    }

    private static async Task<IResult> List(Guid libraryId, ListTags.Handler handler, CancellationToken cancellationToken) =>
        TypedResults.Ok(await handler.HandleAsync(new ListTags.Query(libraryId), cancellationToken));

    private static async Task<IResult> Create(
        Guid libraryId, CreateTagRequest body, CreateTag.Handler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new CreateTag.Command(libraryId, body.Name), cancellationToken);

        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/libraries/{libraryId}/tags/{result.Value.Id}", result.Value)
            : result.Error!.ToProblem();
    }

    private static async Task<IResult> Delete(
        Guid libraryId, Guid id, DeleteTag.Handler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new DeleteTag.Command(libraryId, id), cancellationToken);

        return result.IsSuccess ? TypedResults.NoContent() : result.Error!.ToProblem();
    }
}
