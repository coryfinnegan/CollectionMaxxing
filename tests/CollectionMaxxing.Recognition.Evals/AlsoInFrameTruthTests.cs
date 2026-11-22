using System.Text.Json;
using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.TestSupport.Evals;
using static CollectionMaxxing.Recognition.Evals.EvalFixtures;

namespace CollectionMaxxing.Recognition.Evals;

/// <summary>
/// A shelf truth may list <c>alsoInFrame</c> items: really in the picture, outside the shelves the truth covers. A read of one is set aside
/// (not predicted, right, wrong or invented); a truth item always wins; one listing sets aside one row.
/// </summary>
public sealed class AlsoInFrameTruthTests : IDisposable
{
    private const string OnShelf = "The Shelf Book";
    private const string Below = "The Stack Below";

    private readonly SyntheticGolden _golden = new();

    public void Dispose() => _golden.Dispose();

    private static GroundedItem Book(string title, string kind = "book") =>
        new(title, null, "model", "matched", [Release(title, platform: null, kind: kind, format: "hardcover")]);

    private static ShelfTruthItem BookItem(string title, string mediaType = "book") => new(title, mediaType, null, null);

    private static Dictionary<string, TruthEntry> Truth(IReadOnlyList<ShelfTruthItem> items, IReadOnlyList<ShelfTruthItem>? alsoInFrame, string id = "s1") =>
        new() { [id] = new TruthEntry(id, GoldenSets.Shelf, new ShelfTruth(items, 0, alsoInFrame), null, null) };

    private static ScoredEngines Score(Dictionary<string, TruthEntry> truths, params GroundedItem[] items) =>
        EvalScorer.Score([Photo("s1", Apple, items)], truths);

    [Fact]
    public void A_read_of_an_item_listed_as_also_in_the_frame_is_set_aside_not_invented()
    {
        var scored = Score(Truth([BookItem(OnShelf)], [BookItem(Below)]), Book(OnShelf), Book(Below));
        var shelf = scored.ShelfOf();

        shelf.InventedTitles.ShouldBe(0);
        shelf.SetAside.ShouldBe(1);
        scored.Misses.ShouldBeEmpty();
    }

    [Fact]
    public void A_confident_read_of_one_is_not_a_wrong_automatic_add()
    {
        var shelf = Score(Truth([BookItem(OnShelf)], [BookItem(Below)]), T1(Book(OnShelf)), T1(Book(Below))).ShelfOf();

        shelf.AutoAdds.ShouldBe(1);
        shelf.CorrectAutoAdds.ShouldBe(1);
        shelf.AutoAddPrecisionPct.ShouldBe(100);
        shelf.T1Auto.ShouldBe(1);
    }

    [Fact]
    public void It_is_not_counted_as_predicted_and_it_claims_no_truth_item()
    {
        // The set-aside row shares a title with nothing on the shelf; the truth item it might have been mistaken for stays a miss.
        var withList = Score(Truth([BookItem(OnShelf)], [BookItem(Below)]), Book(Below)).ShelfOf();

        withList.PredictedItems.ShouldBe(0);
        withList.CorrectItems.ShouldBe(0);
        withList.Top1Hits.ShouldBe(0);
        withList.Top3Hits.ShouldBe(0);
        withList.TruthItems.ShouldBe(1);
        withList.SetAside.ShouldBe(1);
        withList.PrecisionPct.ShouldBeNull();
    }

    [Fact]
    public void A_set_aside_row_changes_no_number_but_its_own_count()
    {
        var without = Score(Truth([BookItem(OnShelf)], null), Book(OnShelf)).ShelfOf();
        var with = Score(Truth([BookItem(OnShelf)], [BookItem(Below)]), Book(OnShelf), T0(Book(Below))).ShelfOf();

        JsonSerializer.Serialize(with, JsonSerializerOptions.Web).ShouldBe(JsonSerializer.Serialize(without with { SetAside = 1 }, JsonSerializerOptions.Web));
    }

    [Fact]
    public void A_row_that_matches_a_truth_item_is_scored_as_today_even_when_the_same_title_is_listed_as_in_the_frame()
    {
        var shelf = Score(Truth([BookItem(OnShelf)], [BookItem(OnShelf)]), Book(OnShelf)).ShelfOf();

        shelf.PredictedItems.ShouldBe(1);
        shelf.CorrectItems.ShouldBe(1);
        shelf.Top1Hits.ShouldBe(1);
        shelf.SetAside.ShouldBeNull();
    }

    [Fact]
    public void A_second_row_of_a_truth_title_is_not_set_aside_either()
    {
        var shelf = Score(Truth([BookItem(OnShelf)], [BookItem(OnShelf)]), Book(OnShelf), Book(OnShelf)).ShelfOf();

        shelf.PredictedItems.ShouldBe(2);
        shelf.CorrectItems.ShouldBe(1);
        shelf.SetAside.ShouldBeNull();
    }

