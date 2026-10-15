namespace CollectionMaxxing.Application.Exports;

/// <summary>Tuning for data exports. The defaults suit a home server; tests change them.</summary>
public sealed class ExportSettings
{
    /// <summary>How many items are read from the database at a time, so a big library never sits in memory whole.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>How long a finished export file is kept before the cleanup job deletes it.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How long a download link stays valid. Asking for the export's status again gives a fresh one while the file exists.</summary>
    public TimeSpan LinkLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Called with the number of items in each batch read. A diagnostic hook for tests.</summary>
    public Action<int>? BatchObserved { get; set; }
}

/// <summary>Where finished export files live (a folder on the PC).</summary>
public interface IExportStore
{
    /// <summary>Writes the export file. It appears all at once, so a reader never sees half a file; writing again replaces it.</summary>
    Task SaveAsync(Guid exportId, DateTimeOffset createdAt, Func<Stream, CancellationToken, Task> write, CancellationToken cancellationToken);

    bool Exists(Guid exportId);

    /// <summary>The file, or null when there is none (never made, or already cleaned up).</summary>
    Task<Stream?> OpenReadAsync(Guid exportId, CancellationToken cancellationToken);

    /// <summary>Deletes the export file, if there is one. A file that is already gone is fine.</summary>
    Task DeleteAsync(Guid exportId, CancellationToken cancellationToken);

    /// <summary>
    /// Like <see cref="DeleteAsync"/>, but says whether the file is gone: false means it is stuck (locked, say) and still on disk, so the caller
    /// keeps its record of the export and tries again later. Stores that never get stuck can leave the default.
    /// </summary>
    async Task<bool> TryDeleteAsync(Guid exportId, CancellationToken cancellationToken)
    {
        await DeleteAsync(exportId, cancellationToken);
        return true;
    }

    /// <summary>Deletes every export file created before <paramref name="cutoff"/> (and stray half-written ones). Returns how many.</summary>
    Task<int> DeleteCreatedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
}

/// <summary>What the job table says about one export job.</summary>
public sealed record ExportJobState(Guid UserId, string JobStatus);

/// <summary>Finds export jobs. Implemented by Infrastructure, which owns the job table.</summary>
public interface IExportJobs
{
    Task<ExportJobState?> FindAsync(Guid exportId, CancellationToken cancellationToken);

    /// <summary>The export this user already has queued or running, if any.</summary>
    Task<Guid?> FindActiveAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>The ids of every export job of this user, whatever its state.</summary>
    Task<IReadOnlyList<Guid>> ListIdsForUserAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Removes the export jobs of this user from the queue. Their payloads name the person, so they go when the person does.</summary>
    Task DeleteForUserAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Removes just these export jobs of this user (the ones whose files are gone).</summary>
    Task DeleteForUserAsync(Guid userId, IReadOnlyCollection<Guid> exportIds, CancellationToken cancellationToken);
}
