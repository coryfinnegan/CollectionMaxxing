using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CollectionMaxxing.TestSupport;

/// <summary>Asserts that the database itself refused something, and why (by Postgres error code).</summary>
public static class SchemaAssert
{
    /// <summary>
    /// Runs <paramref name="action"/> and expects Postgres to refuse it with <paramref name="sqlState"/>
    /// (e.g. <see cref="PostgresErrorCodes.UniqueViolation"/>), whether it surfaces straight from Npgsql or wrapped by EF.
    /// </summary>
    public static async Task FailsWithAsync(string sqlState, Func<Task> action)
    {
        var failure = await Should.ThrowAsync<Exception>(action);
        var postgres = failure as PostgresException ?? (failure as DbUpdateException)?.InnerException as PostgresException;

        postgres.ShouldNotBeNull($"Expected a Postgres error, got {failure.GetType().Name}: {failure.Message}");
        postgres.SqlState.ShouldBe(sqlState, postgres.MessageText);
    }
}