    [Fact]
    public void One_listing_sets_aside_one_row_and_a_second_row_with_the_same_title_is_scored_as_today()
    {
        var shelf = Score(Truth([BookItem(OnShelf)], [BookItem(Below)]), Book(OnShelf), Book(Below), Book(Below)).ShelfOf();

        shelf.SetAside.ShouldBe(1);
        shelf.PredictedItems.ShouldBe(2);
        shelf.InventedTitles.ShouldBe(1);
    }

    [Fact]
    public void Two_listings_of_one_title_set_aside_two_rows()
    {
        var shelf = Score(Truth([BookItem(OnShelf)], [BookItem(Below), BookItem(Below)]), Book(OnShelf), Book(Below), Book(Below), Book(Below)).ShelfOf();

        shelf.SetAside.ShouldBe(2);
        shelf.PredictedItems.ShouldBe(2);
        shelf.InventedTitles.ShouldBe(1);
    }

    [Fact]
    public void The_kind_must_agree_as_for_a_truth_item()
    {
        // The list says a book; the read is the film of the same title: it is invented as today, not set aside.
        var shelf = Score(Truth([BookItem(OnShelf)], [BookItem(Below, "book")]), Book(OnShelf), Book(Below, kind: "film")).ShelfOf();

        shelf.SetAside.ShouldBeNull();
        shelf.InventedTitles.ShouldBe(1);
        shelf.PredictedItems.ShouldBe(2);
    }

    [Fact]
    public void A_listed_full_title_sets_a_row_aside_too()
    {
        var listed = new ShelfTruthItem(Below, "book", null, null, ["The Stack Below: A Memoir"]);

        var shelf = Score(Truth([BookItem(OnShelf)], [listed]), Book(OnShelf), Book("The Stack Below: A Memoir")).ShelfOf();

        shelf.SetAside.ShouldBe(1);
        shelf.InventedTitles.ShouldBe(0);
    }

    [Fact]
    public void A_picture_without_the_list_scores_as_before()
    {
        var shelf = Score(Truth([BookItem(OnShelf)], null), Book(OnShelf), Book(Below)).ShelfOf();

        shelf.SetAside.ShouldBeNull();
        shelf.InventedTitles.ShouldBe(1);
        shelf.PredictedItems.ShouldBe(2);
    }

    [Fact]
    public void A_set_aside_row_still_accounts_for_its_captured_segment()
    {
        var segment = Book(Below) with { SegmentId = "a" };
        var photo = Photo("s1", Apple, [Book(OnShelf) with { SegmentId = "b" }, segment], capturedCount: 2);

        var shelf = EvalScorer.Score([photo], Truth([BookItem(OnShelf)], [BookItem(Below)])).ShelfOf();

        shelf.UnreconciledPhotos.ShouldBe(0);
        shelf.InventedTitles.ShouldBe(0);
    }

    [Fact]
    public void A_missed_truth_item_names_no_set_aside_row_as_its_closest_read()
    {
        var scored = Score(Truth([BookItem("Alpha Beta")], [BookItem("Alpha Betas")]), Book("Alpha Betas"));

        scored.Misses.Single().Read.ShouldBe("(nothing read)");
    }

    [Fact]
    public void The_summary_json_names_the_count_only_when_rows_were_set_aside()
    {
        var with = Score(Truth([BookItem(OnShelf)], [BookItem(Below)]), Book(OnShelf), Book(Below)).ShelfOf();
        var without = Score(Truth([BookItem(OnShelf)], null), Book(OnShelf)).ShelfOf();

        JsonSerializer.Serialize(with, JsonSerializerOptions.Web).ShouldContain("\"setAside\":1");
        JsonSerializer.Serialize(without, JsonSerializerOptions.Web).ShouldNotContain("setAside");
    }

    [Fact]
    public void The_report_says_how_many_rows_were_set_aside_one_line_per_photo()
    {
        var truths = Truth([BookItem(OnShelf)], [BookItem(Below), BookItem("Other Below")]);
        var run = RunOf(Photo("s1", Apple, [Book(OnShelf), Book(Below), Book("Other Below")]));

        var report = EvalReport.Evaluate(run, truths, null, "abc", []).ReportMarkdown;

        report.ShouldContain("### Set aside (2 rows)");
        report.ShouldContain("- `s1`: 2 set aside");
        report.ShouldContain(Below);
    }

