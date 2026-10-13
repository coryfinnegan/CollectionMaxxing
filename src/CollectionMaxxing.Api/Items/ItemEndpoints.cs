using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Libraries;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.AspNetCore.Mvc;

namespace CollectionMaxxing.Api.Items;

/// <summary>
/// Items (your copies of releases) and the library's stats (docs/04, "Items"). Conventions that apply here:
/// <c>ETag</c> on GET, <c>If-Match</c> required on PATCH and DELETE (412 on mismatch), and an <c>Idempotency-Key</c>
/// required on create (a replay returns the stored response; a different body with the same key is 422).
/// </summary>
public static class ItemEndpoints
{
    public static IServiceCollection AddItems(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapItemEndpoints(this IEndpointRouteBuilder app)
    {
        var library = app.MapLibraryGroup();

        library.MapGet("/items", (Guid libraryId, [AsParameters] ListItemsQuery query) => StubEndpoints.NotImplemented())
            .WithName("ListItems").WithTags("Items")
            .Produces<Page<ItemSummaryDto>>()
            .ProducesValidationProblem()
            .RequireLibraryRole(LibraryRole.Viewer).IsStub();

        library.MapPost(
                "/items",
                (Guid libraryId, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, CreateItemRequest body) =>
                    StubEndpoints.NotImplemented())
            .WithName("CreateItem").WithTags("Items")
            .Produces<ItemDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .RequireLibraryRole(LibraryRole.Editor).IsStub();

        library.MapGet("/items/{id:guid}", (Guid libraryId, Guid id) => StubEndpoints.NotImplemented())
            .WithName("GetItem").WithTags("Items")
            .Produces<ItemDetailDto>()
            .RequireLibraryRole(LibraryRole.Viewer).IsStub();

        library.MapPatch(
                "/items/{id:guid}",
                (Guid libraryId, Guid id, [FromHeader(Name = "If-Match")] string ifMatch, UpdateItemRequest body) =>
                    StubEndpoints.NotImplemented())
            .WithName("UpdateItem").WithTags("Items")
            .Produces<ItemDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .RequireLibraryRole(LibraryRole.Editor).IsStub();

        // Soft delete: the item sits in "Recently deleted" for 30 days.
        library.MapDelete(
                "/items/{id:guid}",
                (Guid libraryId, Guid id, [FromHeader(Name = "If-Match")] string ifMatch) => StubEndpoints.NotImplemented())
            .WithName("DeleteItem").WithTags("Items")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .RequireLibraryRole(LibraryRole.Editor).IsStub();

        library.MapPost("/items/{id:guid}:restore", (Guid libraryId, Guid id) => StubEndpoints.NotImplemented())
            .WithName("RestoreItem").WithTags("Items")
            .Produces<ItemDto>()
            .RequireLibraryRole(LibraryRole.Editor).IsStub();

        // Swap the release, keep the copy data.
        library.MapPost("/items/{id:guid}:rematch", (Guid libraryId, Guid id, RematchItemRequest body) => StubEndpoints.NotImplemented())
            .WithName("RematchItem").WithTags("Items")
            .Produces<ItemDto>()
            .ProducesValidationProblem()
            .RequireLibraryRole(LibraryRole.Editor).IsStub();

        library.MapPost("/items:bulk", (Guid libraryId, BulkItemsRequest body) => StubEndpoints.NotImplemented())
            .WithName("BulkItems").WithTags("Items")
            .Produces<BulkItemsResultDto>()
            .ProducesValidationProblem()
            .RequireLibraryRole(LibraryRole.Editor).IsStub();

        library.MapGet("/stats", (Guid libraryId) => StubEndpoints.NotImplemented())
            .WithName("GetStats").WithTags("Items")
            .Produces<StatsDto>()
            .RequireLibraryRole(LibraryRole.Viewer).IsStub();

        return app;
    }
}
