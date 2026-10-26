using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Items;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Scanning;

// Issue #346 (design §11): POST …/items/{id}:rematch accepts a barcode instead of a release. A barcode of the item's
// own work swaps the release in place, verifies the edition and teaches the catalogue; anything else is refused
// without changing anything.
public class ItemUpgradeFromBarcodeTests(ApiFixture api) : ScanTest(api)
{
    private static string RematchUrl(Guid libraryId, Guid itemId) => $"/api/v1/libraries/{libraryId}/items/{itemId}:rematch";

    /// <summary>Two releases of one work; the second carries <paramref name="code"/>.</summary>
    private async Task<(Guid First, Guid Second)> SameWorkAsync(string title, string code)
    {
        var first = await ItemSeed.AddReleaseAsync(Db, title, WorkKind.Book, ReleaseFormat.Paperback, 1990);
        var workId = await ItemSeed.WorkOfAsync(Db, first);
        var second = await ItemSeed.AddReleaseAsync(Db, title, WorkKind.Book, ReleaseFormat.Hardcover, 1990, workId: workId);
        Identifier.TryParse(code, out var id).ShouldBeTrue();
        await using var context = Db.CreateContext();
        context.ReleaseIdentifiers.Add(new ReleaseIdentifier { ReleaseId = second, Type = id!.Type, Value = id.Value });
        await context.SaveChangesAsync();
        return (first, second);
    }

    private async Task<Guid> UnverifiedItemAsync(
        TestLibrary library, Guid releaseId, ItemStatus status = ItemStatus.Owned, string? notes = null,
        string? lentTo = null, ItemCondition? condition = null, Guid? locationId = null, Guid? tagId = null)
    {
        var item = new Item
        {
            Id = Guid.CreateVersion7(),
            LibraryId = library.Id,
            ReleaseId = releaseId,
            Status = status,
            Condition = condition,
            Completeness = ItemCompleteness.Complete,
            LocationId = locationId,
            Notes = notes,
            LentTo = lentTo,
            LentOn = lentTo is null ? null : new DateOnly(2026, 10, 1),
            CoverChoice = CoverChoice.Release,
            AddedVia = AddedVia.Picture,
            EditionVerified = false,
            CreatedAt = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
            CreatedBy = library.OwnerId,
            UpdatedAt = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
        };
        await using var context = Db.CreateContext();
        context.Items.Add(item);
        await context.SaveChangesAsync();
        if (tagId is { } tag)
        {
            await ItemSeed.TagItemAsync(Db, library.Id, item.Id, tag);
        }

        return item.Id;
    }

