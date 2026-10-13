using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Respawn;
using Testcontainers.PostgreSql;
using Xunit;

namespace CollectionMaxxing.TestSupport;

/// <summary>
/// One real Postgres container per test assembly. Register it with
/// <c>[assembly: AssemblyFixture(typeof(PostgresFixture))]</c> and inherit <see cref="DatabaseTest"/>
/// to get a clean, fully migrated database at the start of every test.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    static PostgresFixture()
    {
        // Docker Desktop 24 (API 1.43) rejects the newer API version Testcontainers asks for by default.
        // Every Docker release since accepts 1.43 too, so pin it unless the caller already chose one.
        if (Environment.GetEnvironmentVariable("DOCKER_API_VERSION") is null)
        {
            Environment.SetEnvironmentVariable("DOCKER_API_VERSION", "1.43");
        }
    }

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();
    private Respawner? _respawner;

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        // Apply the real migrations, so every test run also proves they work.
        await using (var context = CreateContext())
        {
            await context.Database.MigrateAsync();
        }

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        _respawner = await Respawner.CreateAsync(connection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            SchemasToInclude = ["public"],
            TablesToIgnore = [new Respawn.Graph.Table("__EFMigrationsHistory")],
        });
    }

    /// <summary>Deletes all rows in every table (the schema stays).</summary>
    public async Task ResetAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await _respawner!.ResetAsync(connection);
    }

    /// <summary>
    /// A context for the given caller. With no argument the caller belongs to no library, so the
    /// library query filter hides everything; pass <see cref="CurrentUser"/> to act as a member.
    /// </summary>
    public AppDbContext CreateContext(ICurrentUser? currentUser = null) =>
        new(AppDbContextOptions.Create(ConnectionString), currentUser ?? new CurrentUser());

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}
