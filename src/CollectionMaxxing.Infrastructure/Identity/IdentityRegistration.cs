using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Application.Accounts.Devices;
using CollectionMaxxing.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CollectionMaxxing.Infrastructure.Identity;

public static class IdentityRegistration
{
    /// <summary>Users and roles on ASP.NET Core Identity, plus the stores the sign-in use cases need.</summary>
    public static IServiceCollection AddIdentityAccounts(this IServiceCollection services)
    {
        services.AddOptions<AuthOptions>().BindConfiguration(AuthOptions.SectionName);

        services.AddIdentityCore<AppUser>(options =>
            {
                // Passwordless: nobody signs in with an email or a password, so nothing about them is required.
                options.User.RequireUniqueEmail = false;
                options.SignIn.RequireConfirmedAccount = false;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<AppDbContext>();

        services.TryAddScoped<IAccountStore, IdentityAccountStore>();
        services.TryAddScoped<IWebSessions, WebSessionStore>();
        services.TryAddScoped<IPlatformRoles, IdentityPlatformRoles>();
        return services;
    }
}
