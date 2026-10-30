using CollectionMaxxing.Application.Evals;
using CollectionMaxxing.TestSupport.Evals;
using static CollectionMaxxing.Recognition.Evals.EvalFixtures;

namespace CollectionMaxxing.Recognition.Evals;

/// <summary>
/// A truth names a book by its spine title while a provider answers with title and subtitle. A truth item may therefore list
/// <c>alsoTitled</c> full titles that count as the same book; nothing else about matching is loosened.
/// </summary>
public sealed class AlsoTitledTruthTests : IDisposable
{
    private const string Spine = "The Staub Cookbook";
    private const string Full = "The Staub Cookbook: Modern Recipes for Classic Cast Iron";
    private const string LookAlike = "The Staub Cookbook: Set of Prints";

    private readonly SyntheticGolden _golden = new();

    public void Dispose() => _golden.Dispose();

    private static GroundedCandidate Book(string title) => Release(title, platform: null, kind: "book", format: "hardcover");

    private static GroundedItem MatchedBook(string title) => new(title, null, "model", "matched", [Book(title)]);

    private static SyntheticGolden.ShelfItem BookTruth(params string[] alsoTitled) =>
        new(Spine, "book", AlsoTitled: alsoTitled.Length == 0 ? null : alsoTitled);

    [Fact]
    public void A_shelf_item_can_list_accepted_full_titles_in_either_spelling()
    {
        var camel = TruthFiles.Parse(
            "s1",
            GoldenSets.Shelf,
            """{ "items": [{ "title": "The Staub Cookbook", "mediaType": "book", "alsoTitled": ["Full A", " Full B "] }], "unreadable": 0 }""");
        var snake = TruthFiles.Parse(
            "s2",
            GoldenSets.Shelf,
            """{ "items": [{ "title": "The Staub Cookbook", "media_type": "book", "also_titled": ["Full A"], /* comments and trailing commas are fine */ }], "unreadable": 0 }""");

        camel.ShouldNotBeNull();
        camel.Shelf!.Items.Single().AlsoTitled.ShouldBe(["Full A", "Full B"]);
        snake.ShouldNotBeNull();
        snake.Shelf!.Items.Single().AlsoTitled.ShouldBe(["Full A"]);
    }

    [Fact]
    public void A_single_picture_truth_can_list_accepted_full_titles()
    {
        var single = TruthFiles.Parse(
            "c1",
            GoldenSets.Single,
            """{ "title": "The Staub Cookbook", "format": "hardcover", "completeness": "n_a", "kind": "book_spine", "alsoTitled": ["Full A"] }""");
        var snake = TruthFiles.Parse(
            "c2",
            GoldenSets.Single,
            """{ "title": "The Staub Cookbook", "format": "hardcover", "also_titled": ["Full A"] }""");

        single.ShouldNotBeNull();
        single.Single!.AlsoTitled.ShouldBe(["Full A"]);
        snake.ShouldNotBeNull();
        snake.Single!.AlsoTitled.ShouldBe(["Full A"]);
    }

    [Fact]
    public void A_truth_file_without_also_titled_parses_exactly_as_before()
    {
        var shelf = TruthFiles.Parse("s1", GoldenSets.Shelf, """{ "items": [{ "title": "Star Fox 64", "mediaType": "game", "platform": "Nintendo 64", "year": 1997 }], "unreadable": 1 }""");
        var single = TruthFiles.Parse("c1", GoldenSets.Single, """{ "title": "Star Fox 64", "format": "game_cartridge", "completeness": "cart_only", "kind": "loose_cartridge" }""");
        var empty = TruthFiles.Parse("s2", GoldenSets.Shelf, """{ "items": [{ "title": "Star Fox 64", "mediaType": "game", "alsoTitled": [] }] }""");

        shelf.ShouldNotBeNull();
        shelf.Shelf!.Items.Single().ShouldBe(new ShelfTruthItem("Star Fox 64", "game", "Nintendo 64", 1997));
        shelf.Shelf.Items.Single().AlsoTitled.ShouldBeNull();
        single.ShouldNotBeNull();
        single.Single!.AlsoTitled.ShouldBeNull();
        empty.ShouldNotBeNull();
        empty.Shelf!.Items.Single().AlsoTitled.ShouldBeNull();
    }

    [Theory]
    [InlineData("\"alsoTitled\": \"Full A\"")] // a string, not an array
    [InlineData("\"alsoTitled\": {\"a\": \"Full A\"}")]
    [InlineData("\"alsoTitled\": [\"Full A\", 7]")] // an entry that is not a string
    [InlineData("\"alsoTitled\": [[\"Full A\"]]")]
    [InlineData("\"also_titled\": 12")]
    public void An_also_titled_that_is_not_an_array_of_strings_makes_the_file_invalid(string field)
    {
        TruthFiles.Parse("s1", GoldenSets.Shelf, $$"""{ "items": [{ "title": "The Staub Cookbook", "mediaType": "book", {{field}} }] }""").ShouldBeNull();
        TruthFiles.Parse("c1", GoldenSets.Single, $$"""{ "title": "The Staub Cookbook", "format": "hardcover", {{field}} }""").ShouldBeNull();
    }

