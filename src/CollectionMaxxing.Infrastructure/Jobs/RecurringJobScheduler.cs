using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Infrastructure.Jobs;

/// <summary>
/// Turns recurring registrations into ordinary jobs. Time is cut into periods; each period gets a job with the
/// key <c>recurring:{type}:{period number}</c>, and the key is unique across every status, so any number of
/// workers (or restarts) queue it once per period.
/// </summary>
public sealed class RecurringJobScheduler(
    IEnumerable<RecurringJobRegistration> recurring,
    IServiceScopeFactory scopes,
    TimeProvider time)
{
    private readonly RecurringJobRegistration[] _recurring = [.. recurring];

    /// <returns>How many jobs this call queued.</returns>
    public async Task<int> EnqueueDueAsync(CancellationToken cancellationToken)
    {
        if (_recurring.Length == 0)
        {
            return 0;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ids = scope.ServiceProvider.GetRequiredService<IIdGenerator>();
        var now = time.GetUtcNow();
        var queued = 0;
        foreach (var job in _recurring)
        {
            var period = now.UtcTicks / job.Every.Ticks;
            var key = $"recurring:{job.Type}:{period}";
            if (await JobInserter.InsertAsync(db, ids.NewId(), job.Type, null, now, now, key, activeOnly: false, cancellationToken))
            {
                queued++;
            }
        }

        return queued;
    }
}
