using System.Diagnostics;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace CollectionMaxxing.TestSupport;

/// <summary>
/// The Postgres container of a test assembly whose test classes run in parallel: <see cref="Shared"/>, started on first use (or up front by
/// <see cref="PostgresServerLifetime"/>, which also disposes it). A test process runs one assembly, so there is one server per assembly. It
/// migrates once into a template database; each test class then asks for a database of its own (<see cref="CreateDatabaseAsync"/>), a copy of
/// the template that costs a fraction of a second, so no two classes ever see each other's rows.
/// </summary>
public sealed class PostgresServer : IAsyncDisposable
{
    private const string Template = "cm_template";

    static PostgresServer()
    {
        PostgresSupport.PinDockerApiVersion();
        Shared = new PostgresServer();
    }

    // Parallel classes each hold a pool of connections; the default of 100 would run out. A throwaway database has no use for durability.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17")
        .WithCommand("-c", "max_connections=1000", "-c", "fsync=off", "-c", "synchronous_commit=off", "-c", "full_page_writes=off")
        .Build();

    private readonly SemaphoreSlim _cloning = new(4);
    private readonly Lazy<Task> _started;
    private string _adminConnectionString = string.Empty;
    private int _disposed;

    private PostgresServer() => _started = new Lazy<Task>(StartAsync);

    public static PostgresServer Shared { get; }

    /// <summary>Starts the container and migrates the template. Safe to call from many classes at once: it happens once.</summary>
    public Task EnsureStartedAsync() => _started.Value;

    /// <summary>A new, empty (but migrated) database of its own. Dispose it to drop it.</summary>
    public async Task<PostgresFixture> CreateDatabaseAsync()
    {
        await EnsureStartedAsync();
        var name = "cm_" + Guid.NewGuid().ToString("N")[..16];
        await _cloning.WaitAsync();
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await ExecuteAsync($"CREATE DATABASE \"{name}\" TEMPLATE \"{Template}\"");
                    break;
                }
                catch (PostgresException ex) when (attempt < 5 && ex.SqlState is "55006" or "40001" or "40P01")
                {
                    // Object in use / serialization failure / deadlock while another class was cloning at the same moment: try again.
                    await Task.Delay(100 * attempt);
                }
            }
        }
        finally
        {
            _cloning.Release();
        }

        var database = new PostgresFixture(this, name, ConnectionStringFor(name));
        await database.PrepareCloneAsync();
        return database;
    }

    internal async Task DropDatabaseAsync(string name)
    {
        try
        {
            await ExecuteAsync($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
        }
        catch (Exception ex) when (Volatile.Read(ref _disposed) == 1 && ex is NpgsqlException or System.IO.IOException or System.Net.Sockets.SocketException)
        {
            // The container is already gone (xUnit tears down assembly fixtures in no promised order), and it took the database with it.
        }
    }

    public async ValueTask DisposeAsync()
    {
        Volatile.Write(ref _disposed, 1);
        await _container.DisposeAsync();
    }

    private async Task StartAsync()
    {
        var started = Stopwatch.GetTimestamp();
        await _container.StartAsync();
        var containerUp = Stopwatch.GetTimestamp();

        _adminConnectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = "postgres" }.ConnectionString;
        await ExecuteAsync($"CREATE DATABASE \"{Template}\"");

        // Unpooled, so no session lingers on the template: Postgres refuses to copy a database that anyone is connected to.
        await PostgresSupport.MigrateAsync(new NpgsqlConnectionStringBuilder(ConnectionStringFor(Template)) { Pooling = false }.ConnectionString);
        await ExecuteAsync($"ALTER DATABASE \"{Template}\" WITH IS_TEMPLATE true ALLOW_CONNECTIONS false");

        await PostgresSupport.WritePerfLineAsync(started, containerUp, Stopwatch.GetTimestamp());
    }

    private string ConnectionStringFor(string database) =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = database }.ConnectionString;

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// Register with <c>[assembly: AssemblyFixture(typeof(PostgresServerLifetime))]</c>: starts <see cref="PostgresServer.Shared"/> when the
/// assembly's tests begin and removes the container when they end.
/// </summary>
public sealed class PostgresServerLifetime : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await PostgresServer.Shared.EnsureStartedAsync();

    public async ValueTask DisposeAsync() => await PostgresServer.Shared.DisposeAsync();
}
