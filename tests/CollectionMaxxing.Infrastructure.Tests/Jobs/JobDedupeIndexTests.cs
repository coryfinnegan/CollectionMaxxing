using CollectionMaxxing.TestSupport;
using Npgsql;

namespace CollectionMaxxing.Infrastructure.Tests.Jobs;

/// <summary>
/// Every job insert with a dedupe key looks the key up in <c>payload ->> 'key'</c>, and the recurring scheduler does it every
/// few seconds across every finished row. Without an index that is a scan of the whole table each time.
/// </summary>
public class JobDedupeIndexTests(PostgresFixture db) : DatabaseTest(db)
{
    private async Task<List<string>> QueryAsync(string sql, bool seqScanOff = false)
    {
        await using var connection = new NpgsqlConnection(Db.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        if (seqScanOff)
        {
            await using var off = new NpgsqlCommand("set local enable_seqscan = off", connection, transaction);
            await off.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    [Fact]
    public async Task The_jobs_table_has_an_index_on_the_dedupe_key()
    {
        var definitions = await QueryAsync("select indexdef from pg_indexes where schemaname = 'public' and tablename = 'jobs'");

        definitions.ShouldContain(d => d.Contains("((payload ->> 'key'::text))", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_dedupe_lookup_can_use_that_index()
    {
        // The table is empty, so the planner would scan it anyway; with scans off it must pick the index if the index can serve the query.
        // This is the lookup JobInserter runs for a recurring slot (any status) and for a user job (pending or running only).
        var plan = await QueryAsync(
            """
            explain select 1 from jobs
            where payload ->> 'key' = 'recurring:audit.prune:123'::text
              and (false::boolean = false or status in ('pending', 'running'))
            """,
            seqScanOff: true);

        string.Join('\n', plan).ShouldContain("ix_jobs_payload_key");
    }
}
