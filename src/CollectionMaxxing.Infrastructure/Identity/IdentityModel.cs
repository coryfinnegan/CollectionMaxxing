using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Infrastructure.Identity;

/// <summary>
/// Gives the tables ASP.NET Core Identity and OpenIddict bring along our naming (snake_case, no "AspNet" prefix), so the
/// whole schema reads the same. Model only: the Identity and OpenIddict services are set up by the ID stream.
/// </summary>
internal static class IdentityModel
{
    private static readonly Dictionary<string, string> TableNames = new()
    {
        ["AspNetRoles"] = "roles",
        ["AspNetUserRoles"] = "user_roles",
        ["AspNetUserClaims"] = "user_claims",
        ["AspNetRoleClaims"] = "role_claims",
        ["AspNetUserLogins"] = "user_logins",
        ["AspNetUserTokens"] = "user_tokens",
        ["AspNetUserPasskeys"] = "user_passkeys",
        ["OpenIddictApplications"] = "openiddict_applications",
        ["OpenIddictAuthorizations"] = "openiddict_authorizations",
        ["OpenIddictScopes"] = "openiddict_scopes",
        ["OpenIddictTokens"] = "openiddict_tokens",
    };

    public static void Configure(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (entityType.GetTableName() is { } table && TableNames.TryGetValue(table, out var renamed))
            {
                entityType.SetTableName(renamed);
            }
        }

        // Index names are global in Postgres, so give Identity's PascalCase ones snake_case names too.
        modelBuilder.Entity<IdentityRole<Guid>>().HasIndex(r => r.NormalizedName).HasDatabaseName("ux_roles_normalized_name").IsUnique();
    }
}
