using CollectionMaxxing.Domain.Operations;

namespace CollectionMaxxing.Application.Admin;

/// <summary>A background job that is failed or waiting.</summary>
public sealed record AdminJobDto(Guid Id, string Type, JobStatus Status, int Attempts, DateTimeOffset RunAfter, string? LastError);
