using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Libraries;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.AspNetCore.Mvc;

namespace CollectionMaxxing.Api.Scanning;

/// <summary>
/// Scan sessions, candidates and the live event stream (docs/04, "Scanning"). A session is created under a library; every
/// other endpoint is addressed by session id, so its library comes from the stored session, never from the caller
/// (<c>RequireScanSessionRole</c>), and a session is also its starter's own (<see cref="ScanSessionOwnerFilter"/>). There are
/// no modes: a viewer can scan to check what the library has, an editor commits.
/// </summary>
public static class ScanningEndpoints
{
    public static IEndpointRouteBuilder MapScanningEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapLibraryGroup()
            .MapPost("/scan-sessions", CreateSession)
            .WithName("CreateScanSession").WithTags("Scanning")
            .Produces<CreatedScanSessionDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireLibraryRole(LibraryRole.Viewer);

        var sessions = app.MapScanSessionGroup();

        sessions.MapGet("/{id:guid}", GetSession)
            .WithName("GetScanSession").WithTags("Scanning")
            .Produces<ScanSessionDto>()
            .ProducesValidationProblem()
            .RequireScanSessionRole(LibraryRole.Viewer);

        // Server-sent events: candidate.added, candidate.updated, photo.status, session.status. Resume with Last-Event-ID.
        sessions.MapGet("/{id:guid}/events", Events.ScanEventsEndpoint.StreamAsync)
            .WithName("StreamScanSessionEvents").WithTags("Scanning")
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .RequireScanSessionRole(LibraryRole.Viewer);

        sessions.MapPost("/{id:guid}/barcodes", SubmitBarcodes)
            .WithName("ScanBarcodes").WithTags("Scanning")
            .Produces<CandidatesDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .RequireScanSessionRole(LibraryRole.Viewer);

        sessions.MapPost("/{id:guid}/identify", (Guid id, IdentifyRequest body) => StubEndpoints.NotImplemented())
            .WithName("IdentifyItems").WithTags("Scanning")
            .Produces<CandidatesDto>()
            .ProducesValidationProblem()
            .RequireScanSessionRole(LibraryRole.Viewer).IsStub();

        sessions.MapPost("/{id:guid}/candidates/{cid:guid}:art", (Guid id, Guid cid, ArtDistancesRequest body) => StubEndpoints.NotImplemented())
            .WithName("ScoreCandidateArtwork").WithTags("Scanning")
            .Produces<CandidateDto>()
            .ProducesValidationProblem()
            .RequireScanSessionRole(LibraryRole.Viewer).IsStub();

        // Multipart crop, used as the item's cover ("Use my photo"). JPEG only, at most 4 MB; stored without EXIF. Read from the stream
        // by hand (see CandidatePhotoEndpoint), so there is no form binding and no antiforgery token: the usual bearer / X-CSRF rules protect it.
        sessions.MapPost("/{id:guid}/candidates/{cid:guid}/photo", CandidatePhotoEndpoint.UploadAsync)
            .WithName("UploadCandidatePhoto").WithTags("Scanning")
            .Accepts<CandidatePhotoForm>("multipart/form-data")
            .Produces<CandidateDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .RequireScanSessionRole(LibraryRole.Editor);

        sessions.MapPatch("/{id:guid}/candidates/{cid:guid}", DecideCandidate)
            .WithName("DecideCandidate").WithTags("Scanning")
            .Produces<CandidateDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireScanSessionRole(LibraryRole.Editor);

        sessions.MapPost("/{id:guid}/candidates/{cid:guid}:search", SearchForCandidate)
            .WithName("SearchForCandidate").WithTags("Scanning")
            .Produces<CandidateDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireScanSessionRole(LibraryRole.Editor);

        // Creates items for the accepted candidates in one transaction.
        sessions.MapPost("/{id:guid}/commit", Commit)
            .WithName("CommitScanSession").WithTags("Scanning")
            .Produces<CommitResultDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .RequireScanSessionRole(LibraryRole.Editor);

        // Abandon.
        sessions.MapDelete("/{id:guid}", Abandon)
            .WithName("AbandonScanSession").WithTags("Scanning")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireScanSessionRole(LibraryRole.Editor);

        return app;
    }

    private static async Task<IResult> CreateSession(
        Guid libraryId, CreateScanSessionRequest body, CreateScanSession.Handler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new CreateScanSession.Command(libraryId, body.DefaultLocationId, body.DefaultStatus), cancellationToken);
        return result.IsSuccess
            ? TypedResults.Created($"{ScanSessionRoutes.Prefix}/{result.Value.Id}", result.Value)
            : result.Error!.ToScanProblem();
    }

    private static async Task<IResult> GetSession(
        Guid id, [AsParameters] ScanSessionQuery query, GetScanSession.Handler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetScanSession.Query(id, query.Since), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToScanProblem();
    }

    private static async Task<IResult> SubmitBarcodes(
        Guid id,
        [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
        BarcodeBatchRequest body,
        SubmitBarcodes.Handler handler,
        CancellationToken cancellationToken)
    {
        var codes = body.Codes?.Select(c => new SubmittedCode(c?.Value, c?.Symbology, c?.Copies)).ToList();
        var result = await handler.HandleAsync(new SubmitBarcodes.Command(id, idempotencyKey, codes), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToScanProblem();
    }

    private static async Task<IResult> DecideCandidate(
        Guid id, Guid cid, DecideCandidateRequest body, DecideCandidate.Handler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new DecideCandidate.Command(id, cid, body.Decision, body.ReleaseId, body.Copies), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToScanProblem();
    }

    private static async Task<IResult> SearchForCandidate(
        Guid id, Guid cid, SearchCandidateRequest body, SearchForCandidate.Handler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new SearchForCandidate.Command(id, cid, body.Text), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToScanProblem();
    }

    private static async Task<IResult> Commit(
        Guid id, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, CommitScanSession.Handler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new CommitScanSession.Command(id, idempotencyKey), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToScanProblem();
    }

    private static async Task<IResult> Abandon(Guid id, AbandonScanSession.Handler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new AbandonScanSession.Command(id), cancellationToken);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error!.ToScanProblem();
    }

    /// <summary>Like <see cref="ProblemResults.ToProblem"/>, plus 422 for an <c>Idempotency-Key</c> reused with a different request.</summary>
    private static IResult ToScanProblem(this DomainError error) => error.Code == ItemErrors.KeyReusedCode
        ? ProblemResults.Problem(StatusCodes.Status422UnprocessableEntity, "Unprocessable", error.Message, error.Code)
        : error.ToProblem();
}
