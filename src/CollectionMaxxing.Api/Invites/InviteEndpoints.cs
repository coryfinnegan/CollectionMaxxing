using CollectionMaxxing.Api.Auth;
using CollectionMaxxing.Api.Libraries;
using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Invites;
using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Api.Invites;

/// <summary>Invitations to join a library (docs/04, "Libraries and members"). Owner only; creating one needs a recent passkey.</summary>
public static class InviteEndpoints
{
    public static IServiceCollection AddInvites(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapInviteEndpoints(this IEndpointRouteBuilder app)
    {
        var library = app.MapLibraryGroup();

        library.MapGet("/invites", (Guid libraryId) => StubEndpoints.NotImplemented())
            .WithName("ListInvites").WithTags("Invites")
            .Produces<Page<InviteDto>>()
            .RequireLibraryRole(LibraryRole.Owner).IsStub();

        library.MapPost("/invites", (Guid libraryId, CreateInviteRequest body) => StubEndpoints.NotImplemented())
            .WithName("CreateInvite").WithTags("Invites")
            .Produces<CreatedInviteDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .RequireLibraryRole(LibraryRole.Owner).IsStub();

        library.MapDelete("/invites/{id:guid}", (Guid libraryId, Guid id) => StubEndpoints.NotImplemented())
            .WithName("RevokeInvite").WithTags("Invites")
            .Produces(StatusCodes.Status204NoContent)
            .RequireLibraryRole(LibraryRole.Owner).IsStub();

        return app;
    }
}
