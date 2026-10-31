using System.Text.Json;
using CollectionMaxxing.Api.Tests.Scanning;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.TestSupport.Evals;

namespace CollectionMaxxing.Api.Tests.Evals;

/// <summary>
/// In the last real shelf eval every wrong automatic add was a title only the picture model read, at a place Vision found nothing, that
/// happens to exist in the catalogue. The scanner now keeps such a read for review (<c>unconfirmed</c>), so the eval must count it as
/// review, not as an auto-add that could be wrong. The phone sends the mark with the item, the PC keeps it with the grounded run, and
/// the scorer reads it: a read the server matched but nothing else saw is a T2 whatever tier the phone claimed.
/// </summary>
public class EvalUnconfirmedReadTests(ApiFixture api) : EvalTest(api)
{
    private static readonly SyntheticGolden.ShelfItem[] Shelf =
    [
        new("Star Fox 64", "game_cartridge", "Nintendo 64"),
        new("Wave Race 64", "game_cartridge", "Nintendo 64"),
    ];

    /// <summary>A read the phone is confident of (it claims T1), optionally marked unconfirmed.</summary>
    private static Dictionary<string, object?> ConfidentRead(string title, bool? unconfirmed)
    {
        var read = new Dictionary<string, object?>
        {
            ["title"] = title,
            ["text"] = title,
            ["kind"] = "game",
            ["format"] = "game_cartridge",
            ["platform"] = "Nintendo 64",
            ["resolvedBy"] = "model",
            ["tier"] = "T1",
        };
        if (unconfirmed is { } flag)
        {
            read["unconfirmed"] = flag;
        }

        return read;
    }

    private async Task<JsonElement> LatestRunAsync()
    {
        var text = await File.ReadAllTextAsync(Path.Combine(Golden.Results, "latest-run.json"));
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<JsonElement> ShelfOfAsync(IReadOnlyList<Dictionary<string, object?>> reads)
    {
        Golden.AddShelf("s1", Shelf);
        Games.Results.Add(ScanData.Game("Star Fox 64", "Nintendo 64", 1997, "g1"));
        Games.Results.Add(ScanData.Game("Wave Race 64", "Nintendo 64", 1996, "g2"));
        using var admin = AdminClient();

        var response = await PostRunOkAsync(admin, RunBody(PhotoResult("s1", "apple_model", [.. reads])));

        return response.GetProperty("summary").GetProperty("engines").GetProperty("apple_model").GetProperty("shelf");
    }

    [Fact]
    public async Task A_matched_read_marked_unconfirmed_counts_as_review_not_as_an_auto_add()
    {
        var shelf = await ShelfOfAsync([ConfidentRead("Star Fox 64", unconfirmed: true), ConfidentRead("Wave Race 64", unconfirmed: null)]);

        shelf.GetProperty("autoAdds").GetInt32().ShouldBe(1, "only the read nothing doubts is an auto-add");
        shelf.GetProperty("t1Auto").GetInt32().ShouldBe(1);
        shelf.GetProperty("t2Review").GetInt32().ShouldBe(1);
        shelf.GetProperty("correctAutoAdds").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task A_read_marked_not_unconfirmed_is_scored_as_the_phone_tiered_it()
    {
        var shelf = await ShelfOfAsync([ConfidentRead("Star Fox 64", unconfirmed: false), ConfidentRead("Wave Race 64", unconfirmed: null)]);

        shelf.GetProperty("autoAdds").GetInt32().ShouldBe(2);
        shelf.GetProperty("t1Auto").GetInt32().ShouldBe(2);
        shelf.GetProperty("t2Review").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task The_kept_run_records_which_reads_were_unconfirmed()
    {
        await ShelfOfAsync([ConfidentRead("Star Fox 64", unconfirmed: true), ConfidentRead("Wave Race 64", unconfirmed: null)]);

        var items = (await LatestRunAsync()).GetProperty("photos")[0].GetProperty("items");

        items[0].GetProperty("unconfirmed").GetBoolean().ShouldBeTrue();
        items[1].GetProperty("unconfirmed").GetBoolean().ShouldBeFalse();
    }
}