    private async Task<Item> ReadItemAsync(Guid itemId)
    {
        await using var context = Db.CreateContext();
        return await context.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.Id == itemId);
    }

    private async Task<List<ReleaseIdentifier>> IdentifiersOfAsync(Guid releaseId)
    {
        await using var context = Db.CreateContext();
        return await context.ReleaseIdentifiers.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.ReleaseId == releaseId).ToListAsync();
    }

    [Fact]
    public async Task Upgrading_from_a_barcode_swaps_the_release_keeps_the_copy_data_and_verifies_the_edition()
    {
        var library = await CreateLibraryAsync();
        var shelf = await TestData.CreateLocationAsync(Db, library.Id, "Shelf");
        var tag = await ItemSeed.AddTagAsync(Db, library.Id, "signed");
        var code = ScanData.Upc(6401);
        var (first, second) = await SameWorkAsync("Dune", code);
        var itemId = await UnverifiedItemAsync(
            library, first,
            status: ItemStatus.Lent, notes: "First printing?", lentTo: "Sam", condition: ItemCondition.VeryGood,
            locationId: shelf, tagId: tag);
        using var editor = As(library, LibraryRole.Editor);

        var response = await editor.PostAsJsonAsync(RematchUrl(library.Id, itemId), new { barcode = code });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var upgraded = await ReadJsonAsync(response);
        upgraded.GetProperty("releaseId").GetGuid().ShouldBe(second);
        upgraded.GetProperty("editionVerified").GetBoolean().ShouldBeTrue();
        upgraded.GetProperty("status").GetString().ShouldBe("lent");
        upgraded.GetProperty("condition").GetString().ShouldBe("very_good");
        upgraded.GetProperty("locationId").GetGuid().ShouldBe(shelf);
        upgraded.GetProperty("notes").GetString().ShouldBe("First printing?");
        upgraded.GetProperty("lentTo").GetString().ShouldBe("Sam");
        upgraded.GetProperty("tagIds").EnumerateArray().Select(t => t.GetGuid()).ShouldBe([tag]);
        response.Headers.ETag.ShouldNotBeNull();
        var stored = await ReadItemAsync(itemId);
        stored.ReleaseId.ShouldBe(second);
        stored.EditionVerified.ShouldBeTrue();
        stored.AddedVia.ShouldBe(AddedVia.Picture);
        Products.Calls.ShouldBe(0, "a known barcode never spends a provider call");
    }

    [Fact]
    public async Task Upgrading_teaches_the_barcode_when_the_providers_answer_it()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(6402);
        var first = await ItemSeed.AddReleaseAsync(Db, "Wave Race 64", WorkKind.Game, ReleaseFormat.GameCard, 1996);
        Products.Know(code, ScanData.Product("Wave Race 64", code));
        Games.Results.Add(ScanData.Game("Wave Race 64"));
        var itemId = await UnverifiedItemAsync(library, first);
        using var editor = As(library, LibraryRole.Editor);

        var response = await editor.PostAsJsonAsync(RematchUrl(library.Id, itemId), new { barcode = code });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var upgraded = await ReadJsonAsync(response);
        var releaseId = upgraded.GetProperty("releaseId").GetGuid();
        releaseId.ShouldNotBe(first, "the providers' cartridge is another release of the same game");
        upgraded.GetProperty("editionVerified").GetBoolean().ShouldBeTrue();
        (await ItemSeed.WorkOfAsync(Db, releaseId)).ShouldBe(await ItemSeed.WorkOfAsync(Db, first));
        var learned = await IdentifiersOfAsync(releaseId);
        learned.Count(i => i.Value == ScanData.Gtin13(code)).ShouldBe(1);
        learned.Single(i => i.Value == ScanData.Gtin13(code)).LearnedBySessionId.ShouldBeNull();
    }

    [Fact]
    public async Task A_barcode_of_another_work_is_refused_naming_the_owning_title_and_nothing_changes()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(6403);
        var mine = await ItemSeed.AddReleaseAsync(Db, "Dune", WorkKind.Book, ReleaseFormat.Paperback, 1990);
        var other = await ItemSeed.AddReleaseAsync(Db, "Dune Messiah", WorkKind.Book, ReleaseFormat.Paperback, 1969);
        Identifier.TryParse(code, out var id).ShouldBeTrue();
        await using (var context = Db.CreateContext())
        {
            context.ReleaseIdentifiers.Add(new ReleaseIdentifier { ReleaseId = other, Type = id!.Type, Value = id.Value });
            await context.SaveChangesAsync();
        }

        var itemId = await UnverifiedItemAsync(library, mine, notes: "Mine");
        var before = await ReadItemAsync(itemId);
        using var editor = As(library, LibraryRole.Editor);

        var response = await editor.PostAsJsonAsync(RematchUrl(library.Id, itemId), new { barcode = code });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = await ReadJsonAsync(response);
        problem.GetProperty("code").GetString().ShouldBe("item.barcode_other_work");
        problem.GetProperty("detail").GetString().ShouldBe("That barcode belongs to Dune Messiah.");
        var after = await ReadItemAsync(itemId);
        after.ReleaseId.ShouldBe(before.ReleaseId);
        after.EditionVerified.ShouldBe(before.EditionVerified);
        after.UpdatedAt.ShouldBe(before.UpdatedAt);
        after.Notes.ShouldBe("Mine");
        (await IdentifiersOfAsync(other)).Count.ShouldBe(1, "a refused barcode teaches nothing");
    }

    [Fact]
    public async Task A_barcode_no_edition_has_is_404_and_nothing_changes()
    {
        var library = await CreateLibraryAsync();
        var mine = await ItemSeed.AddReleaseAsync(Db, "Dune", WorkKind.Book, ReleaseFormat.Paperback, 1990);
        var itemId = await UnverifiedItemAsync(library, mine);
        var before = await ReadItemAsync(itemId);
        using var editor = As(library, LibraryRole.Editor);

        var response = await editor.PostAsJsonAsync(RematchUrl(library.Id, itemId), new { barcode = ScanData.Upc(6404) });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("item.barcode_not_found");
        var after = await ReadItemAsync(itemId);
        after.ReleaseId.ShouldBe(before.ReleaseId);
        after.EditionVerified.ShouldBeFalse();
        after.UpdatedAt.ShouldBe(before.UpdatedAt);
    }

    [Fact]
    public async Task A_barcode_shared_by_two_releases_is_refused_as_ambiguous()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(6405);
        var mine = await ItemSeed.AddReleaseAsync(Db, "Dune", WorkKind.Book, ReleaseFormat.Paperback, 1990);
        var first = await ItemSeed.AddReleaseAsync(Db, "Reused A", WorkKind.Game, ReleaseFormat.GameCartridge, 1996);
        var second = await ItemSeed.AddReleaseAsync(Db, "Reused B", WorkKind.Game, ReleaseFormat.GameCartridge, 1996);
        Identifier.TryParse(code, out var id).ShouldBeTrue();
        await using (var context = Db.CreateContext())
        {
            context.ReleaseIdentifiers.Add(new ReleaseIdentifier { ReleaseId = first, Type = id!.Type, Value = id.Value });
            context.ReleaseIdentifiers.Add(new ReleaseIdentifier { ReleaseId = second, Type = id.Type, Value = id.Value });
            await context.SaveChangesAsync();
        }

        var itemId = await UnverifiedItemAsync(library, mine);
        using var editor = As(library, LibraryRole.Editor);

        var response = await editor.PostAsJsonAsync(RematchUrl(library.Id, itemId), new { barcode = code });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("item.barcode_ambiguous");
        (await ReadItemAsync(itemId)).EditionVerified.ShouldBeFalse();
    }

    [Fact]
    public async Task An_unreadable_barcode_is_a_validation_problem()
    {
        var library = await CreateLibraryAsync();
        var mine = await ItemSeed.AddReleaseAsync(Db, "Dune", WorkKind.Book, ReleaseFormat.Paperback, 1990);
        var itemId = await UnverifiedItemAsync(library, mine);
        using var editor = As(library, LibraryRole.Editor);

        var response = await editor.PostAsJsonAsync(RematchUrl(library.Id, itemId), new { barcode = "not a code!!!" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("item.barcode_invalid");
        (await ReadItemAsync(itemId)).EditionVerified.ShouldBeFalse();
    }

    [Fact]
    public async Task A_release_and_a_barcode_together_are_refused()
    {
        var library = await CreateLibraryAsync();
        var mine = await ItemSeed.AddReleaseAsync(Db, "Dune", WorkKind.Book, ReleaseFormat.Paperback, 1990);
        var itemId = await UnverifiedItemAsync(library, mine);
        using var editor = As(library, LibraryRole.Editor);

        var response = await editor.PostAsJsonAsync(RematchUrl(library.Id, itemId), new { releaseId = mine, barcode = ScanData.Upc(6406) });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("item.release_or_barcode");
        (await ReadItemAsync(itemId)).EditionVerified.ShouldBeFalse();
    }

    [Fact]
    public async Task Upgrading_an_unknown_item_is_404()
    {
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);

        var response = await editor.PostAsJsonAsync(RematchUrl(library.Id, Guid.NewGuid()), new { barcode = ScanData.Upc(6407) });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("item.not_found");
    }

    [Fact]
    public async Task Rescanning_the_same_edition_marks_it_verified_without_changing_the_release()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(6408);
        var (_, second) = await SameWorkAsync("Dune", code);
        var itemId = await UnverifiedItemAsync(library, second);
        using var editor = As(library, LibraryRole.Editor);

        var response = await editor.PostAsJsonAsync(RematchUrl(library.Id, itemId), new { barcode = code });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var upgraded = await ReadJsonAsync(response);
        upgraded.GetProperty("releaseId").GetGuid().ShouldBe(second);
        upgraded.GetProperty("editionVerified").GetBoolean().ShouldBeTrue();
    }
}
