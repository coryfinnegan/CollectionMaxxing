using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Infrastructure.Persistence;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace CollectionMaxxing.Infrastructure.Tests.Persistence;

/// <summary>
/// The migration that lets a scan session remember the phone's UTC offset. The column is nullable and nothing in the old schema needs
/// it, so a session from before keeps working and a failed deploy that rolls back to the previous image (which never writes the
/// column) still starts sessions against the migrated schema. Runs in a scratch database, so the migrations are walked one step at a time.
/// </summary>
public class ScanSessionOffsetMigrationTests(PostgresFixture db) : DatabaseTest(db)
{
    private const string Before = "20261031041310_JobsDedupeKeyIndex";
    private const string After = "20261031101821_ScanSessionUtcOffset";

    private static readonly Guid Library = Guid.CreateVersion7();
    private static readonly Guid User = Guid.CreateVersion7();

    private async Task<(string ConnectionString, Func<Task> Drop)> CreateScratchDatabaseAsync()
    {
        var name = "migration_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(Db.ConnectionString))
        {
            await admin.OpenAsync(TestContext.Current.CancellationToken);
            await using var create = new NpgsqlCommand($"create database {name}", admin);
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var scratch = new NpgsqlConnectionStringBuilder(Db.ConnectionString) { Database = name, Pooling = false }.ConnectionString;
        return (scratch, DropAsync);

        async Task DropAsync()
        {
            await using var admin = new NpgsqlConnection(Db.ConnectionString);
            await admin.OpenAsync(CancellationToken.None);
            await using var drop = new NpgsqlCommand($"drop database if exists {name} with (force)", admin);
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var relax = new NpgsqlCommand("set session_replication_role = replica", connection); // no users or members to point at
        await relax.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<List<string>> QueryAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(reader.IsDBNull(0) ? "(null)" : reader.GetValue(0).ToString()!);
        }

        return rows;
    }

    private static string InsertSession(Guid id, string? extraColumns = null, string? extraValues = null) => $"""
        insert into scan_sessions (id, library_id, user_id, status, channel, media_hint, default_status, auto_commit_copies, auto_commit_tiers, created_at{extraColumns})
        values ('{id}', '{Library}', '{User}', 'open', 'home', 'mixed', 'owned', false, array[]::text[], now(){extraValues})
        """;

    private async Task<(string ConnectionString, Func<Task> Drop, IMigrator Migrator, AppDbContext Context)> StartAtAsync(string migration)
    {
        var (connectionString, drop) = await CreateScratchDatabaseAsync();
        var context = new AppDbContext(AppDbContextOptions.Create(connectionString), new CurrentUser());
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(migration, TestContext.Current.CancellationToken);
        return (connectionString, drop, migrator, context);
    }

    [Fact]
    public async Task A_session_from_before_the_migration_keeps_its_row_and_has_no_offset()
    {
        var (connectionString, drop, migrator, context) = await StartAtAsync(Before);
        await using var scratch = context;
        try
        {
            var old = Guid.CreateVersion7();
            await ExecuteAsync(connectionString, $"insert into libraries (id, created_at, name) values ('{Library}', now(), 'A'); {InsertSession(old)};");

            await migrator.MigrateAsync(After, TestContext.Current.CancellationToken);

            (await QueryAsync(connectionString, $"select coalesce(utc_offset_minutes::text, 'none') from scan_sessions where id = '{old}'")).ShouldBe(["none"]);
        }
        finally
        {
            await drop();
        }
    }

    [Fact]
    public async Task After_the_migration_the_previous_release_can_still_start_sessions_without_the_column()
    {
        // A failed deploy rolls back to the previous image against the migrated schema; its insert never names utc_offset_minutes.
        var (connectionString, drop, _, context) = await StartAtAsync(After);
        await using var scratch = context;
        try
        {
            var session = Guid.CreateVersion7();
            await ExecuteAsync(connectionString, $"insert into libraries (id, created_at, name) values ('{Library}', now(), 'A'); {InsertSession(session)};");

            (await QueryAsync(connectionString, $"select count(*)::text from scan_sessions where id = '{session}' and utc_offset_minutes is null")).ShouldBe(["1"]);
        }
        finally
        {
            await drop();
        }
    }

    [Fact]
    public async Task The_database_itself_refuses_an_offset_no_time_zone_has()
    {
        var (connectionString, drop, _, context) = await StartAtAsync(After);
        await using var scratch = context;
        try
        {
            await ExecuteAsync(connectionString, $"insert into libraries (id, created_at, name) values ('{Library}', now(), 'A');");
            foreach (var real in new[] { -720, -300, 0, 840 })
            {
                await ExecuteAsync(connectionString, InsertSession(Guid.CreateVersion7(), ", utc_offset_minutes", $", {real}"));
            }

            foreach (var unreal in new[] { -721, 841 })
            {
                var refused = await Should.ThrowAsync<PostgresException>(
                    () => ExecuteAsync(connectionString, InsertSession(Guid.CreateVersion7(), ", utc_offset_minutes", $", {unreal}")));
                refused.ConstraintName.ShouldBe("ck_scan_sessions_utc_offset_minutes");
            }
        }
        finally
        {
            await drop();
        }
    }

    [Fact]
    public async Task Rolling_the_migration_back_drops_the_column_and_keeps_the_session()
    {
        var (connectionString, drop, migrator, context) = await StartAtAsync(After);
        await using var scratch = context;
        try
        {
            var session = Guid.CreateVersion7();
            await ExecuteAsync(connectionString, $"insert into libraries (id, created_at, name) values ('{Library}', now(), 'A'); {InsertSession(session, ", utc_offset_minutes", ", -300")};");

            await migrator.MigrateAsync(Before, TestContext.Current.CancellationToken);

            (await QueryAsync(connectionString, $"select count(*)::text from scan_sessions where id = '{session}'")).ShouldBe(["1"]);
            (await QueryAsync(connectionString, "select count(*)::text from information_schema.columns where table_name = 'scan_sessions' and column_name = 'utc_offset_minutes'"))
                .ShouldBe(["0"]);
        }
        finally
        {
            await drop();
        }
    }
}
