using System.Text.Json;
using CollectionMaxxing.Api.Tests.Catalog;
using CollectionMaxxing.Api.Tests.Scanning;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Items;
using CollectionMaxxing.TestSupport;
using CollectionMaxxing.TestSupport.Evals;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Evals;

/// <summary>
/// The background grounding acts as the person who opened the run, with the libraries they belong to now, as every job does and as the one-shot
/// route does inside its request. So a release a library made for itself is seen by the eval exactly when the scanner would see it for them.
/// </summary>
public class EvalOpenRunOpenerTests(ApiFixture api) : OpenRunTest(api)
{
    private const string Title = "Zorblax Quest";

    private static readonly SyntheticGolden.SingleItem ZorblaxTruth = new(Title, "game_cartridge", "cart_only", "loose_cartridge");

    private static object ZorblaxRead() => ReadItem(Title, "game", "game_cartridge", completeness: "cart_only", resolvedBy: "model");

    /// <summary>The platform administrator who is also a member of <paramref name="library"/> (the owner of the PC usually is).</summary>
    private HttpClient AdminMember(TestLibrary library)
    {
        var client = AsUser(library.OwnerId);
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "admin");
        return client;
    }

    private async Task<TestLibrary> LibraryWithItsOwnReleaseAsync()
    {
        var library = await CreateLibraryAsync();
        await CatalogSeed.AddAsync(Db, Title, WorkKind.Game, 1997, ownerLibraryId: library.Id, format: ReleaseFormat.GameCartridge);
        Golden.AddSingle("c1", ZorblaxTruth);
        return library;
    }

    private async Task<IReadOnlyList<string?>> BestTitlesInLatestRunAsync() =>
        [.. (await LatestRunFileAsync()).GetProperty("photos").EnumerateArray()
            .Select(photo => photo.GetProperty("items")[0].GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("title").GetString()).FirstOrDefault())];

    private static async Task RunInBackgroundAsync(HttpClient opener)
    {
        await OpenOkAsync(opener);
        await PutPhotoOkAsync(opener, "c1", AllEngines("c1", ZorblaxRead));
        await FinishOkAsync(opener);
        await StatusWhenDoneAsync(opener);
    }

    [Fact]
    public async Task A_release_the_openers_library_made_for_itself_is_found_by_the_background_grounding_as_the_one_shot_route_finds_it()
    {
        var library = await LibraryWithItsOwnReleaseAsync();
        using var opener = AdminMember(library);

        await PostRunOkAsync(opener, RunBody(AllEngines("c1", ZorblaxRead)));
        var oneShot = await BestTitlesInLatestRunAsync();
        await RunInBackgroundAsync(opener);
        var background = await BestTitlesInLatestRunAsync();

        oneShot.ShouldBe([Title, Title, Title], "the one-shot route sees the library's own release");
        background.ShouldBe(oneShot);
    }

    [Fact]
    public async Task That_release_is_not_found_once_the_opener_has_left_the_library()
    {
        var library = await LibraryWithItsOwnReleaseAsync();
        using var opener = AdminMember(library);
        await OpenOkAsync(opener);
        await using (var context = Db.CreateContext())
        {
            await context.LibraryMembers.IgnoreQueryFilters().Where(m => m.UserId == library.OwnerId).ExecuteDeleteAsync();
        }

        await PutPhotoOkAsync(opener, "c1", AllEngines("c1", ZorblaxRead));
        await FinishOkAsync(opener);
        await StatusWhenDoneAsync(opener);

        (await BestTitlesInLatestRunAsync()).ShouldBe([null, null, null], "the memberships are read when the photo is grounded, not when the run was opened");
    }

    [Fact]
    public async Task A_run_opened_by_an_administrator_in_no_library_sees_global_releases_only()
    {
        _ = await LibraryWithItsOwnReleaseAsync();
        await CatalogAsync("Star Fox 64", ScanData.Upc(7001), ReleaseFormat.GameCartridge, WorkKind.Game, 1997);
        Golden.AddSingle("c2", StarFoxTruth);
        using var opener = AdminClient();
        await OpenOkAsync(opener);

        await PutPhotoOkAsync(opener, "c1", AllEngines("c1", ZorblaxRead));
        await PutPhotoOkAsync(opener, "c2", AllEngines("c2", StarFoxRead));
        await FinishOkAsync(opener);
        await StatusWhenDoneAsync(opener);

        var titles = (await LatestRunFileAsync()).GetProperty("photos").EnumerateArray()
            .ToDictionary(
                photo => $"{photo.GetProperty("photoId").GetString()}/{photo.GetProperty("engine").GetString()}",
                photo => photo.GetProperty("items")[0].GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("title").GetString()).FirstOrDefault());
        titles["c1/apple_model"].ShouldBeNull("the library's own release is private to its members");
        titles["c2/apple_model"].ShouldBe("Star Fox 64");
    }

    [Fact]
    public async Task The_opener_is_kept_with_the_run_so_a_restarted_API_still_grounds_as_them()
    {
        var library = await LibraryWithItsOwnReleaseAsync();
        using var opener = AdminMember(library);
        await OpenOkAsync(opener);

        var header = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(OpenRunFolder, "run.json"))).RootElement;

        header.GetProperty("openedBy").GetGuid().ShouldBe(library.OwnerId);
    }
}
