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
        // Testcontainers asks for a newer Docker API than old daemons (e.g. Docker Desktop 24, API 1.43) accept,
        // and Docker 29+ refuses anything older than 1.44. Pin the client to whatever the local daemon reports,
        // unless the caller already chose a version.
        if (Environment.GetEnvironmentVariable("DOCKER_API_VERSION") is null && TryGetDaemonApiVersion() is { } version)
        {
            Environment.SetEnvironmentVariable("DOCKER_API_VERSION", version);
        }
    }

    private static string? TryGetDaemonApiVersion()
    {
        try
        {
            using var docker = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "docker",
                Arguments = "version --format {{.Server.APIVersion}}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (docker is null || !docker.WaitForExit(TimeSpan.FromSeconds(15)) || docker.ExitCode != 0)
            {
                return null;
            }

            var version = docker.StandardOutput.ReadToEnd().Trim();
            return version.Length > 0 ? version : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // no docker CLI on PATH: let Testcontainers use its default
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
