using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Common;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;

namespace CollectionMaxxing.Application.Tests.Items;

public class CreateItemTests(PostgresFixture db) : DatabaseTest(db)
{
    private static readonly SteppingClock Clock = new(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero));

    private static CreateItem.Command Manual(
        Guid libraryId, string key, ManualRelease? manual, Guid? releaseId = null, string? notes = null) =>
        new(libraryId, key, releaseId, manual, null, null, null, null, notes, null, null, null);

    private static ManualRelease Book(string? title = "A Book", string? barcode = null, Guid? platformId = null) =>
        new(WorkKind.Book, title, null, null, platformId, null, barcode);

    private Task<Result<Versioned<ItemDto>>> RunAsync(TestLibrary library, CreateItem.Command command)
    {
        var caller = library.ActingAs(LibraryRole.Editor);
        var handler = new CreateItem.Handler(Db.CreateContext(caller), new FakeIdGenerator(), caller, Clock);
        return handler.HandleAsync(command, CancellationToken.None);
    }

    [Fact]
    public async Task Giving_both_a_release_and_manual_details_or_neither_is_rejected()
    {
        var library = await TestData.CreateLibraryAsync(Db);

        (await RunAsync(library, Manual(library.Id, "k1", null))).Error!.Code.ShouldBe("item.source_required");
        (await RunAsync(library, Manual(library.Id, "k2", Book(), Guid.CreateVersion7()))).Error!.Code.ShouldBe("item.source_required");
    }

    [Fact]
    public async Task Manual_details_with_a_title_are_accepted()
    {
        var library = await TestData.CreateLibraryAsync(Db);

        (await RunAsync(library, Manual(library.Id, "k1", Book()))).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Notes_over_the_limit_are_rejected()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var notes = new string('n', Domain.Items.Item.MaxNotesLength + 1);

        (await RunAsync(library, Manual(library.Id, "k1", Book(), notes: notes))).Error!.Code.ShouldBe("item.notes_too_long");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_manual_item_needs_a_title(string? title)
    {
        var library = await TestData.CreateLibraryAsync(Db);

        (await RunAsync(library, Manual(library.Id, "k1", Book(title)))).Error!.Code.ShouldBe("item.title_required");
    }

    [Theory]
    [InlineData("1234567X")]
    [InlineData("12345")]
    [InlineData("97803064061571")]
    [InlineData("12345678901AB")]
    public async Task A_manual_barcode_must_be_8_or_13_digits(string barcode)
    {
        var library = await TestData.CreateLibraryAsync(Db);

        (await RunAsync(library, Manual(library.Id, "k1", Book(barcode: barcode)))).Error!.Code.ShouldBe("item.barcode_invalid");
    }

    [Theory]
    [InlineData("12345670")]
    [InlineData("9780306406157")]
    [InlineData("4006381333931")]
    public async Task An_8_or_13_digit_manual_barcode_is_accepted(string barcode)
    {
        var library = await TestData.CreateLibraryAsync(Db);

        (await RunAsync(library, Manual(library.Id, "k1", Book(barcode: barcode)))).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_platform_that_does_not_exist_is_not_found()
    {
        var library = await TestData.CreateLibraryAsync(Db);

        var result = await RunAsync(library, Manual(library.Id, "k1", Book(platformId: Guid.CreateVersion7())));

        result.Error!.Code.ShouldBe("platform.not_found");
    }

    [Fact]
    public async Task Sending_the_same_request_twice_returns_the_same_item_and_a_different_one_with_the_same_key_is_refused()
    {
        var library = await TestData.CreateLibraryAsync(Db);

        var first = await RunAsync(library, Manual(library.Id, "same-key", Book("Dune")));
        var again = await RunAsync(library, Manual(library.Id, "same-key", Book("Dune")));
        var different = await RunAsync(library, Manual(library.Id, "same-key", Book("Emma")));

        first.IsSuccess.ShouldBeTrue();
        again.Value.Value.Id.ShouldBe(first.Value.Value.Id);
        different.Error!.Code.ShouldBe(ItemErrors.KeyReusedCode);
    }
}
