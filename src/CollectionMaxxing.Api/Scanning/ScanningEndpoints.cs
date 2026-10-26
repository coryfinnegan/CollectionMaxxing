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

        // Server-sent events: candidate.added, candidate.updated, candidate.auto_committed, photo.status, session.counters, session.status. Resume with Last-Event-ID.
        sessions.MapGet("/{id:guid}/events", Events.ScanEventsEndpoint.StreamAsync)
            .WithName("StreamScanSessionEvents").WithTags("Scanning")
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .RequireScanSessionRole(LibraryRole.Viewer)
            .DisableRequestTimeout(); // a live stream is meant to stay open

        sessions.MapPost("/{id:guid}/barcodes", SubmitBarcodes)
            .WithName("ScanBarcodes").WithTags("Scanning")
            .Produces<CandidatesDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .RequireScanSessionRole(LibraryRole.Viewer);

        // What the phone read from one picture (text only, never an image). The body is read inside the handler, after the
        // session filters, and bounded; see IdentifyEndpoint.
        sessions.MapPost("/{id:guid}/identify", Identify.IdentifyEndpoint.HandleAsync)
            .WithName("IdentifyItems").WithTags("Scanning")
            .Accepts<IdentifyRequest>("application/json")
            .Produces<CandidatesDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .RequireScanSessionRole(LibraryRole.Viewer);

        // The feature-print distances the phone measured for a close call; the server re-scores. The body is read inside the
        // handler, after the session filters, and bounded; see ArtworkEndpoint.
        sessions.MapPost("/{id:guid}/candidates/{cid:guid}:art", Artwork.ArtworkEndpoint.HandleAsync)
            .WithName("ScoreCandidateArtwork").WithTags("Scanning")
            .Accepts<ArtDistancesRequest>("application/json")
            .Produces<CandidateDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .RequireScanSessionRole(LibraryRole.Viewer);

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
            .RequireScanSessionRole(LibraryRole.Editor)
            .WithRequestTimeout(Security.SecurityHeaders.UploadTimeoutPolicy); // 4 MB on a slow link takes longer than the usual minute

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
            .Accepts<CommitScanRequest>(isOptional: true, "application/json")
            .Produces<CommitResultDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .RequireScanSessionRole(LibraryRole.Editor);

        // Takes back a commit made moments ago: the new items are removed for good and Review gets its results back (SCAN-1e).
        sessions.MapPost("/{id:guid}/commit:undo", UndoCommit)
            .WithName("UndoScanCommit").WithTags("Scanning")
            .Produces<UndoResultDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .RequireScanSessionRole(LibraryRole.Editor);

        // Takes back one T0 auto-add: exactly its items go, the candidate gets its evidence back, and the session stays open.
        sessions.MapPost("/{id:guid}/auto-commit:undo", UndoAutoCommit)
            .WithName("UndoAutoCommit").WithTags("Scanning")
            .Accepts<UndoAutoCommitRequest>(isOptional: true, "application/json")
            .Produces<UndoAutoCommitResultDto>()
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
        var result = await handler.HandleAsync(
            new CreateScanSession.Command(
                libraryId, body.DefaultLocationId, body.DefaultStatus, body.MediaHint, body.Channel, body.AutoCommitTiers, body.AutoCommitCopies),
            cancellationToken);
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
        var result = await handler.HandleAsync(new SubmitBarcodes.Command(id, idempotencyKey, codes, body.CandidateId), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToScanProblem();
    }

    private static async Task<IResult> DecideCandidate(
        Guid id, Guid cid, DecideCandidateRequest body, DecideCandidate.Handler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new DecideCandidate.Command(id, cid, body.Decision, body.ReleaseId, body.Copies, body.Format, body.Platform, body.Edition), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToScanProblem();
    }

    private static async Task<IResult> SearchForCandidate(
        Guid id, Guid cid, SearchCandidateRequest body, SearchForCandidate.Handler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new SearchForCandidate.Command(id, cid, body.Text, body.ContextWorkIds), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToScanProblem();
    }

    private static async Task<IResult> Commit(
        Guid id,
        [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
        CommitScanSession.Handler handler,
        CancellationToken cancellationToken,
        CommitScanRequest? body = null)
    {
        var options = body is null
            ? null
            : new CommitOptions(
                body.AddTo, body.Status, body.Condition,
                body.Overrides?.Select(o => new CommitOverride(o?.CandidateId, o?.LocationId, o?.Status, o?.Condition, o?.Copies)).ToList());
        var result = await handler.HandleAsync(new CommitScanSession.Command(id, idempotencyKey, options), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToScanProblem();
    }

    private static async Task<IResult> UndoCommit(
        Guid id,
        [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
        UndoScanCommit.Handler handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new UndoScanCommit.Command(id, idempotencyKey), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToScanProblem();
    }

    private static async Task<IResult> UndoAutoCommit(
        Guid id,
        [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
        UndoAutoCommit.Handler handler,
        CancellationToken cancellationToken,
        UndoAutoCommitRequest? body = null)
    {
        var result = await handler.HandleAsync(new UndoAutoCommit.Command(id, body?.CandidateId, idempotencyKey), cancellationToken);
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
