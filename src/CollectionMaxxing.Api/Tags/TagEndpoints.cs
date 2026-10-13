using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Libraries;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Tags;
using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Api.Tags;

/// <summary>Tags (docs/04, "Locations, tags, import/export"). Locations are in <c>LocationEndpoints</c>.</summary>
public static class TagEndpoints
{
    public static IServiceCollection AddTags(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapTagEndpoints(this IEndpointRouteBuilder app)
    {
        var library = app.MapLibraryGroup();

        library.MapGet("/tags", (Guid libraryId) => StubEndpoints.NotImplemented())
            .WithName("ListTags").WithTags("Tags")
            .Produces<Page<TagDto>>()
            .RequireLibraryRole(LibraryRole.Viewer).IsStub();

        library.MapPost("/tags", (Guid libraryId, CreateTagRequest body) => StubEndpoints.NotImplemented())
            .WithName("CreateTag").WithTags("Tags")
            .Produces<TagDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireLibraryRole(LibraryRole.Editor).IsStub();

        library.MapDelete("/tags/{id:guid}", (Guid libraryId, Guid id) => StubEndpoints.NotImplemented())
            .WithName("DeleteTag").WithTags("Tags")
            .Produces(StatusCodes.Status204NoContent)
            .RequireLibraryRole(LibraryRole.Editor).IsStub();

        return app;
    }
}
