using CollectionMaxxing.Application.Common;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Infrastructure.Tests.Persistence;

/// <summary>Defence in depth: even a query with no Where clause only sees the caller's libraries.</summary>
public class LibraryIsolationTests(PostgresFixture db) : DatabaseTest(db)
{
    [Fact]
    public async Task A_member_sees_only_the_locations_of_their_own_library()
    {
        var mine = await TestData.CreateLibraryAsync(Db);
        var theirs = await TestData.CreateLibraryAsync(Db);
        await TestData.CreateLocationAsync(Db, mine.Id, "My shelf");
        await TestData.CreateLocationAsync(Db, theirs.Id, "Their shelf");

        await using var context = Db.CreateContext(mine.ActingAs(LibraryRole.Viewer));
        var names = await context.Locations.Select(l => l.Name).ToListAsync();

        names.ShouldBe(["My shelf"]);
    }

    [Fact]
    public async Task A_caller_with_no_library_sees_nothing()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        await TestData.CreateLocationAsync(Db, library.Id, "Shelf");

        await using var context = Db.CreateContext();

        (await context.Locations.CountAsync()).ShouldBe(0);
        (await context.Libraries.CountAsync()).ShouldBe(0);
        (await context.LibraryMembers.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task A_member_sees_their_library_and_its_members_but_not_other_libraries()
    {
        var mine = await TestData.CreateLibraryAsync(Db, "Mine");
        await TestData.CreateLibraryAsync(Db, "Theirs");

        await using var context = Db.CreateContext(mine.ActingAs(LibraryRole.Editor));

        (await context.Libraries.Select(l => l.Name).ToListAsync()).ShouldBe(["Mine"]);
        (await context.LibraryMembers.CountAsync()).ShouldBe(3);
    }

    [Fact]
    public async Task Ignoring_query_filters_is_the_explicit_way_to_see_everything()
    {
        var first = await TestData.CreateLibraryAsync(Db);
        var second = await TestData.CreateLibraryAsync(Db);
        await TestData.CreateLocationAsync(Db, first.Id, "A");
        await TestData.CreateLocationAsync(Db, second.Id, "B");

        await using var context = Db.CreateContext();

        (await context.Locations.IgnoreQueryFilters().CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task The_filter_follows_the_callers_memberships_as_they_change_within_one_context()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        await TestData.CreateLocationAsync(Db, library.Id, "Shelf");
        var caller = new CurrentUser();
        await using var context = Db.CreateContext(caller);

        (await context.Locations.CountAsync()).ShouldBe(0);

        caller.Initialize(library.ViewerId, [new Membership(library.Id, LibraryRole.Viewer)]);

        (await context.Locations.CountAsync()).ShouldBe(1);
    }
}
