using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Application.Tests.Scanning;

/// <summary>
/// A read that names no platform sees first, in a close call, a release on a platform its shot prints (a neighbour's line that
/// is a console's name): the scorer's verdict and every score are untouched, and a read that names its platform, or that
/// matched, is not reordered. The profile weighs the title 0.96 and the year 0.04 and the platform nothing, so one year apart
/// costs exactly 0.01. Neutral titles throughout.
/// </summary>
public class TextSearchPrintedPlatformTests
{
    private const string Title = "Quiet Harbor Atlas";

    private static readonly ScoringProfile Profile =
        new("platform-test", new ScoringWeights(Title: 0.96, Type: 0, Platform: 0, Year: 0.04, Creator: 0, Edition: 0, Art: 0), new ScoringThresholds());

    private static readonly ReadItem Kindless = new() { Title = Title };

    private static readonly string[] Ps2 = ["PS2"];

    private sealed record Rel(WorkKind Kind, string? Platform, int? Year, string Name = Title);

    private static LocalPool Pool(params Rel[] releases)
    {
        var items = new List<(CandidateRelease Scoring, ResolutionOption Option)>();
        foreach (var rel in releases)
        {
            var id = Guid.NewGuid();
            items.Add((new CandidateRelease { Key = id.ToString(), Title = rel.Name, Kind = rel.Kind, Platform = rel.Platform, Year = rel.Year }, new ResolutionOption(id, null, 0)));
        }

        return new LocalPool(items, null, new Dictionary<string, Guid>());
    }

    private static BarcodeResolution Score(ReadItem read, LocalPool pool, IReadOnlyCollection<string>? printed, WorkKind? kindContext = null) =>
        TextSearchResolver.Score(
            read, Profile, pool, ProviderAnswers.None, pendingWhenProvidersFailed: false, kindContext: kindContext, printedPlatforms: printed);

    // The options are the pool's releases, found again by id and told by kind, platform and year.
    private static List<string> Names(BarcodeResolution resolution, LocalPool pool)
    {
        var byId = pool.Items.ToDictionary(i => i.Option.LocalReleaseId!.Value, i => i.Scoring);
        return resolution.Options.Select(o => byId[o.LocalReleaseId!.Value]).Select(r => $"{r.Kind}:{r.Platform}:{r.Year}").ToList();
    }

    // ICO's pool: three books, a film and the PS3 and PS2 games, all level, in the providers' order.
    private static LocalPool IcoPool() => Pool(
        new(WorkKind.Book, null, null), new(WorkKind.Book, null, 2001), new(WorkKind.Book, null, 2019),
        new(WorkKind.Film, null, 2017), new(WorkKind.Game, "PS3", 2012), new(WorkKind.Game, "PS2", 2001));

    [Fact]
    public void A_read_that_names_no_platform_offers_first_the_tied_release_on_the_platform_its_shot_prints()
    {
        var pool = IcoPool();
        var plain = Score(Kindless, pool, null);

        var printed = Score(Kindless, pool, Ps2);

        Names(plain, pool).ShouldBe(["Book::", "Book::2001", "Book::2019"], "no print: the scorer's order stands");
        Names(printed, pool).First().ShouldBe("Game:PS2:2001");
        printed.Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        printed.Verdict.ShouldBe(plain.Verdict);
        printed.Options.Select(o => o.Score).ShouldAllBe(s => s == 1.0);
        printed.Steps.ShouldContain(s => s.Provider == "platform" && s.Outcome == "biased");
        plain.Steps.Any(s => s.Provider == "platform").ShouldBeFalse();
    }

    [Fact]
    public void With_no_printed_platform_the_scorers_order_stands()
    {
        var pool = IcoPool();

        Names(Score(Kindless, pool, []), pool).ShouldBe(Names(Score(Kindless, pool, null), pool));
    }

    [Fact]
    public void A_release_on_a_printed_platform_a_hair_below_the_best_comes_first_and_one_further_below_does_not()
    {
        var read = new ReadItem { Title = Title, Year = 2001 };

        var hair = Pool(new(WorkKind.Book, null, 2001), new(WorkKind.Game, "PS2", 2002)); // 1.00 against 0.99
        Names(Score(read, hair, Ps2), hair).First().ShouldBe("Game:PS2:2002");

        var far = Pool(new(WorkKind.Book, null, 2001), new(WorkKind.Game, "PS2", 2004)); // 1.00 against 0.97
        Names(Score(read, far, Ps2), far).First().ShouldBe("Book::2001");
    }

    [Fact]
    public void A_read_that_names_its_platform_is_not_reordered_by_the_shots_print()
    {
        var pool = IcoPool();
        var read = new ReadItem { Title = Title, Platform = "PS3" };

        Names(Score(read, pool, Ps2), pool).ShouldBe(Names(Score(read, pool, null), pool));
    }

    [Fact]
    public void A_matched_read_is_not_reordered_by_the_shots_print()
    {
        var pool = Pool(new(WorkKind.Book, null, 2001), new(WorkKind.Game, "PS2", 2002, "Another Tale Entirely"));
        var read = new ReadItem { Title = Title, Year = 2001 };

        var result = Score(read, pool, Ps2);

        result.Verdict.ShouldBe(ResolutionVerdict.Matched);
        Names(result, pool).First().ShouldBe("Book::2001");
        result.Steps.Any(s => s.Provider == "platform").ShouldBeFalse();
    }

    [Fact]
    public void On_a_tie_with_a_release_of_the_snaps_kind_the_release_on_the_printed_platform_comes_first()
    {
        var pool = Pool(new(WorkKind.Book, null, 2001), new(WorkKind.Film, null, 2017), new(WorkKind.Game, "PS2", 2001));

        var result = Score(Kindless, pool, Ps2, kindContext: WorkKind.Film);

        Names(result, pool).ShouldBe(["Game:PS2:2001", "Film::2017", "Book::2001"]);
    }

    [Fact]
    public void The_scorers_best_stays_among_the_three_offered()
    {
        // The best is a book at 1.00; four games on the printed platform at 0.99 all sort above it.
        var pool = Pool(
            new(WorkKind.Book, null, 2001),
            new(WorkKind.Game, "PS2", 2002), new(WorkKind.Game, "PS2", 2000), new(WorkKind.Game, "PS2", 2002), new(WorkKind.Game, "PS2", 2000));

        var result = Score(new ReadItem { Title = Title, Year = 2001 }, pool, Ps2);

        Names(result, pool).ShouldContain("Book::2001");
    }
}
