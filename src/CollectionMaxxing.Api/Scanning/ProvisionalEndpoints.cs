using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Libraries;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Provisional;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.AspNetCore.Mvc;

namespace CollectionMaxxing.Api.Scanning;

/// <summary>
/// Provisional records: unreadable spines the person kept (design §11, #345). Keeping happens in the session the row was
/// scanned in; everything after that is library-scoped, because the record outlives its session (the 7-day cleanup must not
/// take it). An editor keeps and acts; any member may read the Needs attention list.
/// </summary>
public static class ProvisionalEndpoints
{
    public static IEndpointRouteBuilder MapProvisionalEndpoints(this IEndpointRouteBuilder app)
    {
        var sessions = app.MapScanSessionGroup();

        // A kept row's segment crop (ADR-030): stored sanitized, claimed by keeping the row. Successes are refused.
        sessions.MapPost("/{id:guid}/candidates/{cid:guid}/crop", CropEndpoint.UploadAsync)
            .WithName("UploadCandidateCrop").WithTags("Scanning")
            .Accepts<CandidateCropForm>("multipart/form-data")
            .Produces<CropUploadDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .RequireScanSessionRole(LibraryRole.Editor);

        // "Keep as unverified": the row retires and its provisional record is born.
        sessions.MapPost("/{id:guid}/provisionals", Keep)
            .WithName("CreateProvisional").WithTags("Scanning")
            .Produces<CreateProvisionalResultDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireScanSessionRole(LibraryRole.Editor);

        var library = app.MapLibraryGroup();

        library.MapGet("/provisionals", List)
            .WithName("ListProvisionals").WithTags("Scanning")
            .Produces<ProvisionalsDto>()
            .ProducesValidationProblem()
            .RequireLibraryRole(LibraryRole.Viewer);

        library.MapPost("/provisionals/{id:guid}:promote", Promote)
            .WithName("PromoteProvisional").WithTags("Scanning")
            .Produces<ProvisionalDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .RequireLibraryRole(LibraryRole.Editor);

        library.MapPost("/provisionals/{id:guid}:resolve", Resolve)
            .WithName("ResolveProvisional").WithTags("Scanning")
            .Produces<ProvisionalDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireLibraryRole(LibraryRole.Editor);

        library.MapDelete("/provisionals/{id:guid}", Delete)
            .WithName("DeleteProvisional").WithTags("Scanning")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireLibraryRole(LibraryRole.Editor);

        return app;
    }

    private static async Task<IResult> Keep(
        Guid id, CreateProvisionalRequest body, CreateProvisional.Handler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new CreateProvisional.Command(id, body.CandidateId, body.CropAssetId), cancellationToken);
        return result.IsSuccess
            ? TypedResults.Created($"{ScanSessionRoutes.Prefix}/{id}/provisionals/{result.Value.Provisional.Id}", result.Value)
            : result.Error!.ToProblem();
    }

    private static async Task<IResult> List(
        Guid libraryId, ListProvisionals.Handler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ListProvisionals.Query(libraryId), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToProblem();
    }

    private static async Task<IResult> Promote(
        Guid libraryId,
        Guid id,
        [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
        PromoteProvisional.Handler handler,
        CancellationToken cancellationToken,
        PromoteProvisionalRequest? body = null)
    {
        var result = await handler.HandleAsync(
            new PromoteProvisional.Command(libraryId, id, idempotencyKey, body?.KeepCrop ?? true), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToProblem();
    }

    private static async Task<IResult> Resolve(
        Guid libraryId, Guid id, ResolveProvisionalRequest body, ResolveProvisional.Handler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ResolveProvisional.Command(libraryId, id, body.ItemId), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToProblem();
    }

    private static async Task<IResult> Delete(
        Guid libraryId, Guid id, DeleteProvisional.Handler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new DeleteProvisional.Command(libraryId, id), cancellationToken);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error!.ToProblem();
    }
}
