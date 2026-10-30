using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;

namespace CollectionMaxxing.Application.Tests.Scanning;

/// <summary>
/// A read that names no kind sees the snap's kind first, but only among releases that score level (or within 0.02): the scorer's
/// verdict, every score, and a read's own kind are untouched. The profile weighs the title 0.96 and the year 0.04, so one year
/// apart costs exactly 0.01 and four years apart costs 0.04. Neutral titles throughout.
/// </summary>
public class TextSearchKindContextTests
{
    private const string Title = "Quiet Harbor Atlas";

    private static readonly ScoringProfile Profile =
        new("kind-test", new ScoringWeights(Title: 0.96, Type: 0, Platform: 0, Year: 0.04, Creator: 0, Edition: 0, Art: 0), new ScoringThresholds());

    private static readonly ReadItem Kindless = new() { Title = Title };

    private static LocalPool Pool(params (string Title, WorkKind Kind, int? Year)[] releases)
    {
        var items = new List<(CandidateRelease Scoring, ResolutionOption Option)>();
        foreach (var (title, kind, year) in releases)
        {
            var id = Guid.NewGuid();
            items.Add((new CandidateRelease { Key = id.ToString(), Title = title, Kind = kind, Year = year }, new ResolutionOption(id, null, 0)));
        }

        return new LocalPool(items, null, new Dictionary<string, Guid>());
    }

    private static BarcodeResolution Score(ReadItem read, LocalPool pool, WorkKind? kindContext, ProviderAnswers? providers = null) =>
        TextSearchResolver.Score(read, Profile, pool, providers ?? ProviderAnswers.None, pendingWhenProvidersFailed: false, kindContext: kindContext);

    // The options are the pool's releases, found again by id; the years the test gave them tell them apart.
    private static List<int?> Years(BarcodeResolution resolution, LocalPool pool)
    {
        var years = pool.Items.ToDictionary(i => i.Option.LocalReleaseId!.Value, i => i.Scoring.Year);
        return resolution.Options.Select(o => years[o.LocalReleaseId!.Value]).ToList();
    }

    [Fact]
    public void A_tied_book_and_film_list_the_film_first_on_a_shelf_of_films_and_change_no_verdict_or_score()
    {
        var pool = Pool((Title, WorkKind.Book, 2001), (Title, WorkKind.Film, 2002));
        var plain = Score(Kindless, pool, kindContext: null);

        var biased = Score(Kindless, pool, WorkKind.Film);

        Years(plain, pool).ShouldBe([2001, 2002], "the scorer cannot tell them apart, so the pool's order stands");
        Years(biased, pool).ShouldBe([2002, 2001]);
        biased.Verdict.ShouldBe(plain.Verdict);
        biased.Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        biased.Options.Select(o => o.Score).ShouldBe([1.0, 1.0]);
        biased.Steps.ShouldContain(s => s.Provider == "kind" && s.Outcome == "biased");
        plain.Steps.Any(s => s.Provider == "kind").ShouldBeFalse();
    }

    [Fact]
    public void Releases_from_a_provider_are_ordered_the_same_way()
    {
        var providers = new ProviderAnswers(
            [
                new ProviderRelease { Provider = "test", ExternalId = "b", Title = Title, Kind = WorkKind.Book, Year = 2001 },
                new ProviderRelease { Provider = "test", ExternalId = "f", Title = Title, Kind = WorkKind.Film, Year = 2002 },
            ],
            [],
            false);

        var biased = Score(Kindless, LocalPool.Empty, WorkKind.Film, providers);

        biased.Options.Select(o => o.Provider!.Kind).ShouldBe([WorkKind.Film, WorkKind.Book]);
    }

    [Fact]
    public void A_show_is_in_the_same_family_as_a_film()
    {
        var pool = Pool((Title, WorkKind.Book, 2001), (Title, WorkKind.Tv, 2002));

        Years(Score(Kindless, pool, WorkKind.Film), pool).ShouldBe([2002, 2001]);
    }

    [Fact]
    public void Without_a_kind_context_nothing_moves()
    {
        var pool = Pool((Title, WorkKind.Book, 2001), (Title, WorkKind.Film, 2002));

        Years(Score(Kindless, pool, kindContext: null), pool).ShouldBe([2001, 2002]);
    }

