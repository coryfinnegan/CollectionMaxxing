using CollectionMaxxing.Api.Problems;
using CollectionMaxxing.Application.Common;
using Microsoft.AspNetCore.Authorization;

namespace CollectionMaxxing.Api.Auth;

/// <summary>
/// Runs before the handler of every endpoint that calls <c>RequireLibraryRole</c> (the extension attaches it) and of
/// every endpoint in the library route group (the group attaches it, so a forgotten <c>RequireLibraryRole</c> fails
/// closed). Not a member of the library: 404 (don't reveal that it exists); a member whose role is too low: 403.
/// An endpoint reached through both is checked once per request.
/// </summary>
public sealed class LibraryMembershipFilter(ICurrentUser currentUser, IAuthorizationService authorization) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;

        // Already checked by the same filter attached at another level (group, then endpoint): once is enough.
        if (http.Items.ContainsKey(CheckedKey))
        {
            return await next(context);
        }

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

        http.Items[CheckedKey] = true;
        return await next(context);
    }

    private static readonly object CheckedKey = new();
}
