using CollectionMaxxing.Domain.Accounts;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Operations;
using CollectionMaxxing.Infrastructure.Jobs;
using CollectionMaxxing.Infrastructure.Providers.Caching;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CollectionMaxxing.Infrastructure.Tests.Jobs;

/// <summary>
/// Three tables hold rows that are useless once their time has passed: replay keys, cached provider answers and recovery
/// codes. A daily job removes them. What must survive: the permanent ISBN copy (it is kept in the provider cache under an
/// expiry no clock reaches, and by name must never go), and a recovery code that can still be used.
/// </summary>
public class CleanExpiredRowsTests(PostgresFixture db) : DatabaseTest(db)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Forever = new(9999, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task CleanAsync(FakeTimeProvider? time = null)
    {
        await using var context = Db.CreateContext();
        await new CleanExpiredRows.Cleaner(context, time ?? new FakeTimeProvider(Now), NullLogger<CleanExpiredRows.Cleaner>.Instance)
            .HandleAsync(new CleanExpiredRowsPayload(), Ct);
    }

    private async Task AddKeyAsync(Guid userId, string key, DateTimeOffset expiresAt)
    {
        await using var context = Db.CreateContext();
        context.IdempotencyKeys.Add(new IdempotencyKey
        {
            UserId = userId,
            Key = key,
            RequestHash = "hash",
            ResponseStatus = 201,
            ResponseBodyJson = "{}",
            CreatedAt = Now.AddDays(-3),
            ExpiresAt = expiresAt,
        });
        await context.SaveChangesAsync(Ct);
    }

    private async Task AddCacheAsync(string provider, string key, DateTimeOffset expiresAt)
    {
        await using var context = Db.CreateContext();
        context.ProviderCache.Add(new ProviderCacheEntry
        {
            Provider = provider,
            Key = key,
            PayloadJson = "{}",
            FetchedAt = Now.AddDays(-3),
            ExpiresAt = expiresAt,
        });
        await context.SaveChangesAsync(Ct);
    }

    private async Task AddCodeAsync(Guid userId, string hash, DateTimeOffset expiresAt, DateTimeOffset? usedAt = null)
    {
        await using var context = Db.CreateContext();
        context.SetupCodes.Add(new SetupCode
        {
            Id = Guid.CreateVersion7(),
            Purpose = SetupCodePurpose.Recovery,
            UserId = userId,
            CodeHash = hash,
            CreatedAt = Now.AddDays(-3),
            ExpiresAt = expiresAt,
            UsedAt = usedAt,
        });
        await context.SaveChangesAsync(Ct);
    }

    private async Task<List<string>> KeysAsync()
    {
        await using var context = Db.CreateContext();
        return await context.IdempotencyKeys.AsNoTracking().Select(k => k.Key).ToListAsync(Ct);
    }

    private async Task<List<string>> CacheAsync()
    {
        await using var context = Db.CreateContext();
        return await context.ProviderCache.AsNoTracking().Select(c => c.Provider + "/" + c.Key).ToListAsync(Ct);
    }

    private async Task<List<string>> CodesAsync()
    {
        await using var context = Db.CreateContext();
        return await context.SetupCodes.AsNoTracking().Select(c => c.CodeHash).ToListAsync(Ct);
    }

    [Fact]
    public async Task Expired_replay_keys_go_and_the_ones_still_in_force_stay()
    {
        var user = await TestData.CreateUserAsync(Db);
        await AddKeyAsync(user, "long-gone", Now.AddDays(-30));
        await AddKeyAsync(user, "just-ended", Now.AddSeconds(-1));
        await AddKeyAsync(user, "ends-this-instant", Now); // a key is in force only while it ends after now
        await AddKeyAsync(user, "ends-a-moment-later", Now.AddSeconds(1));
        await AddKeyAsync(user, "next-week", Now.AddDays(7));

        await CleanAsync();

        (await KeysAsync()).ShouldBe(["ends-a-moment-later", "next-week"], ignoreOrder: true);
    }

    [Fact]
    public async Task Expired_provider_answers_go_for_every_provider_and_the_ones_still_fresh_stay()
    {
        await AddCacheAsync("openlibrary", "lookup:isbn:9780441172719", Now.AddDays(-1));
        await AddCacheAsync("igdb", "search:zelda", Now.AddSeconds(-1));
        await AddCacheAsync("openlibrary", "search:dune", Now.AddDays(10));
        await AddCacheAsync("igdb", "search:halo", Now.AddSeconds(1));

        await CleanAsync();

        (await CacheAsync()).ShouldBe(["openlibrary/search:dune", "igdb/search:halo"], ignoreOrder: true);
    }

    [Fact]
    public async Task Search_hits_whose_day_has_passed_go_and_those_still_open_stay()
    {
        await AddCacheAsync(ProviderCacheHitStore.StoreName, "openlibrary:old", Now.AddHours(-2));
        await AddCacheAsync(ProviderCacheHitStore.StoreName, "openlibrary:open", Now.AddHours(2));

        await CleanAsync();

        (await CacheAsync()).ShouldBe(["hit/openlibrary:open"]);
    }

    [Fact]
    public async Task The_permanent_isbn_copy_stays_whatever_its_expiry()
    {
        await AddCacheAsync(ProviderCacheIsbnStore.StoreName, "9780441172719", Forever);
        await AddCacheAsync(ProviderCacheIsbnStore.StoreName, "9780345391803", Now.AddYears(-5)); // not how it is written, but it must still stay
        await AddCacheAsync("openlibrary", "lookup:isbn:9780441172719", Now.AddDays(-1));

        await CleanAsync();

        (await CacheAsync()).ShouldBe(["isbn/9780441172719", "isbn/9780345391803"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_used_recovery_code_goes_even_if_in_date_and_an_expired_one_goes_and_a_usable_one_stays()
    {
        var user = await TestData.CreateUserAsync(Db);
        await AddCodeAsync(user, "used-in-date", Now.AddMinutes(20), usedAt: Now.AddMinutes(-5));
        await AddCodeAsync(user, "used-and-expired", Now.AddDays(-2), usedAt: Now.AddDays(-2).AddMinutes(10));
        await AddCodeAsync(user, "expired-unused", Now.AddMinutes(-1));
        await AddCodeAsync(user, "expires-this-instant", Now);
        await AddCodeAsync(user, "usable", Now.AddMinutes(1));

        await CleanAsync();

        (await CodesAsync()).ShouldBe(["usable"]);
    }

    [Fact]
    public async Task A_big_backlog_in_each_table_is_cleared_in_batches_and_what_is_kept_stays()
    {
        var user = await TestData.CreateUserAsync(Db);
        var count = CleanExpiredRows.BatchSize * 2 + 500;
        var past = Now.AddDays(-20);
        await using (var context = Db.CreateContext())
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO idempotency_keys (user_id, key, request_hash, response_status, response_body, created_at, expires_at)
                SELECT {user}, 'bulk-' || n, 'hash', 201, '[]'::jsonb, {past}, {past} FROM generate_series(1, {count}) AS n
                """, Ct);
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO provider_cache (provider, key, payload, fetched_at, expires_at)
                SELECT 'openlibrary', 'bulk-' || n, '[]'::jsonb, {past}, {past} FROM generate_series(1, {count}) AS n
                """, Ct);
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO setup_codes (id, purpose, user_id, code_hash, created_at, expires_at)
                SELECT gen_random_uuid(), 'recovery', {user}, 'bulk-' || n, {past}, {past} FROM generate_series(1, {count}) AS n
                """, Ct);
        }

        await AddKeyAsync(user, "keep", Now.AddDays(1));
        await AddCacheAsync("openlibrary", "keep", Now.AddDays(1));
        await AddCacheAsync(ProviderCacheIsbnStore.StoreName, "keep", Forever);
        await AddCodeAsync(user, "keep", Now.AddMinutes(10));

        await CleanAsync();

        (await KeysAsync()).ShouldBe(["keep"]);
        (await CacheAsync()).ShouldBe(["openlibrary/keep", "isbn/keep"], ignoreOrder: true);
        (await CodesAsync()).ShouldBe(["keep"]);
    }

    [Fact]
    public async Task Rows_another_transaction_holds_are_left_for_the_next_run_and_nothing_waits_for_them()
    {
        await AddCacheAsync("openlibrary", "held", Now.AddDays(-1));
        await AddCacheAsync("openlibrary", "free", Now.AddDays(-1));

        await using var holder = Db.CreateContext();
        await using var transaction = await holder.Database.BeginTransactionAsync(Ct);
        await holder.Database.ExecuteSqlRawAsync("SELECT 1 FROM provider_cache WHERE key = 'held' FOR UPDATE", Ct);

        await CleanAsync(); // would hang here if it waited for the held row
        (await CacheAsync()).ShouldBe(["openlibrary/held"]);

        await transaction.RollbackAsync(Ct);
        await CleanAsync();
        (await CacheAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Cleaning_twice_in_a_row_is_harmless()
    {
        var user = await TestData.CreateUserAsync(Db);
        await AddKeyAsync(user, "gone", Now.AddDays(-1));
        await AddKeyAsync(user, "stays", Now.AddDays(1));

        await CleanAsync();
        await CleanAsync();

        (await KeysAsync()).ShouldBe(["stays"]);
    }

    [Fact]
    public async Task Rows_expire_by_the_clock_not_by_the_wall_time_of_the_run()
    {
        var user = await TestData.CreateUserAsync(Db);
        await AddKeyAsync(user, "ends-in-a-day", Now.AddDays(1));
        var time = new FakeTimeProvider(Now);

        await CleanAsync(time);
        (await KeysAsync()).ShouldBe(["ends-in-a-day"]);

        time.Advance(TimeSpan.FromDays(1));
        await CleanAsync(time);
        (await KeysAsync()).ShouldBeEmpty();
    }

    [Fact]
    public void The_cleanup_is_a_recurring_daily_job_with_a_handler()
    {
        var services = new ServiceCollection();
        services.AddJobs();
        services.AddExpiredRowCleanup();

        using var provider = services.BuildServiceProvider();

        provider.GetServices<RecurringJobRegistration>().ShouldContain(r => r.Type == CleanExpiredRows.JobType && r.Every == TimeSpan.FromDays(1));
        provider.GetServices<JobRegistration>().ShouldContain(r => r.Type == CleanExpiredRows.JobType);
    }
}
