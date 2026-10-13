using Xunit;

namespace CollectionMaxxing.TestSupport;

/// <summary>Base class for tests that use the shared Postgres: the database is emptied before each test.</summary>
public abstract class DatabaseTest(PostgresFixture db) : IAsyncLifetime
{
    protected PostgresFixture Db { get; } = db;

    public virtual async ValueTask InitializeAsync() => await Db.ResetAsync();

    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
