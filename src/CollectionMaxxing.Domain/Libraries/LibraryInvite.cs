using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Domain.Libraries;

/// <summary>A single-use, revocable link that lets one person join a library. Only the hash of the token is stored.</summary>
public sealed class LibraryInvite : ILibraryScoped, IConcurrencyVersioned
{
    public const int MaxNameLength = 100;

    public Guid Id { get; init; }

    public Guid LibraryId { get; init; }

    /// <summary>Editor or viewer. An invite can never make an owner.</summary>
    public LibraryRole Role { get; init; }

    /// <summary>Who the invite is for.</summary>
    public string Name { get; init; } = string.Empty;

    public string TokenHash { get; init; } = string.Empty;

    public Guid CreatedBy { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Seven days after creation.</summary>
    public DateTimeOffset ExpiresAt { get; init; }

    public Guid? AcceptedBy { get; set; }

    public DateTimeOffset? AcceptedAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public uint Version { get; private set; }
}
