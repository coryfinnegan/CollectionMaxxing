using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Infrastructure.Persistence;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace CollectionMaxxing.Infrastructure.Tests.Persistence;

/// <summary>
/// The migration that makes a learned barcode belong to its library, run against data the old schema held: a link the commit of a
/// scan session taught keeps that session's library; every other link (a provider's, or one whose session is gone) stays shared and is
/// never dropped. Runs in a scratch database, so the migrations are walked one step at a time.
/// </summary>
public class LearnedBarcodeMigrationTests(PostgresFixture db) : DatabaseTest(db)
{
    private const string Before = "20261028154605_ScanEngineFmText";
    private const string After = "20261031041146_LearnedBarcodesPerLibrary";
    private const string EmptyObject = "{}";

    private static readonly Guid LibraryA = Guid.CreateVersion7();
    private static readonly Guid LibraryB = Guid.CreateVersion7();
    private static readonly Guid SessionA = Guid.CreateVersion7();
    private static readonly Guid SessionB = Guid.CreateVersion7();
    private static readonly Guid SessionGone = Guid.CreateVersion7();
    private static readonly Guid Release1 = Guid.CreateVersion7();
    private static readonly Guid Release2 = Guid.CreateVersion7();
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

    private static async Task ExecuteAsync(string connectionString, string sql, bool withoutForeignKeys = false)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        if (withoutForeignKeys)
        {
            await using var relax = new NpgsqlCommand("set session_replication_role = replica", connection);
            await relax.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

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

    /// <summary>The old schema, with two libraries, their sessions, two releases and one link of each kind.</summary>
    private static async Task SeedOldSchemaAsync(string connectionString)
    {
        // Foreign keys are skipped while seeding (no users, no members): the rows only need to be consistent with each other.
        await ExecuteAsync(connectionString, $"""
            insert into libraries (id, created_at, name) values
              ('{LibraryA}', now(), 'A'), ('{LibraryB}', now(), 'B');
            insert into scan_sessions (id, library_id, user_id, status, channel, media_hint, default_status, auto_commit_copies, created_at) values
              ('{SessionA}', '{LibraryA}', '{User}', 'open', 'home', 'mixed', 'owned', false, now()),
              ('{SessionB}', '{LibraryB}', '{User}', 'open', 'home', 'mixed', 'owned', false, now()),
              ('{SessionGone}', '{LibraryA}', '{User}', 'open', 'home', 'mixed', 'owned', false, now());
            insert into works (id, kind, title, sort_title, normalized_title, creators, genres, external_ids, created_at) values
              ('{Guid.CreateVersion7()}', 'book', 'W', 'w', 'w', '[]', '{EmptyObject}', '{EmptyObject}', now());
            """, withoutForeignKeys: true);
        await ExecuteAsync(connectionString, $"""
            insert into releases (id, work_id, format, source, created_at)
              select '{Release1}', id, 'paperback', 'provider', now() from works limit 1;
            insert into releases (id, work_id, format, source, created_at)
              select '{Release2}', id, 'paperback', 'provider', now() from works limit 1;
            insert into release_identifiers (type, value, release_id, learned_by_session_id) values
              ('gtin13', '0000000000001', '{Release1}', null),
              ('gtin13', '0000000000002', '{Release1}', '{SessionA}'),
              ('gtin13', '0000000000003', '{Release2}', '{SessionB}'),
              ('gtin13', '0000000000004', '{Release2}', '{SessionGone}');
            """, withoutForeignKeys: true);

        // The session is deleted for real (foreign keys on): the link it taught keeps its row but loses its session.
        await ExecuteAsync(connectionString, $"delete from scan_sessions where id = '{SessionGone}'");
    }

    [Fact]
    public async Task A_link_a_session_taught_keeps_that_sessions_library_and_every_other_link_stays_shared()
    {
        var (connectionString, drop) = await CreateScratchDatabaseAsync();
        try
        {
            await using var context = new AppDbContext(AppDbContextOptions.Create(connectionString), new CurrentUser());
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(Before, TestContext.Current.CancellationToken);
            await SeedOldSchemaAsync(connectionString);

            await migrator.MigrateAsync(After, TestContext.Current.CancellationToken);

            var links = await QueryAsync(connectionString, "select value || ':' || coalesce(library_id::text, 'shared') from release_identifiers order by value");
            links.ShouldBe(
            [
                "0000000000001:shared", // a provider's link
                $"0000000000002:{LibraryA}", // taught by library A's session
                $"0000000000003:{LibraryB}", // taught by library B's session
                "0000000000004:shared", // taught by a session that is gone: looks like a provider's link, so it is kept shared
            ]);
            (await QueryAsync(connectionString, "select count(distinct id)::text from release_identifiers")).ShouldBe(["4"]);
        }
        finally
        {
            await drop();
        }
    }

    [Fact]
    public async Task After_the_migration_the_previous_release_can_still_write_links_without_an_id()
    {
        // A failed deploy rolls back to the previous image against the migrated schema; it inserts (type, value, release_id) only.
        var (connectionString, drop) = await CreateScratchDatabaseAsync();
        try
        {
            await using var context = new AppDbContext(AppDbContextOptions.Create(connectionString), new CurrentUser());
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(Before, TestContext.Current.CancellationToken);
            await SeedOldSchemaAsync(connectionString);
            await migrator.MigrateAsync(After, TestContext.Current.CancellationToken);

            await ExecuteAsync(connectionString, $"""
                insert into release_identifiers (type, value, release_id) values ('gtin13', '0000000000009', '{Release1}')
                """);

            (await QueryAsync(connectionString, "select count(*)::text from release_identifiers where value = '0000000000009' and id is not null"))
                .ShouldBe(["1"]);
        }
        finally
        {
            await drop();
        }
    }

    [Fact]
    public async Task After_the_migration_another_library_can_learn_the_same_barcode_for_the_same_release()
    {
        var (connectionString, drop) = await CreateScratchDatabaseAsync();
        try
        {
            await using var context = new AppDbContext(AppDbContextOptions.Create(connectionString), new CurrentUser());
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(Before, TestContext.Current.CancellationToken);
            await SeedOldSchemaAsync(connectionString);
            await migrator.MigrateAsync(After, TestContext.Current.CancellationToken);

            await ExecuteAsync(connectionString, $"""
                insert into release_identifiers (id, type, value, release_id, library_id)
                values (gen_random_uuid(), 'gtin13', '0000000000002', '{Release1}', '{LibraryB}')
                """);

            (await QueryAsync(connectionString, $"select count(*)::text from release_identifiers where value = '0000000000002'")).ShouldBe(["2"]);
        }
        finally
        {
            await drop();
        }
    }

    [Fact]
    public async Task Rolling_back_keeps_one_link_per_barcode_and_release_preferring_the_shared_one()
    {
        var (connectionString, drop) = await CreateScratchDatabaseAsync();
        try
        {
            await using var context = new AppDbContext(AppDbContextOptions.Create(connectionString), new CurrentUser());
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(Before, TestContext.Current.CancellationToken);
            await SeedOldSchemaAsync(connectionString);
            await migrator.MigrateAsync(After, TestContext.Current.CancellationToken);
            await ExecuteAsync(connectionString, $"""
                insert into release_identifiers (id, type, value, release_id, library_id) values
                  (gen_random_uuid(), 'gtin13', '0000000000002', '{Release1}', '{LibraryB}'),
                  (gen_random_uuid(), 'gtin13', '0000000000002', '{Release1}', null),
                  (gen_random_uuid(), 'gtin13', '0000000000003', '{Release2}', '{LibraryA}')
                """);

            await migrator.MigrateAsync(Before, TestContext.Current.CancellationToken);

            (await QueryAsync(connectionString, "select value || ':' || count(*) from release_identifiers group by value order by value"))
                .ShouldBe(["0000000000001:1", "0000000000002:1", "0000000000003:1", "0000000000004:1"]);
            (await QueryAsync(connectionString, "select count(*)::text from information_schema.columns where table_name = 'release_identifiers' and column_name in ('id', 'library_id')"))
                .ShouldBe(["0"]);

            // The old key is back: the same barcode twice for one release is refused again.
            var duplicate = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(connectionString, $"""
                insert into release_identifiers (type, value, release_id) values ('gtin13', '0000000000001', '{Release1}')
                """));
            duplicate.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        }
        finally
        {
            await drop();
        }
    }
}
