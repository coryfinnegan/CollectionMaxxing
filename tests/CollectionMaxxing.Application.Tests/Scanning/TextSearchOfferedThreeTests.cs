using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Application.Tests.Scanning;

/// <summary>
/// The three releases offered for one read are never three editions of one work while a same-titled release of another kind
/// scores level with them: on a shelf of tapes the film was shut out of the list by three editions of the same-named novel
/// (#615). The profile weighs the title 0.96 and the year 0.04, so a read with no year of its own leaves every same-titled
/// release scoring exactly the same, and four years apart costs the full 0.04. Neutral titles throughout.
/// </summary>
public class TextSearchOfferedThreeTests
{
    private const string Title = "Quiet Harbor Atlas";

    private static readonly ScoringProfile Profile =
        new("offer-test", new ScoringWeights(Title: 0.96, Type: 0, Platform: 0, Year: 0.04, Creator: 0, Edition: 0, Art: 0), new ScoringThresholds());

    /// <summary>A release the test put in the pool: which kind of thing it is, what year it is from, and who made it.</summary>
    private sealed record Release(WorkKind Kind, int? Year, string Creator);

    /// <summary>The pool the test wrote, and where each release sits in it, so an offer can be read back as the test's own order.</summary>
    private sealed record Pooled(LocalPool Pool, IReadOnlyList<Guid> Ids)
    {
        public int IndexOf(Guid id) => Ids.ToList().IndexOf(id);
    }

    private static Pooled Pool(params Release[] releases)
    {
        var items = new List<(CandidateRelease Scoring, ResolutionOption Option)>();
        var ids = new List<Guid>();
        foreach (var release in releases)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            items.Add((
                new CandidateRelease
                {
                    Key = id.ToString(),
                    Title = Title,
                    Kind = release.Kind,
                    Year = release.Year,
                    Creators = release.Creator.Length == 0 ? [] : [release.Creator],
                },
                new ResolutionOption(id, null, 0)));
        }

