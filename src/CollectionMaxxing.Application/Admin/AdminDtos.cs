using CollectionMaxxing.Domain.Operations;

namespace CollectionMaxxing.Application.Admin;

/// <summary>Usage of the AI helper for one user on one day.</summary>
public sealed record AiUsageRowDto(DateOnly Day, Guid? UserId, int Requests, decimal CostUsd);

public sealed record AiUsageDto(IReadOnlyList<AiUsageRowDto> Rows);

/// <summary>A background job that is failed or waiting.</summary>
public sealed record AdminJobDto(Guid Id, string Type, JobStatus Status, int Attempts, DateTimeOffset RunAfter, string? LastError);
