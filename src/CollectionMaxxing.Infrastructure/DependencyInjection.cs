using CollectionMaxxing.Application.Accounts;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Infrastructure.Ids;
using CollectionMaxxing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CollectionMaxxing.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Name of the connection string. Aspire injects it as <c>ConnectionStrings__collectionmaxxing</c>.</summary>
    public const string ConnectionStringName = "collectionmaxxing";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IIdGenerator, Uuid7IdGenerator>();

        // Identity maps its passkey table only for store schema version 3; the model needs to see that setting.
        services.Configure<Microsoft.AspNetCore.Identity.IdentityOptions>(AppDbContextOptions.ConfigureIdentityStores);

        // The connection string is read when the context is first used, not at startup, so tools that start the
        // app without a database (OpenAPI generation, `dotnet ef`) still work.
        services.AddDbContext<AppDbContext>((provider, options) =>
        {
            var configuration = provider.GetRequiredService<IConfiguration>();
            var connectionString = configuration.GetConnectionString(ConnectionStringName)
                ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured.");
            AppDbContextOptions.Configure(options, connectionString);
        });
        services.AddScoped<IAppDbContext>(provider => provider.GetRequiredService<AppDbContext>());
        services.AddScoped<IMembershipReader, MembershipReader>();

        services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);
        return services;
    }
}
