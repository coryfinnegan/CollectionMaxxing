using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using Microsoft.Extensions.Logging.Abstractions;
using static CollectionMaxxing.Application.Tests.Scanning.Identify.TrimmedRetryHarness;

namespace CollectionMaxxing.Application.Tests.Scanning.Identify;

// Build-161 eval, shelf-03 "Beer for My Corpses": the creator swap asks the real title with a neighbour's author as the creator. A film
// with no creators took the neutral creator term and won over the right book. On a book spine the swap's creator is a guess, so a
// release with no creators gets no edge from it; on a film spine, and in a snap with no kind, the film keeps its neutral term.
public class IdentifyGroundingGuessedCreatorTests
{
    private static IdentifyGrounding Grounding() => new(
        new TextSearchResolver(null!, new Catalogue(), [new Providers()], new ScanningOptions(), NullLogger<TextSearchResolver>.Instance),
        new FixedScoring());

    private static ProviderRelease Release(string id, string title, WorkKind kind, params string[] creators) => new()
    {
        Provider = "p",
        ExternalId = id,
        Title = title,
        Kind = kind,
        Creators = creators,
        Format = kind == WorkKind.Book ? ReleaseFormat.Paperback : null,
    };

    private static ReadFacts Spine(string title, string creatorAsRead, WorkKind? kindContext) => new(
        title, title, null, null, null, null, null, null, null, KindContext: kindContext, CreatorAsRead: creatorAsRead);

    /// <summary>Retries a spine whose own ask found nothing; the providers answer only a search for <paramref name="asked"/>.</summary>
    private static async Task<(BarcodeResolution Answer, List<ReadFacts> Asked)> RetryAsync(
        ReadFacts facts, string asked, params ProviderRelease[] pool)
    {
        var grounding = Grounding();
        var notFound = grounding.Score(facts, ScoringProfileKind.ShelfSpine, LocalPool.Empty, ProviderAnswers.None);
        var seen = new List<ReadFacts>();
        var answers = await grounding.RetryTrimmedAsync(
            [new TrimmedRetry(facts, ScoringProfileKind.ShelfSpine, notFound, f =>
            {
                seen.Add(f);
                return Task.FromResult(string.Equals(f.Title, asked, StringComparison.OrdinalIgnoreCase)
                    ? new ProviderAnswers(pool, [], false)
                    : ProviderAnswers.None);
            }, CancellationToken.None)],
            CancellationToken.None);
        return (answers.Single(), seen);
    }

    private static ProviderRelease[] BeerPool() =>
    [
        Release("film", "Beer for My Horses", WorkKind.Film),
        Release("book", "Beer For My Corpses", WorkKind.Book, "C. M. McGuire"),
        Release("longer", "Beer For My Corpses - A Misplaced Adventures Novel", WorkKind.Book, "C M McGuire"),
        Release("game", "Beer for My Pals", WorkKind.Game, "Eden Games"),
    ];

    private static ProviderRelease[] ExcaliburPool() =>
    [
        Release("film1", "Excalibur", WorkKind.Film),
        Release("film2", "Excalibur", WorkKind.Film),
        Release("film3", "Excalibur", WorkKind.Film),
        Release("game", "Excalibur", WorkKind.Game),
        Release("book", "Excalibur", WorkKind.Book, "Mara Quillon", "Tess Harrowgate"),
    ];

    [Fact]
    public async Task A_book_spine_whose_title_is_a_neighbours_author_is_offered_the_book_its_creator_as_read_names_not_a_same_titled_film()
    {
        var (answer, _) = await RetryAsync(Spine("M.L. EADEN", "BEER FOR MY CORPSES", WorkKind.Book), "BEER FOR MY CORPSES", BeerPool());

        answer.Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        answer.Options[0].Provider!.ExternalId.ShouldBe("book");
        answer.Steps.ShouldContain(s => s.Provider == "swapped");
        answer.FoundByTrimmedSpan.ShouldBeTrue();
    }

    [Fact]
    public async Task In_a_snap_of_films_the_swaps_same_titled_film_still_comes_first()
    {
        var (answer, asked) = await RetryAsync(Spine("Frigd faroan fomatoes", "EXCALIBUR", WorkKind.Film), "EXCALIBUR", ExcaliburPool());

        asked.Single(f => f.Title == "EXCALIBUR").CreatorIsAGuess.ShouldBeFalse();
        answer.Options[0].Provider!.Kind.ShouldBe(WorkKind.Film);
        answer.Steps.ShouldContain(s => s.Provider == "swapped");
    }

    [Fact]
    public async Task In_a_snap_with_no_kind_the_swaps_same_titled_film_keeps_its_neutral_creator_term()
    {
        var (answer, asked) = await RetryAsync(Spine("Frigd faroan fomatoes", "EXCALIBUR", null), "EXCALIBUR", ExcaliburPool());
        var (inFilms, _) = await RetryAsync(Spine("Frigd faroan fomatoes", "EXCALIBUR", WorkKind.Film), "EXCALIBUR", ExcaliburPool());

        asked.Single(f => f.Title == "EXCALIBUR").CreatorIsAGuess.ShouldBeFalse();
        answer.Options[0].Provider!.Kind.ShouldBe(WorkKind.Film);
        answer.Options[0].Score.ShouldBe(inFilms.Options[0].Score);
        answer.Options[0].Terms.Single(t => t.Name == "creator").Value.ShouldBe(0.5);
    }

    [Fact]
    public async Task The_ask_of_a_guessed_swap_is_the_same_as_the_ask_of_an_unguessed_one()
    {
        var (_, books) = await RetryAsync(Spine("M.L. EADEN", "BEER FOR MY CORPSES", WorkKind.Book), "BEER FOR MY CORPSES");
        var (_, films) = await RetryAsync(Spine("M.L. EADEN", "BEER FOR MY CORPSES", WorkKind.Film), "BEER FOR MY CORPSES");

        books.Select(f => f.ToQuery()).ShouldBe(films.Select(f => f.ToQuery()));
    }
}
