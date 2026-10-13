using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Infrastructure.Persistence;

/// <summary>The one place that decides how EF talks to Postgres (used by the app, tests and design-time tools).</summary>
public static class AppDbContextOptions
{
    public static void Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();

    public static DbContextOptions<AppDbContext> Create(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        Configure(builder, connectionString);
        return builder.Options;
    }
}