    [Fact]
    public void A_report_with_nothing_set_aside_has_no_such_section()
    {
        var truths = Truth([BookItem(OnShelf)], null);

        EvalReport.Evaluate(RunOf(Photo("s1", Apple, [Book(OnShelf)])), truths, null, "abc", []).ReportMarkdown.ShouldNotContain("Set aside");
    }

    [Fact]
    public void Set_aside_rows_are_not_a_compared_metric()
    {
        var with = EvalRegression.Flatten(Score(Truth([BookItem(OnShelf)], [BookItem(Below)]), Book(OnShelf), Book(Below)).Engines);
        var without = EvalRegression.Flatten(Score(Truth([BookItem(OnShelf)], null), Book(OnShelf)).Engines);

        with.Keys.ShouldBe(without.Keys);
        with.Keys.ShouldNotContain(k => k.Contains("aside", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_also_in_frame_list_parses_in_either_spelling_with_the_shape_of_an_item()
    {
        var camel = TruthFiles.Parse("s1", GoldenSets.Shelf, """
            { "items": [{ "title": "A", "mediaType": "book" }],
              "alsoInFrame": [{ "title": "B", "mediaType": "film", "platform": "DVD", "year": 2001, "alsoTitled": ["B: Full"] }, { "title": "C" }], /* c */ }
            """);
        var snake = TruthFiles.Parse("s2", GoldenSets.Shelf, """{ "items": [], "also_in_frame": [{ "title": "B", "media_type": "game" }] }""");

        var listed = camel!.Shelf!.AlsoInFrame!;
        listed.Select(i => (i.Title, i.MediaType, i.Platform, i.Year)).ShouldBe([("B", "film", "DVD", 2001), ("C", "other", null, null)]);
        listed[0].AlsoTitled.ShouldBe(["B: Full"]);
        snake!.Shelf!.AlsoInFrame!.Select(i => (i.Title, i.MediaType)).ShouldBe([("B", "game")]);
    }

    [Fact]
    public void An_absent_empty_or_null_list_means_none()
    {
        foreach (var field in new[] { "", ", \"alsoInFrame\": []", ", \"alsoInFrame\": null", ", \"also_in_frame\": []" })
        {
            var entry = TruthFiles.Parse("s1", GoldenSets.Shelf, "{ \"items\": [{ \"title\": \"A\" }]" + field + " }");

            entry.ShouldNotBeNull();
            entry.Shelf!.AlsoInFrame.ShouldBeNull();
        }
    }

    [Theory]
    [InlineData("\"alsoInFrame\": \"B\"")]
    [InlineData("\"alsoInFrame\": {\"title\": \"B\"}")]
    [InlineData("\"alsoInFrame\": [\"B\"]")]
    [InlineData("\"alsoInFrame\": [{\"mediaType\": \"book\"}]")]
    [InlineData("\"alsoInFrame\": [{\"title\": \"B\", \"alsoTitled\": \"x\"}]")]
    [InlineData("\"also_in_frame\": 4")]
    public void A_malformed_also_in_frame_makes_the_file_invalid(string field)
    {
        TruthFiles.Parse("s1", GoldenSets.Shelf, "{ \"items\": [{ \"title\": \"A\" }], " + field + " }").ShouldBeNull();
    }

    [Fact]
    public void An_also_in_frame_list_over_the_item_limit_makes_the_file_invalid()
    {
        string List(int count) => string.Join(", ", Enumerable.Range(1, count).Select(i => $"{{ \"title\": \"T{i}\" }}"));

        TruthFiles.Parse("s1", GoldenSets.Shelf, "{ \"items\": [], \"alsoInFrame\": [" + List(201) + "] }").ShouldBeNull();
        TruthFiles.Parse("s2", GoldenSets.Shelf, "{ \"items\": [], \"alsoInFrame\": [" + List(200) + "] }")!.Shelf!.AlsoInFrame!.Count.ShouldBe(200);
    }

    [Fact]
    public void A_picture_whose_truth_has_a_malformed_also_in_frame_is_skipped_as_invalid_truth()
    {
        _golden.AddShelf("good", [new SyntheticGolden.ShelfItem(OnShelf, "book")]);
        _golden.AddShelf("bad", null);
        _golden.AddFile("shelf", "bad.truth.json", """{ "items": [{ "title": "A", "mediaType": "book" }], "alsoInFrame": "oops" }""");

        TruthsOf(_golden).Keys.ShouldBe(["good"]);
    }

    [Fact]
    public void A_draft_truth_file_never_carries_the_list()
    {
        var draft = TruthFiles.WriteShelfDraft([new ShelfTruthItem("A", "book", null, null)], 0);

        draft.ShouldNotContain("alsoInFrame", Case.Insensitive);
        draft.ShouldNotContain("also_in_frame", Case.Insensitive);
    }
}
