using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Libraries;
using CollectionMaxxing.Application.Libraries.Members;
using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Api.Libraries;

/// <summary>Libraries and their members (docs/04 "Libraries and members", docs/15). Invites are in <c>InviteEndpoints</c>.</summary>
public static class LibraryEndpoints
{
    public static IServiceCollection AddLibraries(this IServiceCollection services) => LibraryServices.Add(services);

    public static IEndpointRouteBuilder MapLibraryEndpoints(this IEndpointRouteBuilder app)
    {
        // The caller's own libraries: no library in the route, so just "signed in". Empty for a new account.
        app.MapGet("/api/v1/libraries", List)
            .WithName("ListLibraries").WithTags("Libraries")
            .Produces<Page<LibrarySummaryDto>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireAuthorization();

        // Anyone signed in may create a library and becomes its only owner, up to Libraries:MaxOwnedPerUser (docs/15). Limited per client
        // and per account.
        app.MapPost("/api/v1/libraries", async (CreateLibraryRequest body, CreateLibrary.Handler handler, CancellationToken cancellationToken) =>
            {
                var result = await handler.HandleAsync(body.Name, cancellationToken);
                return result.IsSuccess
                    ? Results.Created($"/api/v1/libraries/{result.Value.Id}", result.Value)
                    : result.Error!.ToProblem();
            })
            .WithName("CreateLibrary").WithTags("Libraries")
            .Produces<LibraryDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .RequireAuthorization().RequirePerClientAndAccountRateLimit();

        var library = app.MapLibraryGroup();

        app.MapGet(LibraryRoutes.Prefix, Get)
            .WithName("GetLibrary").WithTags("Libraries")
            .Produces<LibraryDto>()
            .RequireLibraryRole(LibraryRole.Viewer);

        app.MapPatch(LibraryRoutes.Prefix, Update)
            .WithName("UpdateLibrary").WithTags("Libraries")
            .Produces<LibraryDto>()
            .ProducesValidationProblem()
            .RequireLibraryRole(LibraryRole.Owner);

        // Names and roles for every member; the owner also gets each member's devices and when they were last seen (ID-3a).
        library.MapGet("/members", async (Guid libraryId, ListMembers.Handler handler, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handler.HandleAsync(libraryId, cancellationToken)))
            .WithName("ListMembers").WithTags("Libraries")
            .Produces<Page<MemberDto>>()
            .RequireLibraryRole(LibraryRole.Viewer);

        // A role change takes effect on the member's next request: roles are read from the database on every request.
        library.MapPatch("/members/{userId:guid}", async (Guid libraryId, Guid userId, UpdateMemberRequest body, UpdateMember.Handler handler, CancellationToken cancellationToken) =>
            {
                var result = await handler.HandleAsync(new UpdateMember.Command(libraryId, userId, body.Role), cancellationToken);
                return result.IsSuccess ? Results.Ok(result.Value) : result.Error!.ToProblem();
            })
            .WithName("UpdateMember").WithTags("Libraries")
            .Produces<MemberDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireLibraryRole(LibraryRole.Owner);

        // Ends that membership only, from the member's next request; their account and other libraries stay. The owner can't be removed.
        library.MapDelete("/members/{userId:guid}", async (Guid libraryId, Guid userId, RemoveMember.Handler handler, CancellationToken cancellationToken) =>
            {
                var result = await handler.HandleAsync(new RemoveMember.Command(libraryId, userId), cancellationToken);
                return result.IsSuccess ? Results.NoContent() : result.Error!.ToProblem();
            })
            .WithName("RemoveMember").WithTags("Libraries")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireLibraryRole(LibraryRole.Owner);

        // Leave a library: any member, except its owner (409 library.last_owner). The account and its other libraries stay.
        library.MapDelete("/membership", async (Guid libraryId, LeaveLibrary.Handler handler, CancellationToken cancellationToken) =>
            {
                var result = await handler.HandleAsync(libraryId, cancellationToken);
                return result.IsSuccess ? Results.NoContent() : result.Error!.ToProblem();
            })
            .WithName("LeaveLibrary").WithTags("Libraries")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireLibraryRole(LibraryRole.Viewer);

        return app;
    }

    private static async Task<IResult> List(ListLibraries.Handler handler, CancellationToken cancellationToken) =>
        TypedResults.Ok(await handler.HandleAsync(cancellationToken));

    private static async Task<IResult> Get(Guid libraryId, GetLibrary.Handler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetLibrary.Query(libraryId), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToProblem();
    }

    private static async Task<IResult> Update(
        Guid libraryId, UpdateLibraryRequest body, UpdateLibrary.Handler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new UpdateLibrary.Command(libraryId, body.Name), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToProblem();
    }
}
