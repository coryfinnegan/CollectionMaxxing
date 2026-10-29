using CollectionMaxxing.Application.Items;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;

namespace CollectionMaxxing.Application.Tests.Items;

// Issue #353: stored provider titles display headline-cased (even records stored before the fix),
// while a person's own CustomTitle always displays verbatim.
public class ItemTitleCasingTests(PostgresFixture db) : DatabaseTest(db)
{
    [Fact]
    public async Task The_list_shows_a_sentence_case_stored_title_headline_cased()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var releaseId = await TestData.CreateReleaseAsync(Db, title: "so you want to be a wizard");
        await TestData.CreateItemAsync(Db, library, releaseId);

        var result = await new ListItems.Handler(Db.CreateContext(library.ActingAs(LibraryRole.Viewer)))
            .HandleAsync(new ListItems.Query(library.Id, null, null, null, null, null, null, null, null, null, null), CancellationToken.None);

        result.Value!.Items.Single().Title.ShouldBe("So You Want to Be a Wizard");
    }

    [Fact]
    public async Task The_list_shows_a_custom_title_verbatim_never_re_cased()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var releaseId = await TestData.CreateReleaseAsync(Db, title: "so you want to be a wizard");
        await TestData.CreateItemAsync(Db, library, releaseId, customTitle: "my favourite wizard book");

        var result = await new ListItems.Handler(Db.CreateContext(library.ActingAs(LibraryRole.Viewer)))
            .HandleAsync(new ListItems.Query(library.Id, null, null, null, null, null, null, null, null, null, null), CancellationToken.None);

        result.Value!.Items.Single().Title.ShouldBe("my favourite wizard book");
    }

    [Fact]
    public async Task The_detail_shows_the_work_and_release_titles_headline_cased()
    {
        var library = await TestData.CreateLibraryAsync(Db);
        var releaseId = await TestData.CreateReleaseAsync(Db, title: "lantern road: from harbor towns to mountain passes");
        var itemId = await TestData.CreateItemAsync(Db, library, releaseId);

        var result = await new GetItem.Handler(Db.CreateContext(library.ActingAs(LibraryRole.Viewer)))
            .HandleAsync(new GetItem.Query(library.Id, itemId), CancellationToken.None);

        result.Value!.Value.Work.Title.ShouldBe("Lantern Road: From Harbor Towns to Mountain Passes");
        result.Value.Value.Release.Title.ShouldBe("Lantern Road: From Harbor Towns to Mountain Passes");
    }
}
