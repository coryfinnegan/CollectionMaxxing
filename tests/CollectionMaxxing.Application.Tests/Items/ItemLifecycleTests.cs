using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Application.Tests.Items;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class SteppingClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>An audit log that records nothing: these tests are about item rules, not the trail (see the Trash and Audit tests).</summary>
internal sealed class NoAudit : Application.Audit.IAuditLog
{
    public void Record(string action, string? entityType, Guid? entityId, Guid? libraryId, IReadOnlyDictionary<string, object>? details = null)
    {
    }
}

public class ItemLifecycleTests(PostgresFixture db) : DatabaseTest(db)
{
    private static readonly DateTimeOffset Start = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    private async Task<(TestLibrary Library, Guid ItemId, SteppingClock Clock)> ArrangeAsync()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var itemId = await TestData.CreateItemAsync(Db, library);
        return (library, itemId, new SteppingClock(Start));
    }

    private async Task DeleteAsync(TestLibrary library, Guid itemId, SteppingClock clock)
    {
        await using var context = Db.CreateContext(library.ActingAs(LibraryRole.Editor));
        var version = (await context.Items.SingleAsync(i => i.Id == itemId)).Version;
        var result = await new DeleteItem.Handler(context, clock, new NoAudit()).HandleAsync(
            new DeleteItem.Command(library.Id, itemId, $"\"{version}\""), CancellationToken.None);
        result.IsSuccess.ShouldBeTrue();
    }

    private async Task<Domain.Common.Result<Versioned<ItemDto>>> RestoreAsync(TestLibrary library, Guid itemId, SteppingClock clock)
    {
        await using var context = Db.CreateContext(library.ActingAs(LibraryRole.Editor));
        return await new RestoreItem.Handler(context, clock, new NoAudit()).HandleAsync(new RestoreItem.Command(library.Id, itemId), CancellationToken.None);
    }

    [Fact]
    public async Task An_item_can_be_restored_29_days_after_it_was_deleted()
    {
        var (library, itemId, clock) = await ArrangeAsync();
        await DeleteAsync(library, itemId, clock);
        clock.Advance(TimeSpan.FromDays(29));

        var result = await RestoreAsync(library, itemId, clock);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Value.DeletedAt.ShouldBeNull();
    }

    [Fact]
    public async Task An_item_cannot_be_restored_31_days_after_it_was_deleted()
    {
        var (library, itemId, clock) = await ArrangeAsync();
        await DeleteAsync(library, itemId, clock);
        clock.Advance(TimeSpan.FromDays(31));

        var result = await RestoreAsync(library, itemId, clock);

        result.Error!.Code.ShouldBe("item.restore_window_closed");
    }

    [Fact]
    public async Task Restoring_an_item_that_is_not_deleted_just_returns_it()
    {
        var (library, itemId, clock) = await ArrangeAsync();

        var result = await RestoreAsync(library, itemId, clock);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Value.Id.ShouldBe(itemId);
    }

    private async Task<Domain.Common.Result<Versioned<ItemDto>>> UpdateAsync(TestLibrary library, Guid itemId, SteppingClock clock, ItemPatch patch)
    {
        await using var context = Db.CreateContext(library.ActingAs(LibraryRole.Editor));
        var version = (await context.Items.SingleAsync(i => i.Id == itemId)).Version;
        return await new UpdateItem.Handler(context, clock).HandleAsync(
            new UpdateItem.Command(library.Id, itemId, $"\"{version}\"", patch), CancellationToken.None);
    }

    [Fact]
    public async Task Lending_an_item_sets_it_to_lent_and_records_the_borrower_and_the_date_from_the_clock()
    {
        var (library, itemId, clock) = await ArrangeAsync();

        var result = await UpdateAsync(library, itemId, clock, new ItemPatch { LentTo = PatchValue<string?>.Of("Sam") });

        var item = result.Value.Value;
        item.Status.ShouldBe(ItemStatus.Lent);
        item.LentTo.ShouldBe("Sam");
        item.LentOn.ShouldBe(new DateOnly(2026, 6, 1));
    }

    [Fact]
    public async Task A_sold_item_cannot_be_lent()
    {
        var (library, itemId, clock) = await ArrangeAsync();

        var result = await UpdateAsync(
            library, itemId, clock, new ItemPatch { Status = ItemStatus.Sold, LentTo = PatchValue<string?>.Of("Sam") });

        result.Error!.Code.ShouldBe("item.sold_cannot_be_lent");
    }

    [Fact]
    public async Task Clearing_who_it_is_lent_to_keeps_the_status_the_caller_chose()
    {
        var (library, itemId, clock) = await ArrangeAsync();
        await UpdateAsync(library, itemId, clock, new ItemPatch { LentTo = PatchValue<string?>.Of("Sam") });

        var result = await UpdateAsync(
            library, itemId, clock, new ItemPatch { Status = ItemStatus.Owned, LentTo = PatchValue<string?>.Of(null), LentOn = PatchValue<DateOnly?>.Of(null) });

        var item = result.Value.Value;
        item.Status.ShouldBe(ItemStatus.Owned);
        item.LentTo.ShouldBeNull();
        item.LentOn.ShouldBeNull();
    }
}
