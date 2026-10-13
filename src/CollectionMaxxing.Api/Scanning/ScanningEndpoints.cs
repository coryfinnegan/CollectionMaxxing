using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Libraries;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.AspNetCore.Mvc;

namespace CollectionMaxxing.Api.Scanning;

/// <summary>
/// Scan sessions, candidates and the live event stream (docs/04, "Scanning"). A session is created under a library; every
/// other endpoint is addressed by session id, so its library comes from the stored session, never from the caller
/// (<c>RequireScanSessionRole</c>). There are no modes: a viewer can scan to check what the library has, an editor commits.
/// </summary>
public static class ScanningEndpoints
{
    public static IServiceCollection AddScanning(this IServiceCollection services)
    {
        services.AddScoped<FindSessionLibrary.Handler>();
        return services;
    }

    public static IEndpointRouteBuilder MapScanningEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapLibraryGroup()
            .MapPost("/scan-sessions", (Guid libraryId, CreateScanSessionRequest body) => StubEndpoints.NotImplemented())
            .WithName("CreateScanSession").WithTags("Scanning")
            .Produces<CreatedScanSessionDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .RequireLibraryRole(LibraryRole.Viewer).IsStub();

        const string session = "/api/v1/scan-sessions/{id:guid}";
        var sessions = app.MapGroup(session);

        app.MapGet(session, (Guid id, [AsParameters] ScanSessionQuery query) => StubEndpoints.NotImplemented())
            .WithName("GetScanSession").WithTags("Scanning")
            .Produces<ScanSessionDto>()
            .RequireScanSessionRole(LibraryRole.Viewer).IsStub();

        // Server-sent events: candidate.added, candidate.updated, photo.status, session.status. Resume with Last-Event-ID.
        sessions.MapGet("/events", (Guid id) => StubEndpoints.NotImplemented())
            .WithName("StreamScanSessionEvents").WithTags("Scanning")
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .RequireScanSessionRole(LibraryRole.Viewer).IsStub();

        sessions.MapPost(
                "/barcodes",
                (Guid id, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey, BarcodeBatchRequest body) =>
                    StubEndpoints.NotImplemented())
            .WithName("ScanBarcodes").WithTags("Scanning")
            .Produces<CandidatesDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .RequireScanSessionRole(LibraryRole.Viewer).IsStub();

        sessions.MapPost("/identify", (Guid id, IdentifyRequest body) => StubEndpoints.NotImplemented())
            .WithName("IdentifyItems").WithTags("Scanning")
            .Produces<CandidatesDto>()
            .ProducesValidationProblem()
            .RequireScanSessionRole(LibraryRole.Viewer).IsStub();

        sessions.MapPost("/candidates/{cid:guid}:art", (Guid id, Guid cid, ArtDistancesRequest body) => StubEndpoints.NotImplemented())
            .WithName("ScoreCandidateArtwork").WithTags("Scanning")
            .Produces<CandidateDto>()
            .ProducesValidationProblem()
            .RequireScanSessionRole(LibraryRole.Viewer).IsStub();

        // Multipart crop, used as the item's cover ("Use my photo"). Not bound here, so the stub needs no antiforgery.
        sessions.MapPost("/candidates/{cid:guid}/photo", (Guid id, Guid cid) => StubEndpoints.NotImplemented())
            .WithName("UploadCandidatePhoto").WithTags("Scanning")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<CandidateDto>()
            .RequireScanSessionRole(LibraryRole.Editor).IsStub();

        sessions.MapPatch("/candidates/{cid:guid}", (Guid id, Guid cid, DecideCandidateRequest body) => StubEndpoints.NotImplemented())
            .WithName("DecideCandidate").WithTags("Scanning")
            .Produces<CandidateDto>()
            .ProducesValidationProblem()
            .RequireScanSessionRole(LibraryRole.Editor).IsStub();

        sessions.MapPost("/candidates/{cid:guid}:search", (Guid id, Guid cid, SearchCandidateRequest body) => StubEndpoints.NotImplemented())
            .WithName("SearchForCandidate").WithTags("Scanning")
            .Produces<CandidateDto>()
            .ProducesValidationProblem()
            .RequireScanSessionRole(LibraryRole.Editor).IsStub();

        // Creates items for the accepted candidates in one transaction.
        sessions.MapPost(
                "/commit",
                (Guid id, [FromHeader(Name = "Idempotency-Key")] string idempotencyKey) => StubEndpoints.NotImplemented())
            .WithName("CommitScanSession").WithTags("Scanning")
            .Produces<CommitResultDto>()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .RequireScanSessionRole(LibraryRole.Editor).IsStub();

        // Abandon.
        app.MapDelete(session, (Guid id) => StubEndpoints.NotImplemented())
            .WithName("AbandonScanSession").WithTags("Scanning")
            .Produces(StatusCodes.Status204NoContent)
            .RequireScanSessionRole(LibraryRole.Editor).IsStub();

        return app;
    }
}