    [Fact]
    public void A_book_that_clearly_scores_better_stays_first_whatever_the_snap_is()
    {
        // The read names a year: the book has it, the film is four years off (0.04 lower, past the 0.02 the context may close).
        var read = Kindless with { Year = 2001 };
        var pool = Pool((Title, WorkKind.Film, 2005), (Title, WorkKind.Book, 2001));

        var biased = Score(read, pool, WorkKind.Film);

        Years(biased, pool).ShouldBe([2001, 2005]);
        biased.Options.Select(o => o.Score).ShouldBe([1.0, 0.96]);
        biased.Steps.Any(s => s.Provider == "kind").ShouldBeFalse("nothing moved, so there is nothing to say");
    }

    [Fact]
    public void A_film_a_hair_below_the_book_overtakes_it_but_keeps_its_own_lower_score()
    {
        var read = Kindless with { Year = 2001 };
        var pool = Pool((Title, WorkKind.Book, 2001), (Title, WorkKind.Film, 2002)); // one year off: 0.01 lower

        var biased = Score(read, pool, WorkKind.Film);

        Years(biased, pool).ShouldBe([2002, 2001]);
        biased.Options.Select(o => o.Score).ShouldBe([0.99, 1.0]);
        biased.Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
    }

    [Fact]
    public void A_film_further_below_the_book_than_the_epsilon_does_not_overtake_it()
    {
        var read = Kindless with { Year = 2001 };
        var pool = Pool((Title, WorkKind.Book, 2001), (Title, WorkKind.Film, 2004)); // three years off: 0.03 lower

        Years(Score(read, pool, WorkKind.Film), pool).ShouldBe([2001, 2004]);
    }

    [Fact]
    public void A_read_with_its_own_kind_is_never_reordered_by_the_snap()
    {
        // The profile ignores the type term, so a read of a book ties the film with it; the snap still has no say.
        var read = Kindless with { Kind = WorkKind.Book };
        var pool = Pool((Title, WorkKind.Book, 2001), (Title, WorkKind.Film, 2002));

        var biased = Score(read, pool, WorkKind.Film);

        Years(biased, pool).ShouldBe([2001, 2002]);
        biased.Steps.Any(s => s.Provider == "kind").ShouldBeFalse();
    }

    [Fact]
    public void A_matched_result_keeps_its_order_because_its_lead_is_far_beyond_the_epsilon()
    {
        var read = Kindless with { Year = 2001 };
        var pool = Pool((Title, WorkKind.Book, 2001), ("Quiet Harbor Stories", WorkKind.Film, 2001));

        var biased = Score(read, pool, WorkKind.Film);

        biased.Verdict.ShouldBe(ResolutionVerdict.Matched);
        biased.Options[0].Score.ShouldBe(1.0);
        Years(biased, pool).ShouldBe([2001, 2001]);
        biased.Options[0].LocalReleaseId.ShouldBe(pool.Items[0].Option.LocalReleaseId);
        biased.Steps.Any(s => s.Provider == "kind").ShouldBeFalse();
    }

    [Fact]
    public void A_lone_release_of_another_kind_is_ordered_not_blocked()
    {
        var pool = Pool((Title, WorkKind.Book, 2001));

        var biased = Score(Kindless, pool, WorkKind.Film);

        biased.Options.ShouldHaveSingleItem().Score.ShouldBe(1.0);
        biased.Verdict.ShouldBe(Score(Kindless, pool, kindContext: null).Verdict);
    }

    [Fact]
    public void The_unbiased_best_stays_one_of_the_three_options_whatever_the_snap_says()
    {
        var pool = Pool((Title, WorkKind.Book, 2001), (Title, WorkKind.Film, 2002), (Title, WorkKind.Film, 2003), (Title, WorkKind.Film, 2004));

        Years(Score(Kindless, pool, WorkKind.Film), pool).ShouldBe([2002, 2003, 2001]);
    }

    [Fact]
    public void A_read_nothing_matches_stays_not_found_with_any_context()
    {
        var pool = Pool(("A Different Title Entirely", WorkKind.Film, 2002));

        Score(Kindless, pool, WorkKind.Film).Verdict.ShouldBe(ResolutionVerdict.NotFound);
    }
}
