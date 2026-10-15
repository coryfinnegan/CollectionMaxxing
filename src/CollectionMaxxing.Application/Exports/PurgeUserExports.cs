using CollectionMaxxing.Application.Accounts.Deletion;

namespace CollectionMaxxing.Application.Exports;

/// <summary>
/// Part of purging an account: removes the person's export files and their export jobs. An export is theirs alone, not a library's (it
/// holds every library they belonged to, which carry on without them), so this is a user-level step. The hourly cleanup deletes files after
/// a day, but it only looks at file ages: it can miss one, and it never touches the jobs, whose payloads name the person. Safe to repeat.
/// </summary>
public sealed class PurgeUserExports(IExportJobs jobs, IExportStore store) : IUserPurgeStep
{
    public async Task PurgeAsync(Guid userId, CancellationToken cancellationToken)
    {
        // Files first, then the rows that name them: a crash in between leaves a row to find them by, never a file nobody can name.
        // A file that is stuck keeps its row (the daily purge sweep finds the person by it and tries again) and does not stop the rest of the purge.
        var gone = new List<Guid>();
        foreach (var exportId in await jobs.ListIdsForUserAsync(userId, cancellationToken))
        {
            if (await store.TryDeleteAsync(exportId, cancellationToken))
            {
                gone.Add(exportId);
            }
        }

        await jobs.DeleteForUserAsync(userId, gone, cancellationToken);
    }
}
