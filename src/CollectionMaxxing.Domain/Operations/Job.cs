namespace CollectionMaxxing.Domain.Operations;

public enum JobStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
}

/// <summary>A unit of background work, picked up with <c>FOR UPDATE SKIP LOCKED</c>.</summary>
public sealed class Job
{
    public Guid Id { get; init; }

    public string Type { get; init; } = string.Empty;

    public string PayloadJson { get; init; } = "{}";

    public JobStatus Status { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset RunAfter { get; set; }

    public string? LockedBy { get; set; }

    public DateTimeOffset? LockedUntil { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}
