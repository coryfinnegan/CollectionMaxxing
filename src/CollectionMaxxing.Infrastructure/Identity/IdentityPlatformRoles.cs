using CollectionMaxxing.Application.Accounts;
using Microsoft.AspNetCore.Identity;

namespace CollectionMaxxing.Infrastructure.Identity;

/// <summary>The platform roles of accounts, on ASP.NET Core Identity's roles. The role is made the first time anyone is given it.</summary>
internal sealed class IdentityPlatformRoles(UserManager<AppUser> users, RoleManager<IdentityRole<Guid>> roles) : IPlatformRoles
{
    public async Task GrantDevToolsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await FindAsync(userId);
        if (!await roles.RoleExistsAsync(PlatformRoles.DevTools))
        {
            var created = await roles.CreateAsync(new IdentityRole<Guid>(PlatformRoles.DevTools));
            if (!created.Succeeded && !await roles.RoleExistsAsync(PlatformRoles.DevTools))
            {
                Check(created);
            }
        }

        if (!await users.IsInRoleAsync(user, PlatformRoles.DevTools))
        {
            Check(await users.AddToRoleAsync(user, PlatformRoles.DevTools));
        }
    }

    public async Task<bool> RevokeDevToolsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await FindAsync(userId);
        if (!await users.IsInRoleAsync(user, PlatformRoles.DevTools))
        {
            return false;
        }

        Check(await users.RemoveFromRoleAsync(user, PlatformRoles.DevTools));
        return true;
    }

    private async Task<AppUser> FindAsync(Guid userId) =>
        await users.FindByIdAsync(userId.ToString()) ?? throw new InvalidOperationException("The account is gone.");

    // An Identity failure here is a broken database, not something the person at the host can fix by retyping: let the command report it.
    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException("Identity refused the change: " + string.Join("; ", result.Errors.Select(e => e.Code)));
        }
    }
}
