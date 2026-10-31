using CollectionMaxxing.Api.Tests.Scanning.Quota;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Scanning.Learning;

/// <summary>A scanned release lists the barcodes the scanning library may use, never one only another library taught by hand.</summary>
public class LearnedBarcodeVisibilityTests(ApiFixture api) : UpcScanTest(api, dailyLimit: 1000)
{
    [Fact]
    public async Task A_scanned_candidates_release_lists_only_the_barcodes_the_scanning_library_may_use()
    {
        var teacher = await CreateLibraryAsync();
        var other = await CreateLibraryAsync();
        var shared = ScanData.Upc(6301);
        var taught = ScanData.Upc(6302);
        var release = await CatalogAsync("Visible Release", shared);
        Identifier.TryParse(taught, out var taughtId).ShouldBeTrue();
        await using (var context = Db.CreateContext())
        {
            context.ReleaseIdentifiers.Add(
                new ReleaseIdentifier { ReleaseId = release, Type = taughtId!.Type, Value = taughtId.Value, LibraryId = teacher.Id });
            await context.SaveChangesAsync();
        }

        using var teacherEditor = As(teacher, LibraryRole.Editor);
        using var otherEditor = As(other, LibraryRole.Editor);
        var mine = (await ScanAsync(teacherEditor, await StartSessionAsync(teacherEditor, teacher.Id), shared))[0];
        var theirs = (await ScanAsync(otherEditor, await StartSessionAsync(otherEditor, other.Id), shared))[0];

        Resolution(mine).ShouldBe("matched");
        Resolution(theirs).ShouldBe("matched");
        Codes(mine).ShouldContain(taughtId.Value);
        Codes(theirs).ShouldNotContain(taughtId.Value);
        Codes(theirs).Count.ShouldBeGreaterThan(0);
    }

    private static List<string> Codes(System.Text.Json.JsonElement candidate) =>
        [.. candidate.GetProperty("release").GetProperty("identifiers").EnumerateArray().Select(i => i.GetProperty("value").GetString()!)];
}
