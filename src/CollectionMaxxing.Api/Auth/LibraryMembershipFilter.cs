using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Common;
using Microsoft.AspNetCore.Authorization;

namespace CollectionMaxxing.Api.Auth;

/// <summary>
/// Runs for every endpoint in the library route group, before the handler:
/// not a member of the library: 404 (don't reveal that it exists); a member whose role is too low: 403.
/// </summary>
public sealed class LibraryMembershipFilter(ICurrentUser currentUser, IAuthorizationService authorization) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;

        // Fail closed: an endpoint in this group that forgot RequireLibraryRole must not be reachable.
        var required = http.GetEndpoint()?.Metadata.GetMetadata<RequireLibraryRoleMetadata>()
            ?? throw new InvalidOperationException(
                $"{http.GetEndpoint()?.DisplayName} is in the library route group but doesn't call RequireLibraryRole.");

        if (!Guid.TryParse(http.Request.RouteValues["libraryId"]?.ToString(), out var libraryId)
            || currentUser.RoleIn(libraryId) is not { } callerRole)
        {
            return ProblemResults.Problem(StatusCodes.Status404NotFound, "Not found", "That library doesn't exist.");
        }

        var access = await authorization.AuthorizeAsync(
            http.User, new LibraryAccess(libraryId, callerRole), new LibraryRoleRequirement(required.MinimumRole));
        if (!access.Succeeded)
        {
            return ProblemResults.Problem(StatusCodes.Status403Forbidden, "Forbidden", "Your role in this library doesn't allow that.");
        }

        return await next(context);
    }
}
