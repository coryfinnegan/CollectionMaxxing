using System.Diagnostics;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Infrastructure.Persistence;
using Npgsql;
using Respawn;
using Testcontainers.PostgreSql;
using Xunit;

namespace CollectionMaxxing.TestSupport;

/// <summary>
/// A real, fully migrated Postgres database that tests reset to empty before each test (<see cref="ResetAsync"/>). It comes in two forms:
/// <list type="bullet">
/// <item>Registered as <c>[assembly: AssemblyFixture(typeof(PostgresFixture))]</c> it owns a Postgres container, one database in it, and every
/// test of the assembly shares them (inherit <see cref="DatabaseTest"/>).</item>
/// <item>Handed out by <see cref="PostgresServer.CreateDatabaseAsync"/> it is one database, cloned from the server's migrated template, that
/// belongs to a single test class; disposing it drops the database. That is how test classes run in parallel without sharing data.</item>
/// </list>
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    static PostgresFixture() => PostgresSupport.PinDockerApiVersion();

    private readonly PostgreSqlContainer? _container;
    private readonly PostgresServer? _server;
    private readonly string? _database;
    private readonly string? _cloneConnectionString;
    private Respawner? _respawner;

    /// <summary>The form that owns a container (an assembly fixture). xUnit needs exactly one public constructor.</summary>
    public PostgresFixture() => _container = PostgresSupport.BuildContainer();

    /// <summary>The form that is a database cloned on a shared <see cref="PostgresServer"/>.</summary>
    internal PostgresFixture(PostgresServer server, string database, string connectionString)
    {
        _server = server;
        _database = database;
        _cloneConnectionString = connectionString;
    }

    public string ConnectionString => _cloneConnectionString ?? _container!.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        var started = Stopwatch.GetTimestamp();
        await _container!.StartAsync();
        var containerUp = Stopwatch.GetTimestamp();

        await PostgresSupport.MigrateAsync(ConnectionString);
        _respawner = await PostgresSupport.CreateRespawnerAsync(ConnectionString);
        await PostgresSupport.WritePerfLineAsync(started, containerUp, Stopwatch.GetTimestamp());
    }

    /// <summary>Builds the reset for a cloned database (a clone is already migrated).</summary>
    internal async Task PrepareCloneAsync() => _respawner = await PostgresSupport.CreateRespawnerAsync(ConnectionString);

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

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
        else
        {
            await _server!.DropDatabaseAsync(_database!);
        }
    }
}
