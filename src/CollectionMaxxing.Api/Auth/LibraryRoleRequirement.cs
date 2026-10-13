using CollectionMaxxing.Domain.Libraries;
using Microsoft.AspNetCore.Authorization;

namespace CollectionMaxxing.Api.Auth;

/// <summary>"The caller must have at least this role in the library."</summary>
public sealed record LibraryRoleRequirement(LibraryRole MinimumRole) : IAuthorizationRequirement;

/// <summary>The resource the requirement is checked against: a library, and the caller's role in it.</summary>
public sealed record LibraryAccess(Guid LibraryId, LibraryRole CallerRole);

public sealed class LibraryRoleHandler : AuthorizationHandler<LibraryRoleRequirement, LibraryAccess>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, LibraryRoleRequirement requirement, LibraryAccess resource)
    {
        // Roles are ordered: Viewer < Editor < Owner.
        if (resource.CallerRole >= requirement.MinimumRole)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
