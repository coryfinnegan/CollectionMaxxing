using CollectionMaxxing.Application.Accounts.Deletion;
using CollectionMaxxing.Application.Exports;
using CollectionMaxxing.Infrastructure.Jobs;
using CollectionMaxxing.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CollectionMaxxing.Infrastructure.Exports;

/// <summary>Export files in a folder on the PC. One file per export, named by its id; nothing outside the folder is ever touched.</summary>
public sealed class FileExportStore : IExportStore
{
    private const string Extension = ".zip";
    private const string Partial = ".partial";

    private readonly string _root;

    public FileExportStore(string root)
    {
        _root = Path.GetFullPath(root);
    }

    private string PathOf(Guid exportId) => Path.Combine(_root, exportId.ToString("N") + Extension);

    public async Task SaveAsync(Guid exportId, DateTimeOffset createdAt, Func<Stream, CancellationToken, Task> write, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_root);
        var final = PathOf(exportId);
        var temporary = final + Partial;
        try
        {
            var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                await write(stream, cancellationToken);
            }

            // "Created" is the clock the cleanup reads, so it follows the app's time rather than the file system's.
            File.SetLastWriteTimeUtc(temporary, createdAt.UtcDateTime);
            File.Move(temporary, final, overwrite: true);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
    }

    public bool Exists(Guid exportId) => File.Exists(PathOf(exportId));

    public Task<Stream?> OpenReadAsync(Guid exportId, CancellationToken cancellationToken)
    {
        try
        {
            return Task.FromResult<Stream?>(
                new FileStream(PathOf(exportId), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan));
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
            return Task.FromResult<Stream?>(null);
        }
    }

    public Task DeleteAsync(Guid exportId, CancellationToken cancellationToken)
    {
        foreach (var path in new[] { PathOf(exportId), PathOf(exportId) + Partial })
        {
            try
            {
                File.Delete(path); // no error if it is already gone
            }
            catch (DirectoryNotFoundException)
            {
                // the folder is gone too: nothing to delete
            }
        }

        return Task.CompletedTask;
    }

    public Task<int> DeleteCreatedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_root))
        {
            return Task.FromResult(0);
        }

        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(_root).Where(f => f.EndsWith(Extension, StringComparison.Ordinal) || f.EndsWith(Extension + Partial, StringComparison.Ordinal)))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff.UtcDateTime)
                {
                    File.Delete(file);
                    deleted++;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // In use right now (being downloaded): the next run gets it.
            }
        }

        return Task.FromResult(deleted);
    }
}

/// <summary>Finds export jobs in the job table. The payload is <c>{"key":..., "data": {"exportId":..., "userId":...}}</c>.</summary>
public sealed class ExportJobs(AppDbContext db) : IExportJobs
{
    public async Task<ExportJobState?> FindAsync(Guid exportId, CancellationToken cancellationToken)
    {
        var id = exportId.ToString();
        var type = BuildExport.JobType;
        return (await db.Database.SqlQuery<StateRow>($"""
            SELECT (payload -> 'data' ->> 'userId')::uuid AS user_id, status
            FROM jobs
            WHERE type = {type} AND payload -> 'data' ->> 'exportId' = {id}
            LIMIT 1
            """).ToListAsync(cancellationToken))
            .Select(row => new ExportJobState(row.UserId, row.Status))
            .SingleOrDefault();
    }

    public async Task<Guid?> FindActiveAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = userId.ToString();
        var type = BuildExport.JobType;
        return (await db.Database.SqlQuery<Guid>($"""
            SELECT (payload -> 'data' ->> 'exportId')::uuid AS "Value"
            FROM jobs
            WHERE type = {type} AND payload -> 'data' ->> 'userId' = {user} AND status IN ('pending', 'running')
            ORDER BY created_at DESC
            LIMIT 1
            """).ToListAsync(cancellationToken))
            .Select(id => (Guid?)id)
            .SingleOrDefault();
    }

    public async Task<IReadOnlyList<Guid>> ListIdsForUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = userId.ToString();
        var type = BuildExport.JobType;
        return await db.Database.SqlQuery<Guid>($"""
            SELECT (payload -> 'data' ->> 'exportId')::uuid AS "Value"
            FROM jobs
            WHERE type = {type} AND payload -> 'data' ->> 'userId' = {user}
            """).ToListAsync(cancellationToken);
    }

    public async Task DeleteForUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = userId.ToString();
        var type = BuildExport.JobType;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM jobs WHERE type = {type} AND payload -> 'data' ->> 'userId' = {user}", cancellationToken);
    }

    private sealed record StateRow(Guid UserId, string Status);
}

public static class ExportInfrastructure
{
    /// <summary>Configuration key of the folder that holds export files.</summary>
    public const string PathKey = "Exports:Path";

    /// <summary>Registers the export file store, the job lookups, the build job and the hourly cleanup. The folder defaults to <c>data/exports</c> beside the app.</summary>
    public static IServiceCollection AddExportStorage(this IServiceCollection services)
    {
        services.TryAddSingleton<ExportSettings>();
        services.TryAddSingleton<IExportStore>(provider =>
            new FileExportStore(provider.GetRequiredService<IConfiguration>()[PathKey] ?? Path.Combine("data", "exports")));
        services.TryAddScoped<IExportJobs, ExportJobs>();
        services.AddScoped<IUserPurgeStep, PurgeUserExports>(); // a deleted person's export files and export jobs go with the account
        services.AddJobHandler<BuildExport.Payload, BuildExport.Handler>(BuildExport.JobType);
        services.AddJobHandler<CleanupExports.Payload, CleanupExports.Handler>(CleanupExports.JobType);
        services.AddRecurringJob(CleanupExports.JobType, TimeSpan.FromHours(1));
        return services;
    }
}
