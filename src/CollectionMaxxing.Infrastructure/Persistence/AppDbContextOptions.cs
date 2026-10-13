using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Infrastructure.Persistence;

/// <summary>The one place that decides how EF talks to Postgres (used by the app, tests and design-time tools).</summary>
public static class AppDbContextOptions
{
    /// <summary>
    /// Identity only maps its passkey table when its store schema is version 3, and it reads that from the application's
    /// <see cref="IdentityOptions"/>. The app registers it in <c>AddInfrastructure</c>; tools and tests that have no app
    /// use this tiny provider instead, so every way of building the model agrees.
    /// </summary>
    private static readonly ServiceProvider ModelServices = new ServiceCollection()
        .Configure<IdentityOptions>(ConfigureIdentityStores)
        .BuildServiceProvider();

    public static void ConfigureIdentityStores(IdentityOptions options) =>
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;

    public static void Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();

    public static DbContextOptions<AppDbContext> Create(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        Configure(builder, connectionString);
        builder.UseApplicationServiceProvider(ModelServices);
        return builder.Options;
    }
}
