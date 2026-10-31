using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Locations;
using CollectionMaxxing.Domain.Scanning;
using CollectionMaxxing.Infrastructure.Identity;
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
        foreach (var userId in new[] { library.OwnerId, library.EditorId, library.ViewerId, library.OutsiderId })
        {
            context.Users.Add(NewUser(userId));
        }

        context.Libraries.Add(new Library(library.Id, name, Now));
        context.LibraryMembers.AddRange(
            new LibraryMember(library.Id, library.OwnerId, LibraryRole.Owner, Now),
            new LibraryMember(library.Id, library.EditorId, LibraryRole.Editor, Now),
            new LibraryMember(library.Id, library.ViewerId, LibraryRole.Viewer, Now));
        await context.SaveChangesAsync();
        return library;
    }

    public static AppUser NewUser(Guid id) => new()
    {
        Id = id,
        UserName = id.ToString("N"),
        NormalizedUserName = id.ToString("N").ToUpperInvariant(),
        DisplayName = "Test user",
        CreatedAt = Now,
    };

    /// <summary>Adds a user who belongs to no library.</summary>
    public static async Task<Guid> CreateUserAsync(PostgresFixture db)
    {
        var id = Guid.CreateVersion7();
        await using var context = db.CreateContext();
        context.Users.Add(NewUser(id));
        await context.SaveChangesAsync();
        return id;
    }

    /// <summary>Adds a work with one release. A release owned by a library is private to it; otherwise it is global.</summary>
    public static async Task<Guid> CreateReleaseAsync(PostgresFixture db, Guid? ownerLibraryId = null, string title = "Jurassic Park")
    {
        var work = new Work { Id = Guid.CreateVersion7(), Kind = WorkKind.Film, Title = title, SortTitle = title.ToLowerInvariant(), NormalizedTitle = title.ToLowerInvariant(), CreatedAt = Now };
        var release = new Release
        {
            Id = Guid.CreateVersion7(),
            WorkId = work.Id,
            Format = ReleaseFormat.Dvd,
            Source = ReleaseSource.Provider,
            OwnerLibraryId = ownerLibraryId,
            CreatedAt = Now,
        };

        await using var context = db.CreateContext();
        context.Works.Add(work);
        context.Releases.Add(release);
        await context.SaveChangesAsync();
        return release.Id;
    }

    /// <summary>Adds an item (of a fresh release unless one is given) and returns its id.</summary>
    public static async Task<Guid> CreateItemAsync(
        PostgresFixture db,
        TestLibrary library,
        Guid? releaseId = null,
        Guid? locationId = null,
        string? customTitle = null,
        ItemStatus status = ItemStatus.Owned,
        TimeSpan? addedAfter = null)
    {
        var item = new Item
        {
            Id = Guid.CreateVersion7(),
            LibraryId = library.Id,
            ReleaseId = releaseId ?? await CreateReleaseAsync(db),
            LocationId = locationId,
            CustomTitle = customTitle,
            Status = status,
            CoverChoice = CoverChoice.Release,
            AddedVia = AddedVia.Manual,
            CreatedAt = Now + (addedAfter ?? TimeSpan.Zero),
            CreatedBy = library.OwnerId,
            UpdatedAt = Now + (addedAfter ?? TimeSpan.Zero),
        };

        await using var context = db.CreateContext();
        context.Items.Add(item);
        await context.SaveChangesAsync();
        return item.Id;
    }

    /// <summary>Adds an open scan session started by <paramref name="userId"/> (the library's editor unless given) and returns its id.</summary>
    public static async Task<Guid> CreateScanSessionAsync(PostgresFixture db, TestLibrary library, Guid? userId = null)
    {
        var session = new ScanSession
        {
            Id = Guid.CreateVersion7(),
            LibraryId = library.Id,
            UserId = userId ?? library.EditorId,
            Status = ScanSessionStatus.Open,
            CreatedAt = Now,
        };

        await using var context = db.CreateContext();
        context.ScanSessions.Add(session);
        await context.SaveChangesAsync();
        return session.Id;
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

    /// <summary>Reads a library's locations straight from the table, ignoring the caller filter.</summary>
    public static async Task<List<Location>> ReadLocationsAsync(PostgresFixture db, Guid libraryId)
    {
        await using var context = db.CreateContext();
        return await context.Locations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(l => l.LibraryId == libraryId)
            .OrderBy(l => l.SortOrder)
            .ThenBy(l => l.Name)
            .ToListAsync();
    }

    /// <summary>Adds a tag straight to the table and returns its id. Names are unique per library, ignoring case.</summary>
    public static async Task<Guid> CreateTagAsync(PostgresFixture db, Guid libraryId, string name)
    {
        var tag = new Tag { Id = Guid.CreateVersion7(), LibraryId = libraryId, Name = name };

        await using var context = db.CreateContext();
        context.Tags.Add(tag);
        await context.SaveChangesAsync();
        return tag.Id;
    }

    /// <summary>Puts a tag on an item, in the same library.</summary>
    public static async Task AttachTagAsync(PostgresFixture db, Guid libraryId, Guid itemId, Guid tagId)
    {
        await using var context = db.CreateContext();
        context.ItemTags.Add(new ItemTag { LibraryId = libraryId, ItemId = itemId, TagId = tagId });
        await context.SaveChangesAsync();
    }

    /// <summary>Counts a library's item-tag links straight from the table, ignoring the caller filter.</summary>
    public static async Task<int> CountItemTagsAsync(PostgresFixture db, Guid libraryId)
    {
        await using var context = db.CreateContext();
        return await context.ItemTags.IgnoreQueryFilters().CountAsync(t => t.LibraryId == libraryId);
    }

    /// <summary>Reads a library's tag names straight from the table, ignoring the caller filter.</summary>
    public static async Task<List<string>> ReadTagNamesAsync(PostgresFixture db, Guid libraryId)
    {
        await using var context = db.CreateContext();
        return await context.Tags.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.LibraryId == libraryId)
            .Select(t => t.Name)
            .ToListAsync();
    }
}
