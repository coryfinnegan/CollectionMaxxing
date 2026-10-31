using CollectionMaxxing.Domain.Common;

namespace CollectionMaxxing.Domain.Libraries;

/// <summary>A user's membership of a library, with their role. A library has exactly one owner.</summary>
public sealed class LibraryMember : ILibraryScoped, IConcurrencyVersioned
{
    // EF Core materialises entities through this constructor.
    private LibraryMember()
    {
    }

    public LibraryMember(Guid libraryId, Guid userId, LibraryRole role, DateTimeOffset joinedAt)
    {
        LibraryId = libraryId;
        UserId = userId;
        Role = role;
        JoinedAt = joinedAt;
    }

    public Guid LibraryId { get; private set; }

    /// <summary>The user's id: the account this membership belongs to. The membership goes when the account does.</summary>
    public Guid UserId { get; private set; }

    public LibraryRole Role { get; private set; }

    public DateTimeOffset JoinedAt { get; private set; }

    public uint Version { get; private set; }
}