    [Fact]
    public void An_also_titled_with_too_many_entries_or_an_over_long_entry_makes_the_file_invalid()
    {
        var eleven = string.Join(", ", Enumerable.Range(1, 11).Select(i => $"\"Full {i}\""));
        var ten = string.Join(", ", Enumerable.Range(1, 10).Select(i => $"\"Full {i}\""));
        var tooLong = new string('x', 501);
        var longest = new string('x', 500);

        TruthFiles.Parse("s1", GoldenSets.Shelf, $$"""{ "items": [{ "title": "T", "alsoTitled": [{{eleven}}] }] }""").ShouldBeNull();
        TruthFiles.Parse("s2", GoldenSets.Shelf, $$"""{ "items": [{ "title": "T", "alsoTitled": ["{{tooLong}}"] }] }""").ShouldBeNull();
        TruthFiles.Parse("c1", GoldenSets.Single, $$"""{ "title": "T", "alsoTitled": [{{eleven}}] }""").ShouldBeNull();
        TruthFiles.Parse("c2", GoldenSets.Single, $$"""{ "title": "T", "alsoTitled": ["{{tooLong}}"] }""").ShouldBeNull();

        TruthFiles.Parse("s3", GoldenSets.Shelf, $$"""{ "items": [{ "title": "T", "alsoTitled": [{{ten}}] }] }""")!.Shelf!.Items.Single().AlsoTitled!.Count.ShouldBe(10);
        TruthFiles.Parse("s4", GoldenSets.Shelf, $$"""{ "items": [{ "title": "T", "alsoTitled": ["{{longest}}"] }] }""")!.Shelf!.Items.Single().AlsoTitled!.Single().ShouldBe(longest);
    }

    [Fact]
    public void A_picture_whose_truth_has_a_malformed_also_titled_is_skipped_as_invalid_truth()
    {
        _golden.AddShelf("good", [BookTruth(Full)]);
        _golden.AddShelf("bad", null);
        _golden.AddFile("shelf", "bad.truth.json", """{ "items": [{ "title": "The Staub Cookbook", "mediaType": "book", "alsoTitled": "oops" }], "unreadable": 0 }""");

        var truths = TruthsOf(_golden);

        truths.Keys.ShouldBe(["good"]);
    }

    [Fact]
    public void A_release_with_a_listed_full_title_is_a_top_1_hit_and_not_invented()
    {
        _golden.AddShelf("s1", [BookTruth(Full)]);

        var shelf = EvalScorer.Score([Photo("s1", Apple, [MatchedBook(Full)])], TruthsOf(_golden)).ShelfOf();

        shelf.Top1Pct.ShouldBe(100);
        shelf.Top3Pct.ShouldBe(100);
        shelf.PrecisionPct.ShouldBe(100);
        shelf.InventedTitles.ShouldBe(0);
    }

    [Fact]
    public void A_listed_title_matches_however_it_is_cased_or_punctuated()
    {
        _golden.AddShelf("s1", [BookTruth(Full)]);

        var shelf = EvalScorer.Score([Photo("s1", Apple, [MatchedBook("the STAUB cookbook - modern recipes for classic cast iron")])], TruthsOf(_golden)).ShelfOf();

        shelf.Top1Pct.ShouldBe(100);
        shelf.InventedTitles.ShouldBe(0);
    }

    [Fact]
    public void The_plain_truth_title_still_matches_when_alternatives_are_listed()
    {
        _golden.AddShelf("s1", [BookTruth(Full)]);

        var shelf = EvalScorer.Score([Photo("s1", Apple, [MatchedBook(Spine)])], TruthsOf(_golden)).ShelfOf();

        shelf.Top1Pct.ShouldBe(100);
        shelf.InventedTitles.ShouldBe(0);
    }

    [Fact]
    public void A_subtitled_title_that_is_not_listed_is_still_a_miss_and_invented()
    {
        _golden.AddShelf("s1", [BookTruth(Full)]);

        var shelf = EvalScorer.Score([Photo("s1", Apple, [MatchedBook(LookAlike)])], TruthsOf(_golden)).ShelfOf();

        shelf.Top1Pct.ShouldBe(0);
        shelf.Top3Pct.ShouldBe(0);
        shelf.InventedTitles.ShouldBe(1);
    }

    [Fact]
    public void Without_a_list_a_subtitled_title_is_a_miss_as_before()
    {
        _golden.AddShelf("s1", [BookTruth()]);

        var shelf = EvalScorer.Score([Photo("s1", Apple, [MatchedBook(Full)])], TruthsOf(_golden)).ShelfOf();

        shelf.Top1Pct.ShouldBe(0);
        shelf.InventedTitles.ShouldBe(1);
    }

    [Fact]
    public void A_listed_full_title_in_second_place_counts_for_top_3_but_not_top_1()
    {
        _golden.AddShelf("s1", [BookTruth(Full)]);
        var ambiguous = Ambiguous(Book(LookAlike), Book(Full), Book("Another Cookbook"));

        var shelf = EvalScorer.Score([Photo("s1", Apple, [ambiguous])], TruthsOf(_golden)).ShelfOf();

        shelf.Top1Pct.ShouldBe(0);
        shelf.Top3Pct.ShouldBe(100);
    }

    [Fact]
    public void A_single_picture_with_a_listed_full_title_is_a_top_1_hit()
    {
        _golden.AddSingle("c1", new SyntheticGolden.SingleItem(Spine, "hardcover", "n_a", EvalKinds.BookSpine, AlsoTitled: [Full]));
        _golden.AddSingle("c2", new SyntheticGolden.SingleItem(Spine, "hardcover", "n_a", EvalKinds.BookSpine));

        var single = EvalScorer.Score(
            [Photo("c1", Apple, [MatchedBook(Full)]), Photo("c2", Apple, [MatchedBook(Full)])],
            TruthsOf(_golden)).SoloOf();

        single.TruthItems.ShouldBe(2);
        single.Top1Pct.ShouldBe(50); // c1 lists the full title; c2 does not and stays a miss
        single.InventedTitles.ShouldBe(1);
    }
}
