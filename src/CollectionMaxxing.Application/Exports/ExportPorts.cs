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
}
