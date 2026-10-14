using CollectionMaxxing.Application.Admin;
using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Operations;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Jobs;

/// <summary>Use case: the platform administrator's view of jobs that failed or are waiting (running and finished jobs are left out).</summary>
public static class ListJobs
{
    public const int Limit = 200;

    public sealed class Handler(IAppDbContext db)
    {
        public async Task<Page<AdminJobDto>> HandleAsync(CancellationToken cancellationToken)
        {
            var items = await db.Jobs
                .AsNoTracking()
                .Where(j => j.Status == JobStatus.Failed || j.Status == JobStatus.Pending)
                .OrderBy(j => j.Status) // the column holds text, so "failed" sorts before "pending": those need a person
                .ThenBy(j => j.RunAfter)
                .ThenBy(j => j.Id)
                .Take(Limit)
                .Select(j => new AdminJobDto(j.Id, j.Type, j.Status, j.Attempts, j.RunAfter, j.LastError))
                .ToListAsync(cancellationToken);
            return new Page<AdminJobDto>(items);
        }
    }
}
