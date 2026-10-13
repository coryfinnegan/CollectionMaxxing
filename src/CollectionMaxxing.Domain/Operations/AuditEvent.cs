using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Domain.Operations;

/// <summary>A security-relevant or destructive action, kept for a year. The actor is cleared when their account is purged.</summary>
public sealed class AuditEvent
{
    public Guid Id { get; init; }

    public Guid? ActorUserId { get; set; }

    public Guid? LibraryId { get; init; }

    public string Action { get; init; } = string.Empty;

    public string? EntityType { get; init; }

    public Guid? EntityId { get; init; }

    public DateTimeOffset At { get; init; }

    /// <summary>A hash of the caller's IP address, never the address itself.</summary>
    public string? IpHash { get; init; }

    public string? DetailsJson { get; init; }
}

/// <summary>
/// Which alternative the user picked versus what we ranked first. Feeds the evals.
/// </summary>
public sealed class MatchFeedback : ILibraryScoped
{
    public Guid Id { get; init; }

    public Guid LibraryId { get; init; }

    public Guid CandidateId { get; init; }

    public Guid? RankedFirstReleaseId { get; init; }

    public Guid? PickedReleaseId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>A request we already answered. A replay with the same body gets the stored response; a different body is refused.</summary>
public sealed class IdempotencyKey
{
    public Guid UserId { get; init; }

    public string Key { get; init; } = string.Empty;

    public string RequestHash { get; init; } = string.Empty;

    public int ResponseStatus { get; set; }

    public string? ResponseBodyJson { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }
}