        return new Pooled(new LocalPool(items, null, new Dictionary<string, Guid>()), ids);
    }

    /// <summary>An edition of one novel: every edition of it shares its title and its author, and differs only in its year.</summary>
    private static Release Edition(int year) => new(WorkKind.Book, year, "Dana Pell");

    private static Release Film(int year) => new(WorkKind.Film, year, "");

    private static BarcodeResolution Score(
        ReadItem read,
        Pooled pooled,
        IReadOnlyCollection<Guid>? neighbours = null,
        IReadOnlyDictionary<string, Guid>? workIds = null,
        WorkKind? kindContext = null) =>
        TextSearchResolver.Score(read, Profile, pooled.Pool, ProviderAnswers.None, pendingWhenProvidersFailed: false, neighbours, workIds, kindContext);

    /// <summary>Which releases were offered, in the order the test wrote them into the pool.</summary>
    private static List<int> Offered(BarcodeResolution resolution, Pooled pooled) =>
        [.. resolution.Options.Select(o => pooled.IndexOf(o.LocalReleaseId!.Value))];

    [Fact]
    public void A_level_scoring_film_takes_the_third_slot_from_a_third_edition_of_one_novel()
    {
        var pooled = Pool(Edition(1998), Edition(2001), Edition(2004), Film(1994));

        var offered = Score(new ReadItem { Title = Title }, pooled);

        Offered(offered, pooled).ShouldBe([0, 1, 3]);
        offered.Steps.ShouldContain(s => s.Provider == "offer" && s.Outcome == "varied");
    }

    [Fact]
    public void The_scores_and_the_verdict_are_the_scorers_own_whatever_the_offer_varies()
    {
        var pooled = Pool(Edition(1998), Edition(2001), Edition(2004), Film(1994));

        var offered = Score(new ReadItem { Title = Title }, pooled);

        offered.Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        offered.Options.Select(o => o.Score).ShouldBe([1.0, 1.0, 1.0]);
    }

    [Fact]
    public void A_film_that_scores_below_the_third_edition_does_not_take_its_place()
    {
        // The read names 2001, so the film four years off loses the whole year term: 0.96 against the editions' 1.0.
        var pooled = Pool(Edition(2001), Edition(2001), Edition(2001), Film(1994));

        var offered = Score(new ReadItem { Title = Title, Year = 2001 }, pooled);

        offered.Options.Select(o => o.Score).ShouldBe([1.0, 1.0, 1.0]);
        Offered(offered, pooled).ShouldBe([0, 1, 2]);
        offered.Steps.Any(s => s.Provider == "offer").ShouldBeFalse();
    }

    [Fact]
    public void Three_editions_of_one_novel_stand_when_nothing_of_another_kind_was_found()
    {
        var pooled = Pool(Edition(1998), Edition(2001), Edition(2004), Edition(2011));

        var offered = Score(new ReadItem { Title = Title }, pooled);

        Offered(offered, pooled).ShouldBe([0, 1, 2]);
        offered.Steps.Any(s => s.Provider == "offer").ShouldBeFalse();
    }

    [Fact]
    public void An_offer_that_already_holds_two_works_is_left_as_the_scorer_ranked_it()
    {
        var pooled = Pool(Edition(1998), Edition(2001), Film(1994), Film(1997));

        var offered = Score(new ReadItem { Title = Title }, pooled);

        Offered(offered, pooled).ShouldBe([0, 1, 2]);
        offered.Steps.Any(s => s.Provider == "offer").ShouldBeFalse();
    }

    [Fact]
    public void Two_same_titled_novels_by_different_authors_are_two_works_and_both_stay_offered()
    {
        var pooled = Pool(Edition(1998), Edition(2001), new Release(WorkKind.Book, 2004, "Omar Wyeth"), Film(1994));

        var offered = Score(new ReadItem { Title = Title }, pooled);

        Offered(offered, pooled).ShouldBe([0, 1, 2]);
    }

    [Fact]
    public void The_release_the_scorer_ranked_best_is_never_the_one_displaced()
    {
        // Two editions sort above the best by the neighbour bias, so the third of the three offered is the scorer's own best. It
        // stays selectable (design §8(d)) instead of making room for the level-scoring film.
        var pooled = Pool(Edition(1998), Edition(2001), Edition(2004), Film(1994));
        var neighbour = Guid.NewGuid();
        var workIds = new Dictionary<string, Guid>(StringComparer.Ordinal)
        {
            [pooled.Ids[1].ToString()] = neighbour,
            [pooled.Ids[2].ToString()] = neighbour,
        };

        var offered = Score(new ReadItem { Title = Title }, pooled, [neighbour], workIds);

        Offered(offered, pooled).ShouldBe([1, 2, 0]);
        offered.Steps.Any(s => s.Provider == "offer").ShouldBeFalse();
    }

    // ---- which kind the offer makes room for ------------------------------------------------------------------------

    [Fact]
    public void A_book_read_keeps_all_three_editions_of_its_own_work()
    {
        var pooled = Pool(Edition(1998), Edition(2001), Edition(2004), Film(1994));

        var offered = Score(new ReadItem { Title = Title, Kind = WorkKind.Book }, pooled);

        Offered(offered, pooled).ShouldBe([0, 1, 2], "the read says book, so the offer makes no room for the film");
        offered.Steps.Any(s => s.Provider == "offer").ShouldBeFalse();
    }

    [Fact]
    public void A_novel_never_takes_a_slot_from_three_prints_of_the_film_on_a_shelf_of_films()
    {
        // The read names 2001, so the novel three years off scores a hair under the prints: level enough for the offer, and of the
        // wrong kind for this shelf.
        var print = new Release(WorkKind.Film, 2001, "Ines Harrow");
        var pooled = Pool(print, print, print, new Release(WorkKind.Book, 2000, "Dana Pell"));

        var offered = Score(new ReadItem { Title = Title, Year = 2001 }, pooled, kindContext: WorkKind.Film);

        Offered(offered, pooled).ShouldBe([0, 1, 2]);
        offered.Steps.Any(s => s.Provider == "offer").ShouldBeFalse();
    }

    [Fact]
    public void A_film_still_takes_a_slot_from_three_editions_of_the_novel_on_a_shelf_of_films()
    {
        var pooled = Pool(Edition(1998), Edition(2001), Edition(2004), Film(1994));

        var offered = Score(new ReadItem { Title = Title }, pooled, kindContext: WorkKind.Film);

        Offered(offered, pooled).ShouldBe([3, 0, 1], "the film sorts first on a shelf of films, and the third edition is no longer offered");
    }

    [Fact]
    public void Three_same_titled_films_that_name_no_director_are_three_works_and_all_stay_offered()
    {
        // The film catalogues answer with no creator at all, so nothing says these three are prints of one film.
        var pooled = Pool(Film(1986), Film(1996), Film(2004), new Release(WorkKind.Book, 2001, "Dana Pell"));

        var offered = Score(new ReadItem { Title = Title }, pooled);

        Offered(offered, pooled).ShouldBe([0, 1, 2]);
        offered.Steps.Any(s => s.Provider == "offer").ShouldBeFalse();
    }
}
