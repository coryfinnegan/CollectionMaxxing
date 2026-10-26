using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Api.Tests.Scanning;

// LOCKED tests for the series-volume veto (issue #341, docs/16 §6): veto cases land T2 even when scores exceed
// Matched, and nothing is auto-added. No threshold tuning may override this. Do not weaken, skip or delete.
public class AutoCommitVetoTests : ScanTest
{
    public AutoCommitVetoTests(ApiFixture api)
        : base(api)
    {
    }

    [Fact]
    public async Task Series_volumes_above_matched_land_T2_for_the_person_to_pick()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(34120);
        Products.Know(code, ScanData.Product("One Piece Vol. 1", code));
        Games.Results.Add(ScanData.Game("One Piece Vol. 1", externalId: "v1"));
        Games.Results.Add(ScanData.Game("One Piece Vol. 2", externalId: "v2"));
        Games.Results.Add(ScanData.Game("One Piece Vol. 3", externalId: "v3"));
        var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var candidate = (await ScanAsync(editor, sessionId, code))[0];

        candidate.GetProperty("score").GetDouble().ShouldBeGreaterThanOrEqualTo(0.90, "the top hit clears Matched, and still loses");
        Resolution(candidate).ShouldBe("ambiguous");
        candidate.GetProperty("tier").GetString().ShouldBe("T2");
        Decision(candidate).ShouldBe("undecided");
        candidate.GetProperty("alternatives").GetArrayLength().ShouldBe(3);
        candidate.GetProperty("createdItemId").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        (await ScanReaders.ItemsAsync(Db, library.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Omnibus_against_its_volume_is_a_veto()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(34121);
        Products.Know(code, ScanData.Product("One Piece Omnibus 1", code));
        Games.Results.Add(ScanData.Game("One Piece Omnibus 1", externalId: "o1"));
        Games.Results.Add(ScanData.Game("One Piece Vol. 1", externalId: "v1"));
        var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var candidate = (await ScanAsync(editor, sessionId, code))[0];

        candidate.GetProperty("score").GetDouble().ShouldBeGreaterThanOrEqualTo(0.90, "the top hit clears Matched, and still loses");
        Resolution(candidate).ShouldBe("ambiguous");
        candidate.GetProperty("tier").GetString().ShouldBe("T2");
        (await ScanReaders.ItemsAsync(Db, library.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Single_exact_hit_is_not_vetoed()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(34122);
        Products.Know(code, ScanData.Product("One Piece Vol. 1", code));
        Games.Results.Add(ScanData.Game("One Piece Vol. 1", externalId: "v1"));
        var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var candidate = (await ScanAsync(editor, sessionId, code))[0];

        Resolution(candidate).ShouldBe("matched");
        candidate.GetProperty("tier").GetString().ShouldBe("T0");
        candidate.GetProperty("createdItemId").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.String);
        (await ScanReaders.ItemsAsync(Db, library.Id)).Count.ShouldBe(1);
    }
}
