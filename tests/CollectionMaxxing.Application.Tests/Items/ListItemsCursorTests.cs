using System.Text;
using System.Text.Json;
using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;

namespace CollectionMaxxing.Application.Tests.Items;

// Issue #292: an out-of-range cursor key must be cursor_invalid, never a server error.
public class ListItemsCursorTests(PostgresFixture db) : DatabaseTest(db)
{
    private static string Cursor(string key, Guid id)
    {
        var json = JsonSerializer.Serialize(new { K = key, I = id });
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    [Fact]
    public async Task A_year_cursor_beyond_int_range_is_cursor_invalid()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        await TestData.CreateItemAsync(Db, library);
        var cursor = Cursor("99999999999999999999", Guid.CreateVersion7());

        var result = await new ListItems.Handler(Db.CreateContext(library.ActingAs(LibraryRole.Viewer)))
            .HandleAsync(new ListItems.Query(library.Id, null, null, null, null, null, null, null, "year", null, cursor), CancellationToken.None);

        result.Error!.Code.ShouldBe("items.cursor_invalid");
    }
}
