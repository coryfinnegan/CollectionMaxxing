using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Items;

namespace CollectionMaxxing.Domain.Scanning;

public enum ScanSessionStatus
{
    Open,
    Committed,
    Abandoned,
}

/// <summary>One scanning run. Abandoned after 7 days if nobody commits it.</summary>
public sealed class ScanSession : ILibraryScoped, IConcurrencyVersioned
{
    public Guid Id { get; init; }

    public Guid LibraryId { get; init; }

    public Guid UserId { get; init; }

    public ScanSessionStatus Status { get; set; }

    public Guid? DefaultLocationId { get; set; }

    public ItemStatus? DefaultStatus { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? CommittedAt { get; set; }

    public uint Version { get; private set; }
}
