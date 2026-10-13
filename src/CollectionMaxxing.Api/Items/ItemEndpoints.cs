using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Libraries;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace CollectionMaxxing.Api.Items;

/// <summary>
/// Items (your copies of releases) and the library's stats (docs/04, "Items"). Conventions that apply here:
/// <c>ETag</c> on GET, <c>If-Match</c> required on PATCH and DELETE (412 on mismatch), and an <c>Idempotency-Key</c>
/// required on create (a replay returns the stored response; a different body with the same key is 422).
/// </summary>
public static class ItemEndpoints
{
    public static IServiceCollection AddItems(this IServiceCollection services)
    {
        services.AddScoped<ListItems.Handler>();
        services.AddScoped<CreateItem.Handler>();
        services.AddScoped<GetItem.Handler>();
        services.AddScoped<UpdateItem.Handler>();
        services.AddScoped<DeleteItem.Handler>();
        services.AddScoped<RestoreItem.Handler>();
        services.AddScoped<RematchItem.Handler>();
        services.AddScoped<BulkItems.Handler>();
        services.AddScoped<GetStats.Handler>();
        return services;
    }

    public static IEndpointRouteBuilder MapItemEndpoints(this IEndpointRouteBuilder app)
    {
        var library = app.MapLibraryGroup();

        library.MapGet("/items", List)
            .WithName("ListItems").WithTags("Items")
            .Produces<Page<ItemSummaryDto>>()
            .ProducesValidationProblem()
            .RequireLibraryRole(LibraryRole.Viewer);

        library.MapPost("/items", Create)
            .WithName("CreateItem").WithTags("Items")
            .Produces<ItemDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .RequireLibraryRole(LibraryRole.Editor);

        library.MapGet("/items/{id:guid}", Get)
            .WithName("GetItem").WithTags("Items")
            .Produces<ItemDetailDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireLibraryRole(LibraryRole.Viewer);

        library.MapPatch("/items/{id:guid}", Update)
            .WithName("UpdateItem").WithTags("Items")
            .Produces<ItemDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .RequireLibraryRole(LibraryRole.Editor);

        // Soft delete: the item sits in "Recently deleted" for 30 days.
        library.MapDelete("/items/{id:guid}", Delete)
            .WithName("DeleteItem").WithTags("Items")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .RequireLibraryRole(LibraryRole.Editor);

        library.MapPost("/items/{id:guid}:restore", Restore)
            .WithName("RestoreItem").WithTags("Items")
            .Produces<ItemDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireLibraryRole(LibraryRole.Editor);

        // Swap the release, keep the copy data.
        library.MapPost("/items/{id:guid}:rematch", Rematch)
            .WithName("RematchItem").WithTags("Items")
            .Produces<ItemDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireLibraryRole(LibraryRole.Editor);

        library.MapPost("/items:bulk", Bulk)
            .WithName("BulkItems").WithTags("Items")
            .Produces<BulkItemsResultDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireLibraryRole(LibraryRole.Editor);

        library.MapGet("/stats", Stats)
            .WithName("GetStats").WithTags("Items")
            .Produces<StatsDto>()
            .RequireLibraryRole(LibraryRole.Viewer);

        return app;
    }

    private static async Task<IResult> List(
        Guid libraryId, [AsParameters] ListItemsQuery query, ListItems.Handler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new ListItems.Query(
                libraryId, query.Q, query.Kind, query.Format, query.Platform, query.Status, query.LocationId, query.Tag,
                query.Sort, query.Limit, query.Cursor),
            cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToItemProblem();
    }

