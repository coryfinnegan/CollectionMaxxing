using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.Domain.Locations;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CollectionMaxxing.Infrastructure.Tests.Persistence;

public class SchemaTests(PostgresFixture db) : DatabaseTest(db)
{
    [Fact]
    public async Task Migrations_create_exactly_the_tables_of_this_slice_with_snake_case_names()
    {
        await using var connection = new NpgsqlConnection(Db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "select table_name from information_schema.tables where table_schema = 'public' order by 1", connection);

        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        tables.ShouldBe(["__EFMigrationsHistory", "library_members", "libraries", "locations"], ignoreOrder: true);
    }

    [Fact]
    public async Task Columns_use_snake_case()
    {
        await using var connection = new NpgsqlConnection(Db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "select column_name from information_schema.columns where table_name = 'locations' order by 1", connection);

        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(0));
        }

        columns.ShouldBe(["id", "library_id", "name", "parent_id", "sort_order"], ignoreOrder: true);
    }

    [Fact]
    public async Task Roles_are_stored_as_lowercase_text()
    {
        await TestData.CreateLibraryAsync(Db);

        await using var connection = new NpgsqlConnection(Db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("select role from library_members order by 1", connection);
        var roles = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            roles.Add(reader.GetString(0));
        }

        roles.ShouldBe(["editor", "owner", "viewer"]);
    }

    [Fact]
    public async Task A_library_can_have_only_one_owner()
    {
        var library = await TestData.CreateLibraryAsync(Db);

        await using var context = Db.CreateContext();
        context.LibraryMembers.Add(new LibraryMember(library.Id, Guid.CreateVersion7(), LibraryRole.Owner, DateTimeOffset.UnixEpoch));

        await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Two_top_level_locations_in_one_library_cannot_share_a_name()
    {
        // NULL parents must count as equal, or the rule would silently not apply to top-level locations.
        var library = await TestData.CreateLibraryAsync(Db);
        await TestData.CreateLocationAsync(Db, library.Id, "Garage");

        await using var context = Db.CreateContext();
        var tree = new LocationTree(library.Id, []);
        context.Locations.Add(tree.Add(Guid.CreateVersion7(), null, "Garage", null).Value);

        await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Two_libraries_can_use_the_same_top_level_name()
    {
        var first = await TestData.CreateLibraryAsync(Db);
        var second = await TestData.CreateLibraryAsync(Db);

        await TestData.CreateLocationAsync(Db, first.Id, "Garage");
        await TestData.CreateLocationAsync(Db, second.Id, "Garage");
    }

    [Fact]
    public async Task A_location_that_still_has_children_cannot_be_deleted_from_the_database()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var room = await TestData.CreateLocationAsync(Db, library.Id, "Room");
        await TestData.CreateLocationAsync(Db, library.Id, "Shelf", room);

        await using var context = Db.CreateContext(library.ActingAs(LibraryRole.Owner));
        var roomRow = await context.Locations.SingleAsync(l => l.Id == room);
        context.Locations.Remove(roomRow);

        await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }
}
