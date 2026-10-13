using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.AspNetCore.Authorization;

namespace CollectionMaxxing.Api.Auth;

/// <summary>Endpoint metadata: the lowest role, in the scan session's library, allowed to call this endpoint.</summary>
public sealed record RequireScanSessionRoleMetadata(LibraryRole MinimumRole);

/// <summary>
/// The library check for endpoints under <c>/api/v1/scan-sessions/{id}</c>, which have no library in their route
/// (docs/07: "the handler resolves the owning library first and then runs the same check"). Not a member of the session's
/// library, or no such session: 404. A member whose role is too low: 403. Never trusts a library id from the caller.
/// </summary>
public sealed class ScanSessionAccessFilter(
    ICurrentUser currentUser,
    IAuthorizationService authorization,
    FindSessionLibrary.Handler sessions) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var required = http.GetEndpoint()?.Metadata.GetMetadata<RequireScanSessionRoleMetadata>()
            ?? throw new InvalidOperationException($"{http.GetEndpoint()?.DisplayName} doesn't call RequireScanSessionRole.");

        var libraryId = Guid.TryParse(http.Request.RouteValues["id"]?.ToString(), out var sessionId)
            ? await sessions.HandleAsync(sessionId, http.RequestAborted)
            : null;
        if (libraryId is not { } library || currentUser.RoleIn(library) is not { } callerRole)
        {
            return ProblemResults.Problem(StatusCodes.Status404NotFound, "Not found", "That scan session doesn't exist.");
        }

        var access = await authorization.AuthorizeAsync(
            http.User, new LibraryAccess(library, callerRole), new LibraryRoleRequirement(required.MinimumRole));
        return access.Succeeded
            ? await next(context)
            : ProblemResults.Problem(StatusCodes.Status403Forbidden, "Forbidden", "Your role in this library doesn't allow that.");
    }
}

public static class RequireScanSessionRoleExtensions
{
    /// <summary>Declares the lowest role in the scan session's library that may call the endpoint, and enforces it.</summary>
    public static TBuilder RequireScanSessionRole<TBuilder>(this TBuilder builder, LibraryRole minimumRole)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.RequireAuthorization(); // anonymous callers are turned away (401) before any session is looked up
        builder.WithMetadata(new RequireScanSessionRoleMetadata(minimumRole));
        builder.AddEndpointFilter<TBuilder, ScanSessionAccessFilter>();
        builder.ProducesProblem(StatusCodes.Status401Unauthorized);
        builder.ProducesProblem(StatusCodes.Status403Forbidden);
        builder.ProducesProblem(StatusCodes.Status404NotFound);
        return builder;
    }
}
