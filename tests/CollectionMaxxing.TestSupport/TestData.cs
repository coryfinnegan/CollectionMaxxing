using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Locations;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.TestSupport;

/// <summary>A library with one member in each role, plus a user who belongs to nothing.</summary>
public sealed record TestLibrary(Guid Id, Guid OwnerId, Guid EditorId, Guid ViewerId, Guid OutsiderId)
{
    public Guid UserWithRole(LibraryRole role) => role switch
    {
        LibraryRole.Owner => OwnerId,
        LibraryRole.Editor => EditorId,
        LibraryRole.Viewer => ViewerId,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    /// <summary>The caller as it looks when a member with <paramref name="role"/> makes a request.</summary>
    public CurrentUser ActingAs(LibraryRole role)
    {
        var user = new CurrentUser();
        user.Initialize(UserWithRole(role), [new Membership(Id, role)]);
        return user;
    }
}

/// <summary>Helpers that put known rows in the database. They bypass the handlers on purpose.</summary>
public static class TestData
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static async Task<TestLibrary> CreateLibraryAsync(PostgresFixture db, string name = "Test library")
    {
        var library = new TestLibrary(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());

        await using var context = db.CreateContext();
        context.Libraries.Add(new Library(library.Id, name, Now));
        context.LibraryMembers.AddRange(
            new LibraryMember(library.Id, library.OwnerId, LibraryRole.Owner, Now),
            new LibraryMember(library.Id, library.EditorId, LibraryRole.Editor, Now),
            new LibraryMember(library.Id, library.ViewerId, LibraryRole.Viewer, Now));
        await context.SaveChangesAsync();
        return library;
    }

    /// <summary>Adds a location through the domain rules and returns its id.</summary>
    public static async Task<Guid> CreateLocationAsync(
        PostgresFixture db, Guid libraryId, string name, Guid? parentId = null, int? sortOrder = null)
    {
        var caller = new CurrentUser();
        caller.Initialize(Guid.CreateVersion7(), [new Membership(libraryId, LibraryRole.Owner)]);

        await using var context = db.CreateContext(caller);
        var existing = await context.Locations.Where(l => l.LibraryId == libraryId).ToListAsync();
        var added = new LocationTree(libraryId, existing).Add(Guid.CreateVersion7(), parentId, name, sortOrder);

        context.Locations.Add(added.Value);
        await context.SaveChangesAsync();
        return added.Value.Id;
    }
}
