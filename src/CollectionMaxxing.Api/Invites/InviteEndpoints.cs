using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Libraries;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Invites;
using CollectionMaxxing.Application.Libraries;
using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Api.Invites;

/// <summary>
/// Invitations to join a library (docs/04, "Libraries and members"). Managing them is for the owner; creating one needs a recent
/// sign-in. The one anonymous endpoint, the preview the join page asks for, is rate-limited and answers every token that isn't usable
/// with the same 404.
/// </summary>
public static class InviteEndpoints
{
    public static IServiceCollection AddInvites(this IServiceCollection services) => InviteServices.Add(services);

    public static IEndpointRouteBuilder MapInviteEndpoints(this IEndpointRouteBuilder app)
    {
        var library = app.MapLibraryGroup();

        library.MapGet("/invites", async (Guid libraryId, ListInvites.Handler handler, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handler.HandleAsync(libraryId, cancellationToken)))
            .WithName("ListInvites").WithTags("Invites")
            .Produces<Page<InviteDto>>()
            .RequireLibraryRole(LibraryRole.Owner);

        // The link and QR payload are in this response and nowhere else: only a hash of the token is stored.
        library.MapPost("/invites", async (Guid libraryId, CreateInviteRequest body, CreateInvite.Handler handler, CancellationToken cancellationToken) =>
            {
                var result = await handler.HandleAsync(new CreateInvite.Command(libraryId, body.Name, body.Role), cancellationToken);
                return result.IsSuccess ? Results.Created((string?)null, result.Value) : result.Error!.ToProblem();
            })
            .WithName("CreateInvite").WithTags("Invites")
            .Produces<CreatedInviteDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireLibraryRole(LibraryRole.Owner).RequireRecentSignIn();

        library.MapDelete("/invites/{id:guid}", async (Guid libraryId, Guid id, RevokeInvite.Handler handler, CancellationToken cancellationToken) =>
            {
                var result = await handler.HandleAsync(libraryId, id, cancellationToken);
                return result.IsSuccess ? Results.NoContent() : result.Error!.ToProblem();
            })
            .WithName("RevokeInvite").WithTags("Invites")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireLibraryRole(LibraryRole.Owner);

        // What the join page shows before the person signs in. Anonymous (they have no account yet) and rate-limited like the
        // other endpoints that take a secret. An unknown, expired, used and revoked token all get the same 404, byte for byte.
        app.MapGet("/api/v1/invites/{token}", async (string token, GetInvitePreview.Handler handler, CancellationToken cancellationToken) =>
            {
                var result = await handler.HandleAsync(token, cancellationToken);
                return result.IsSuccess ? Results.Ok(result.Value) : InviteProblems.Invalid();
            })
            .WithName("GetInvitePreview").WithTags("Invites")
            .Produces<InvitePreviewDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .AllowAnonymous().RequireRateLimiting(RateLimiting.PolicyName);

        // Joining while signed in (docs/15): the invite is a link, a link's token or a short code. Unknown, expired, revoked and used invites
        // all get the same 404, byte for byte. Limited per client and per account, so codes can't be guessed.
        app.MapPost("/api/v1/invites:preview", async (InviteLookupRequest body, GetInvitePreview.Handler handler, CancellationToken cancellationToken) =>
            {
                var result = await handler.HandleAsync(InviteKey.Parse(body.Invite), cancellationToken);
                return result.IsSuccess ? Results.Ok(result.Value) : InviteProblems.Invalid();
            })
            .WithName("PreviewInvite").WithTags("Invites")
            .Produces<InvitePreviewDto>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .RequireAuthorization().RequirePerClientAndAccountRateLimit();

        // Exactly the invited role (editor or viewer, never owner), once. A member of that library already gets 409 and the invite stays unspent.
        app.MapPost("/api/v1/invites:accept", async (InviteLookupRequest body, AcceptInvite.Handler handler, CancellationToken cancellationToken) =>
            {
                var result = await handler.HandleAsync(body.Invite, cancellationToken);
                return result.IsSuccess ? Results.Ok(result.Value)
                    : result.Error == InviteErrors.Invalid ? InviteProblems.Invalid()
                    : result.Error!.ToProblem();
            })
            .WithName("AcceptInvite").WithTags("Invites")
            .Produces<LibrarySummaryDto>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .RequireAuthorization().RequirePerClientAndAccountRateLimit();

        return app;
    }
}
