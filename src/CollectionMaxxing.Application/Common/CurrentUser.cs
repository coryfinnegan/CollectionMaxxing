using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Application.Common;

/// <summary>One library the caller belongs to, and the role they have in it.</summary>
public sealed record Membership(Guid LibraryId, LibraryRole Role);

/// <summary>
/// Who is making this request. The database filter on <c>library_id</c> reads <see cref="LibraryIds"/>,
/// so a query can never see a library the caller doesn't belong to.
/// </summary>
public interface ICurrentUser
{
    /// <summary>Null when nobody is signed in.</summary>
    Guid? UserId { get; }

    IReadOnlyList<Membership> Memberships { get; }

    IReadOnlyList<Guid> LibraryIds { get; }

    /// <summary>The caller's role in the library, or null when they aren't a member (or it doesn't exist).</summary>
    LibraryRole? RoleIn(Guid libraryId);
}

/// <summary>
/// The scoped, mutable implementation. It starts out anonymous; the API fills it in once per request
/// after authentication. Tests fill it in directly.
/// </summary>
public sealed class CurrentUser : ICurrentUser
{
    private Dictionary<Guid, LibraryRole> _roles = [];

    public Guid? UserId { get; private set; }

    public IReadOnlyList<Membership> Memberships { get; private set; } = [];

    public IReadOnlyList<Guid> LibraryIds { get; private set; } = [];

    public void Initialize(Guid userId, IEnumerable<Membership> memberships)
    {
        var list = memberships.ToList();
        UserId = userId;
        Memberships = list;
        LibraryIds = list.Select(m => m.LibraryId).ToArray();
        _roles = list.ToDictionary(m => m.LibraryId, m => m.Role);
    }

    public LibraryRole? RoleIn(Guid libraryId) => _roles.TryGetValue(libraryId, out var role) ? role : null;
}
