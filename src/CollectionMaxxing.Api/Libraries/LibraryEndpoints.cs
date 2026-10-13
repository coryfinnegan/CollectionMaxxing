using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Libraries;
using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Api.Libraries;

/// <summary>Libraries and their members (docs/04, "Libraries and members"). Invites are in <c>InviteEndpoints</c>.</summary>
public static class LibraryEndpoints
{
    public static IServiceCollection AddLibraries(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapLibraryEndpoints(this IEndpointRouteBuilder app)
    {
        // The caller's own libraries: no library in the route, so just "signed in".
        app.MapGet("/api/v1/libraries", () => StubEndpoints.NotImplemented())
            .WithName("ListLibraries").WithTags("Libraries")
            .Produces<Page<LibrarySummaryDto>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireAuthorization().IsStub();

        var library = app.MapLibraryGroup();

        app.MapGet(LibraryRoutes.Prefix, (Guid libraryId) => StubEndpoints.NotImplemented())
            .WithName("GetLibrary").WithTags("Libraries")
            .Produces<LibraryDto>()
            .RequireLibraryRole(LibraryRole.Viewer).IsStub();

        app.MapPatch(LibraryRoutes.Prefix, (Guid libraryId, UpdateLibraryRequest body) => StubEndpoints.NotImplemented())
            .WithName("UpdateLibrary").WithTags("Libraries")
            .Produces<LibraryDto>()
            .ProducesValidationProblem()
            .RequireLibraryRole(LibraryRole.Owner).IsStub();

        library.MapGet("/members", (Guid libraryId) => StubEndpoints.NotImplemented())
            .WithName("ListMembers").WithTags("Libraries")
            .Produces<Page<MemberDto>>()
            .RequireLibraryRole(LibraryRole.Viewer).IsStub();

        library.MapPatch("/members/{userId:guid}", (Guid libraryId, Guid userId, UpdateMemberRequest body) => StubEndpoints.NotImplemented())
            .WithName("UpdateMember").WithTags("Libraries")
            .Produces<MemberDto>()
            .ProducesValidationProblem()
            .RequireLibraryRole(LibraryRole.Owner).IsStub();

        // Also revokes that member's devices and sessions. The owner can't be removed.
        library.MapDelete("/members/{userId:guid}", (Guid libraryId, Guid userId) => StubEndpoints.NotImplemented())
            .WithName("RemoveMember").WithTags("Libraries")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireLibraryRole(LibraryRole.Owner).IsStub();

        return app;
    }
}