    private static async Task<IResult> Create(
        Guid libraryId,
        [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
        CreateItemRequest body,
        CreateItem.Handler handler,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var manual = body.Manual is { } m
            ? new ManualRelease(m.Kind, m.Title, m.Year, m.Format, m.PlatformId, m.Creators, m.Barcode)
            : null;
        var result = await handler.HandleAsync(
            new CreateItem.Command(
                libraryId, idempotencyKey, body.ReleaseId, manual, body.Status, body.Condition, body.Completeness, body.LocationId,
                body.Notes, body.AcquiredOn, body.AddedVia, body.TagIds),
            cancellationToken);
        if (!result.IsSuccess)
        {
            return result.Error!.ToItemProblem();
        }

        SetETag(http, result.Value.Version);
        return TypedResults.Created($"/api/v1/libraries/{libraryId}/items/{result.Value.Value.Id}", result.Value.Value);
    }

    private static async Task<IResult> Get(
        Guid libraryId, Guid id, GetItem.Handler handler, HttpContext http, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetItem.Query(libraryId, id), cancellationToken);
        if (!result.IsSuccess)
        {
            return result.Error!.ToItemProblem();
        }

        SetETag(http, result.Value.Version);
        return TypedResults.Ok(result.Value.Value);
    }

    private static async Task<IResult> Update(
        Guid libraryId,
        Guid id,
        [FromHeader(Name = "If-Match")] string ifMatch,
        UpdateItemRequest body,
        UpdateItem.Handler handler,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new UpdateItem.Command(libraryId, id, ifMatch, body.ToPatch()), cancellationToken);
        if (!result.IsSuccess)
        {
            return result.Error!.ToItemProblem();
        }

        SetETag(http, result.Value.Version);
        return TypedResults.Ok(result.Value.Value);
    }

    private static async Task<IResult> Delete(
        Guid libraryId, Guid id, [FromHeader(Name = "If-Match")] string ifMatch, DeleteItem.Handler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new DeleteItem.Command(libraryId, id, ifMatch), cancellationToken);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error!.ToItemProblem();
    }

    private static async Task<IResult> Restore(
        Guid libraryId, Guid id, RestoreItem.Handler handler, HttpContext http, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new RestoreItem.Command(libraryId, id), cancellationToken);
        if (!result.IsSuccess)
        {
            return result.Error!.ToItemProblem();
        }

        SetETag(http, result.Value.Version);
        return TypedResults.Ok(result.Value.Value);
    }

    private static async Task<IResult> Rematch(
        Guid libraryId, Guid id, RematchItemRequest body, RematchItem.Handler handler, HttpContext http, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new RematchItem.Command(libraryId, id, body.ReleaseId), cancellationToken);
        if (!result.IsSuccess)
        {
            return result.Error!.ToItemProblem();
        }

        SetETag(http, result.Value.Version);
        return TypedResults.Ok(result.Value.Value);
    }

    private static async Task<IResult> Bulk(Guid libraryId, BulkItemsRequest body, BulkItems.Handler handler, CancellationToken cancellationToken)
    {
        var operation = body.Action switch
        {
            BulkAction.Patch => BulkOperation.Patch,
            BulkAction.Delete => BulkOperation.Delete,
            _ => (BulkOperation?)null,
        };
        var patch = body.Patch is { } p ? new BulkChange(p.LocationId, p.Status, p.AddTagIds, p.RemoveTagIds) : null;
        var result = await handler.HandleAsync(new BulkItems.Command(libraryId, body.Ids, operation, patch), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToItemProblem();
    }

    private static async Task<IResult> Stats(Guid libraryId, GetStats.Handler handler, CancellationToken cancellationToken) =>
        TypedResults.Ok(await handler.HandleAsync(new GetStats.Query(libraryId), cancellationToken));

    private static void SetETag(HttpContext http, uint version) =>
        http.Response.Headers[HeaderNames.ETag] = $"\"{version}\"";

    /// <summary>Like <see cref="ProblemResults.ToProblem"/>, plus the two statuses items add: 412 and 422.</summary>
    private static IResult ToItemProblem(this DomainError error) => error.Code switch
    {
        ItemErrors.PreconditionFailedCode => ProblemResults.Problem(
            StatusCodes.Status412PreconditionFailed, "Precondition failed", error.Message, error.Code),
        ItemErrors.KeyReusedCode => ProblemResults.Problem(
            StatusCodes.Status422UnprocessableEntity, "Unprocessable", error.Message, error.Code),
        _ => error.ToProblem(),
    };
}
