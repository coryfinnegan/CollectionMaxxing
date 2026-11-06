using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Identifiers;
using Microsoft.Extensions.Logging.Abstractions;
using static CollectionMaxxing.Application.Tests.Scanning.Identify.TrimmedRetryHarness;

namespace CollectionMaxxing.Application.Tests.Scanning.Identify;

// A spine on a shelf of books whose read grounded to three films ("cnoi Emergency Contact o") is asked once more as a book, after
// the whole shot has grounded: the spines beside it matched books, so the shelf is a shelf of books, and only the book providers
// are asked. What that finds is offered for the person to pick, never matched, and the film stays one of the picks. The reads are
// the misreads of a stored eval; the providers are scripted, and know their own kind only, as the real ones do.
public class IdentifyGroundingShotKindTests
{
    private static ReadFacts Vision(string title) => new(title, title, null, null, null, null, null, null, null);

    private static ProviderRelease Release(string title, WorkKind kind, string id) => new()
    {
        Provider = kind == WorkKind.Book ? "books" : "films",
        ExternalId = id,
        Title = title,
        Kind = kind,
        Format = kind == WorkKind.Book ? ReleaseFormat.Paperback : ReleaseFormat.Dvd,
        Year = 2019,
    };

    private static ResolutionOption Option(ProviderRelease release, double score) => new(null, release, score);

    private static BarcodeResolution Answer(ResolutionVerdict verdict, params ResolutionOption[] options) =>
        new(verdict, options, null, [new TraceStep("scripted", "hit", 0)]);

    private static BarcodeResolution MatchedBook(string title, int n) =>
        Answer(ResolutionVerdict.Matched, Option(Release(title, WorkKind.Book, $"m{n}"), 0.97));

    /// <summary>What "cnoi Emergency Contact o" found when nothing said it was a book: three films.</summary>
    private static BarcodeResolution ThreeFilms(double score = 0.5) => Answer(
        ResolutionVerdict.Ambiguous,
        Option(Release("Emergency Contact", WorkKind.Film, "f1"), score),
        Option(Release("Emergency Contact", WorkKind.Film, "f2"), score),
        Option(Release("Emergency Contact", WorkKind.Film, "f3"), score));

    private static string Key(string text) => string.Concat(text.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    /// <summary>A provider of one kind that knows the releases it was told for exactly one asked title, and remembers what it was asked.</summary>
    private sealed class OfKind(WorkKind kind, bool down = false) : ICatalogProvider
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, ProviderRelease[]> _known = [];

        public List<(string Title, WorkKind? Kind)> Asked { get; } = [];

        public string Name => kind == WorkKind.Book ? "books" : "films";

        public bool Supports(WorkKind asked) => kind == WorkKind.Book ? asked == WorkKind.Book : asked is WorkKind.Film or WorkKind.Tv;

        public OfKind Know(string asked, params ProviderRelease[] releases)
        {
            _known[Key(asked)] = releases;
            return this;
        }

        public Task<ProviderRelease?> LookupAsync(Identifier id, CancellationToken ct) => Task.FromResult<ProviderRelease?>(null);

        public Task<IReadOnlyList<ProviderRelease>> SearchAsync(CatalogQuery query, CancellationToken ct)
        {
            lock (_gate)
            {
                Asked.Add((query.Title, query.Kind));
            }

            return down
                ? throw new InvalidOperationException("the provider is down")
                : Task.FromResult<IReadOnlyList<ProviderRelease>>(_known.TryGetValue(Key(query.Title), out var found) ? found : []);
        }
    }

    private static IdentifyGrounding Grounding(OfKind[] providers, Catalogue? catalogue = null) => new(
        new TextSearchResolver(null!, catalogue ?? new Catalogue(), providers, new ScanningOptions(), NullLogger<TextSearchResolver>.Instance),
        new FixedScoring());

    private sealed class Asks
    {
        public List<ReadFacts> Facts { get; } = [];
    }

    private static ShotKindRetry Retry(
        IdentifyGrounding grounding, Asks asks, ReadFacts facts, BarcodeResolution answer,
        ScoringProfileKind profile = ScoringProfileKind.ShelfSpine, CancellationToken budget = default) => new(
        facts, profile, answer,
        asked =>
        {
            asks.Facts.Add(asked);
            return grounding.AskProvidersAsync(asked, CancellationToken.None, budget.CanBeCanceled ? budget : CancellationToken.None);
        },
        budget);

    private static readonly Func<IReadOnlyCollection<Guid>, CancellationToken, Task<IReadOnlyDictionary<Guid, WorkKind>>> NoKinds =
        (_, _) => Task.FromResult<IReadOnlyDictionary<Guid, WorkKind>>(new Dictionary<Guid, WorkKind>());

    private static Task<IReadOnlyList<BarcodeResolution>> RetryAsync(
        IdentifyGrounding grounding, WorkKind? printKind, params ShotKindRetry[] reads) =>
        grounding.RetryShotKindAsync(reads, printKind, NoKinds, CancellationToken.None);

    /// <summary>The shelf: three spines that matched books, then the read given.</summary>
    private static ShotKindRetry[] Shelf(IdentifyGrounding grounding, Asks asks, ShotKindRetry read) =>
    [
        Retry(grounding, asks, Vision("A COURT OF MIST AND FURY"), MatchedBook("A Court of Mist and Fury", 1)),
        Retry(grounding, asks, Vision("A COURT OF WINGS AND RUIN"), MatchedBook("A Court of Wings and Ruin", 2)),
        Retry(grounding, asks, Vision("ASSASSINS APPRENTICE"), MatchedBook("Assassin's Apprentice", 3)),
        read,
    ];

    private static string[] Kinds(BarcodeResolution answer) => [.. answer.Options.Select(o => o.Provider!.Kind.ToString())];

    private static OfKind EmergencyContactBooks() => new OfKind(WorkKind.Book)
        .Know("cnoi Emergency Contact o", Release("Emergency Contact", WorkKind.Book, "b1"));

    // ---- the cases of the stored eval ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_read_beside_matched_books_that_found_three_films_is_offered_the_book_first_and_a_film_after()
    {
        var books = EmergencyContactBooks();
        var grounding = Grounding([books, new OfKind(WorkKind.Film)]);
        var asks = new Asks();
        var film = Retry(grounding, asks, Vision("cnoi Emergency Contact o"), ThreeFilms());

        var answers = await RetryAsync(grounding, null, Shelf(grounding, asks, film));

        answers[3].Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        Kinds(answers[3]).ShouldBe(["Book", "Film", "Film"]);
        answers[3].Options[0].Provider!.ExternalId.ShouldBe("b1");
        answers[3].FoundByTrimmedSpan.ShouldBeTrue();
        answers[3].Steps.ShouldContain(new TraceStep("kind", "cnoi Emergency Contact o (kind: book)", 0));
    }

    [Fact]
    public async Task The_ask_is_the_read_as_a_book_and_only_the_book_providers_are_asked()
    {
        var books = EmergencyContactBooks();
        var films = new OfKind(WorkKind.Film);
        var grounding = Grounding([books, films]);
        var asks = new Asks();
        var read = Vision("cnoi Emergency Contact o") with { KindContext = WorkKind.Book, Year = 2019 };

        await RetryAsync(grounding, null, Shelf(grounding, asks, Retry(grounding, asks, read, ThreeFilms())));

        var asked = asks.Facts.Single();
        asked.Title.ShouldBe("cnoi Emergency Contact o");
        asked.Kind.ShouldBe(WorkKind.Book);
        asked.Year.ShouldBe(2019);
        asked.KindContext.ShouldBe(WorkKind.Book);
        read.Kind.ShouldBeNull();
        books.Asked.ShouldContain(("cnoi Emergency Contact o", WorkKind.Book));
        films.Asked.ShouldBeEmpty();
    }

    [Fact]
    public async Task What_the_ask_found_would_have_matched_but_is_only_offered()
    {
        var books = new OfKind(WorkKind.Book).Know("Emergency Contact", Release("Emergency Contact", WorkKind.Book, "b1"));
        var grounding = Grounding([books]);
        var asks = new Asks();

        var answers = await RetryAsync(grounding, null, Shelf(grounding, asks, Retry(grounding, asks, Vision("Emergency Contact"), ThreeFilms())));

        answers[3].Options[0].Score.ShouldBeGreaterThanOrEqualTo(new ScoringThresholds().MatchedScore);
        answers[3].Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        answers[3].FoundByTrimmedSpan.ShouldBeTrue();
    }

    [Fact]
    public async Task The_other_spines_answers_are_not_touched()
    {
        var grounding = Grounding([EmergencyContactBooks()]);
        var asks = new Asks();
        var shelf = Shelf(grounding, asks, Retry(grounding, asks, Vision("cnoi Emergency Contact o"), ThreeFilms()));

        var answers = await RetryAsync(grounding, null, shelf);

        answers[0].ShouldBeSameAs(shelf[0].Answer);
        answers[1].ShouldBeSameAs(shelf[1].Answer);
        answers[2].ShouldBeSameAs(shelf[2].Answer);
        asks.Facts.Count.ShouldBe(1);
    }

    // ---- the order of the offer ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_same_titled_find_that_scores_worse_than_the_reads_best_option_still_comes_first_on_a_shelf_of_books()
    {
        var grounding = Grounding([EmergencyContactBooks()]);
        var asks = new Asks();
        var film = Retry(grounding, asks, Vision("cnoi Emergency Contact o"), ThreeFilms(score: 0.99));

        var answers = await RetryAsync(grounding, null, Shelf(grounding, asks, film));

        Kinds(answers[3]).ShouldBe(["Book", "Film", "Film"]);
        answers[3].Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        answers[3].FoundByTrimmedSpan.ShouldBeTrue();
    }

    // The ask is scored as a book, so a book earns the type term that the film, found with no kind, never had. The find is compared
    // with the read's best option on the footing the read's own answer had (the score without the type term), and goes first only
    // when it is strictly better there. A same-titled book then goes first anyway on a shelf of books (the 2026-11-05 decision,
    // ShotKindRule.SameTitledOfKindFirst), so what these tests keep is the footing: the film stays offered and the scores are level.

    /// <summary>The answer a kind-less read got for a film of its title: the real scorer's, so the options carry the score and the terms the read earned.</summary>
    private static BarcodeResolution FoundAsFilm(IdentifyGrounding grounding, string read, string filmTitle) => grounding.Score(
        Vision(read), ScoringProfileKind.ShelfSpine, LocalPool.Empty, new ProviderAnswers([Release(filmTitle, WorkKind.Film, "f7")], [], false));

    [Fact]
    public async Task A_book_of_the_same_title_goes_ahead_of_the_film_the_read_found_on_a_shelf_of_books()
    {
        var books = new OfKind(WorkKind.Book).Know("CONGO", Release("Congo", WorkKind.Book, "b7"));
        var grounding = Grounding([books]);
        var asks = new Asks();
        var congo = Retry(grounding, asks, Vision("CONGO"), FoundAsFilm(grounding, "CONGO", "Congo"));

        var answers = await RetryAsync(grounding, null, Shelf(grounding, asks, congo));

        Kinds(answers[3]).ShouldBe(["Book", "Film"]);
        answers[3].Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        answers[3].FoundByTrimmedSpan.ShouldBeTrue();
        asks.Facts.Single().Kind.ShouldBe(WorkKind.Book);
    }

    [Fact]
    public async Task A_misread_title_gives_the_book_no_score_lead_over_the_film_for_the_type_term_it_earns()
    {
        // The film scores a little under 1 on a misread title, and the book scores more in the ask only because the type term
        // agrees; on the footing the film had, the two are level.
        var books = new OfKind(WorkKind.Book).Know("CONGOO", Release("Congo", WorkKind.Book, "b8"));
        var grounding = Grounding([books]);
        var asks = new Asks();
        var filmAnswer = FoundAsFilm(grounding, "CONGOO", "Congo");
        var congo = Retry(grounding, asks, Vision("CONGOO"), filmAnswer);

        var answers = await RetryAsync(grounding, null, Shelf(grounding, asks, congo));

        filmAnswer.Options[0].Score.ShouldBeLessThan(1.0);
        asks.Facts.Single().Kind.ShouldBe(WorkKind.Book); // the ask did name the kind, and the book was scored with the type term there
        answers[3].Options[0].Score.ShouldBe(filmAnswer.Options[0].Score); // but it is kept with the score it has without it, level with the film's
        Kinds(answers[3]).ShouldBe(["Book", "Film"]); // and it comes first for its title and the shelf's kind
    }

    [Fact]
    public async Task A_book_that_matches_the_read_better_than_the_film_even_without_the_type_term_goes_first()
    {
        // The film is a looser title match than the book is: strictly better on the film's own footing.
        var books = new OfKind(WorkKind.Book).Know("CONGO", Release("Congo", WorkKind.Book, "b9"));
        var grounding = Grounding([books]);
        var asks = new Asks();
        var looserFilm = Answer(ResolutionVerdict.Ambiguous, Option(Release("Congo The Movie", WorkKind.Film, "f9"), 0.7));
        var congo = Retry(grounding, asks, Vision("CONGO"), looserFilm);

        var answers = await RetryAsync(grounding, null, Shelf(grounding, asks, congo));

        Kinds(answers[3]).ShouldBe(["Book", "Film"]);
    }

    [Fact]
    public async Task A_single_dvd_among_books_is_offered_the_book_first_and_the_film_second()
    {
        // The precision check: a shelf of books with one DVD on it, whose title is also a book's. At Ambiguous this is acceptable, and the film stays one pick.
        var books = new OfKind(WorkKind.Book).Know("MOONLIT TIDE", Release("Moonlit Tide", WorkKind.Book, "b9"));
        var grounding = Grounding([books]);
        var asks = new Asks();
        var dvd = Answer(ResolutionVerdict.Ambiguous, Option(Release("Moonlit Tide", WorkKind.Film, "f9"), 0.9));

        var answers = await RetryAsync(grounding, null, Shelf(grounding, asks, Retry(grounding, asks, Vision("MOONLIT TIDE"), dvd)));

        Kinds(answers[3]).ShouldBe(["Book", "Film"]);
    }

    // ---- when it must not ask ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_matched_read_is_never_asked_again()
    {
        var grounding = Grounding([EmergencyContactBooks()]);
        var asks = new Asks();
        var matchedFilm = Retry(grounding, asks, Vision("cnoi Emergency Contact o"), Answer(ResolutionVerdict.Matched, Option(Release("Emergency Contact", WorkKind.Film, "f1"), 0.97)));

        var answers = await RetryAsync(grounding, null, Shelf(grounding, asks, matchedFilm));

        answers[3].ShouldBeSameAs(matchedFilm.Answer);
        asks.Facts.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_read_that_found_nothing_is_never_asked_again()
    {
        var grounding = Grounding([EmergencyContactBooks()]);
        var asks = new Asks();
        var lost = Retry(grounding, asks, Vision("cnoi Emergency Contact o"), new BarcodeResolution(ResolutionVerdict.NotFound, [], null, []));

        var answers = await RetryAsync(grounding, null, Shelf(grounding, asks, lost));

        answers[3].ShouldBeSameAs(lost.Answer);
        asks.Facts.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_read_with_its_own_kind_is_never_asked_again()
    {
        var grounding = Grounding([EmergencyContactBooks()]);
        var asks = new Asks();
        var model = Retry(grounding, asks, Vision("cnoi Emergency Contact o") with { Kind = WorkKind.Film }, ThreeFilms());

        var answers = await RetryAsync(grounding, null, Shelf(grounding, asks, model));

        answers[3].ShouldBeSameAs(model.Answer);
        asks.Facts.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_cover_or_a_read_a_product_code_named_is_never_asked_again()
    {
        var grounding = Grounding([EmergencyContactBooks()]);
        var asks = new Asks();
        var cover = Retry(grounding, asks, Vision("cnoi Emergency Contact o"), ThreeFilms(), ScoringProfileKind.Cover);
        var coded = Retry(grounding, asks, Vision("cnoi Emergency Contact o") with { ProductCode = "SLUS-00594" }, ThreeFilms());

        var answers = await RetryAsync(grounding, null, [.. Shelf(grounding, asks, cover), coded]);

        answers[3].ShouldBeSameAs(cover.Answer);
        answers[4].ShouldBeSameAs(coded.Answer);
        asks.Facts.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_read_whose_best_release_is_already_a_book_is_never_asked_again()
    {
        var grounding = Grounding([EmergencyContactBooks()]);
        var asks = new Asks();
        var book = Retry(grounding, asks, Vision("cnoi Emergency Contact o"), Answer(ResolutionVerdict.Ambiguous, Option(Release("Emergency Contact", WorkKind.Book, "b2"), 0.7)));

        var answers = await RetryAsync(grounding, null, Shelf(grounding, asks, book));

        answers[3].ShouldBeSameAs(book.Answer);
        asks.Facts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Two_matched_books_are_not_a_shelf_and_nothing_is_asked()
    {
        var grounding = Grounding([EmergencyContactBooks()]);
        var asks = new Asks();
        var film = Retry(grounding, asks, Vision("cnoi Emergency Contact o"), ThreeFilms());

        var answers = await RetryAsync(
            grounding, null, Retry(grounding, asks, Vision("ASSASSINS APPRENTICE"), MatchedBook("Assassin's Apprentice", 3)), film);

        answers[1].ShouldBeSameAs(film.Answer);
        asks.Facts.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_shelfs_own_print_is_a_shelf_of_books_with_no_matched_spine_at_all()
    {
        var grounding = Grounding([EmergencyContactBooks()]);
        var asks = new Asks();

        var answers = await RetryAsync(grounding, WorkKind.Book, Retry(grounding, asks, Vision("cnoi Emergency Contact o"), ThreeFilms()));

        Kinds(answers[0]).ShouldBe(["Book", "Film", "Film"]);
    }

    [Fact]
    public async Task A_shelf_of_films_leaves_a_film_read_alone_whatever_a_book_of_its_title_would_say()
    {
        var grounding = Grounding([EmergencyContactBooks()]);
        var asks = new Asks();
        var film = Retry(grounding, asks, Vision("cnoi Emergency Contact o"), ThreeFilms());

        var answers = await RetryAsync(grounding, WorkKind.Film, film);

        answers[0].ShouldBeSameAs(film.Answer);
        asks.Facts.ShouldBeEmpty();
    }

    // ---- what the ask may name -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_ask_that_finds_nothing_leaves_the_answer_as_it_was()
    {
        var grounding = Grounding([new OfKind(WorkKind.Book)]);
        var asks = new Asks();
        var film = Retry(grounding, asks, Vision("cnoi Emergency Contact o"), ThreeFilms());

        var answers = await RetryAsync(grounding, null, Shelf(grounding, asks, film));

        answers[3].ShouldBeSameAs(film.Answer);
        asks.Facts.Count.ShouldBe(1); // it did ask; nothing came back
    }

    [Fact]
    public async Task An_ask_whose_best_release_is_not_a_book_leaves_the_answer_as_it_was()
    {
        // No book provider knows it, and the catalogue holds the film: asked as a book, the film is still the best of what was found.
        var catalogue = new Catalogue(LocalFilm("Emergency Contact"));
        var grounding = Grounding([new OfKind(WorkKind.Book)], catalogue);
        var asks = new Asks();
        var film = Retry(grounding, asks, Vision("Emergency Contact"), ThreeFilms());

        var answers = await RetryAsync(grounding, null, Shelf(grounding, asks, film));

        answers[3].ShouldBeSameAs(film.Answer);
        asks.Facts.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Only_the_releases_of_the_shelfs_kind_are_offered_from_the_ask()
    {
        var catalogue = new Catalogue(LocalFilm("Emergency Contact"));
        var books = new OfKind(WorkKind.Book).Know("Emergency Contact", Release("Emergency Contact", WorkKind.Book, "b1"));
        var grounding = Grounding([books], catalogue);
        var asks = new Asks();
        var film = Retry(grounding, asks, Vision("Emergency Contact"), Answer(ResolutionVerdict.Ambiguous, Option(Release("Emergency Contact", WorkKind.Film, "f1"), 0.5)));

        var answers = await RetryAsync(grounding, null, Shelf(grounding, asks, film));

        // The local film is not one of the ask's finds (it is not a book); the film the read had stays as its own pick.
        answers[3].Options.Count(o => o.LocalReleaseId is not null).ShouldBe(0);
        Kinds(answers[3]).ShouldBe(["Book", "Film"]);
    }

    // ---- the budget ----------------------------------------------------------------------------------------------------------------

    // The pending job cannot make this pass (it grounds one read at a time, with no neighbours' answers), so a read the pass ran out
    // of time for is not left Pending for it: every answer stays exactly as it was.

    [Fact]
    public async Task A_pass_that_ran_out_of_time_before_its_asks_leaves_every_answer_as_it_was()
    {
        var grounding = Grounding([EmergencyContactBooks()]);
        var asks = new Asks();
        using var spent = new CancellationTokenSource();
        await spent.CancelAsync();
        var film = Retry(grounding, asks, Vision("cnoi Emergency Contact o"), ThreeFilms(), budget: spent.Token);

        var answers = await RetryAsync(grounding, null, Shelf(grounding, asks, film));

        answers[3].ShouldBeSameAs(film.Answer);
        asks.Facts.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_read_keeps_its_answer_when_the_providers_could_not_answer_its_ask()
    {
        var grounding = Grounding([new OfKind(WorkKind.Book, down: true)]);
        var asks = new Asks();
        var film = Retry(grounding, asks, Vision("cnoi Emergency Contact o"), ThreeFilms());

        var answers = await RetryAsync(grounding, null, Shelf(grounding, asks, film));

        answers[3].ShouldBeSameAs(film.Answer);
        answers[3].Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        asks.Facts.Count.ShouldBe(1);
    }

    [Fact]
    public async Task An_ask_the_pass_ran_out_of_time_for_leaves_the_answer_as_it_was()
    {
        var grounding = Grounding([EmergencyContactBooks()]);
        var asks = new Asks();
        var film = new ShotKindRetry(
            Vision("cnoi Emergency Contact o"), ScoringProfileKind.ShelfSpine, ThreeFilms(),
            _ => Task.FromResult(new ProviderAnswers([], [new TraceStep("budget", "timeout", 0)], true)),
            CancellationToken.None);

        var answers = await RetryAsync(grounding, null, Shelf(grounding, asks, film));

        answers[3].ShouldBeSameAs(film.Answer);
        answers[3].Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
    }

    // ---- releases the catalogue holds ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_kinds_of_the_catalogues_releases_are_looked_up_once_and_the_book_found_may_be_the_catalogues_too()
    {
        var bookA = Book("A Court of Mist and Fury");
        var bookB = Book("A Court of Wings and Ruin");
        var bookC = Book("Assassins Apprentice");
        var film = LocalFilm("Emergency Contact");
        var book = Book("Emergency Contact");
        var grounding = Grounding([new OfKind(WorkKind.Book)], new Catalogue(book));
        var asks = new Asks();
        var calls = new List<Guid[]>();
        var kinds = new Dictionary<Guid, WorkKind>
        {
            [bookA.Release.Id] = WorkKind.Book,
            [bookB.Release.Id] = WorkKind.Book,
            [bookC.Release.Id] = WorkKind.Book,
            [film.Release.Id] = WorkKind.Film,
        };
        ShotKindRetry Local(LocalHit hit, string title, ResolutionVerdict verdict) =>
            Retry(grounding, asks, Vision(title), new BarcodeResolution(verdict, [new ResolutionOption(hit.Release.Id, null, verdict == ResolutionVerdict.Matched ? 0.97 : 0.5)], null, []));

        var answers = await grounding.RetryShotKindAsync(
            [
                Local(bookA, "A COURT OF MIST AND FURY", ResolutionVerdict.Matched), Local(bookB, "A COURT OF WINGS AND RUIN", ResolutionVerdict.Matched),
                Local(bookC, "ASSASSINS APPRENTICE", ResolutionVerdict.Matched), Local(film, "Emergency Contact", ResolutionVerdict.Ambiguous),
            ],
            null,
            (ids, _) =>
            {
                calls.Add([.. ids]);
                return Task.FromResult<IReadOnlyDictionary<Guid, WorkKind>>(kinds);
            },
            CancellationToken.None);

        calls.ShouldHaveSingleItem().ToHashSet().ShouldBe([bookA.Release.Id, bookB.Release.Id, bookC.Release.Id, film.Release.Id]);
        answers[3].Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        answers[3].Options[0].LocalReleaseId.ShouldBe(book.Release.Id); // the catalogue's own book, found by asking as a book
        answers[3].Options[1].LocalReleaseId.ShouldBe(film.Release.Id); // and the film the read had, still a pick
        answers[3].FoundByTrimmedSpan.ShouldBeTrue();
    }

    [Fact]
    public async Task The_catalogue_is_not_asked_for_kinds_when_no_read_could_ask()
    {
        var grounding = Grounding([EmergencyContactBooks()]);
        var asks = new Asks();
        var calls = 0;
        var local = new BarcodeResolution(ResolutionVerdict.Matched, [new ResolutionOption(Guid.NewGuid(), null, 0.97)], null, []);

        await grounding.RetryShotKindAsync(
            [Retry(grounding, asks, Vision("ONE"), local), Retry(grounding, asks, Vision("TWO"), local), Retry(grounding, asks, Vision("THREE"), local)],
            null,
            (_, _) =>
            {
                calls++;
                return NoKinds([], CancellationToken.None);
            },
            CancellationToken.None);

        calls.ShouldBe(0);
    }

    [Fact]
    public async Task The_catalogue_is_not_asked_for_kinds_when_the_shot_could_not_have_a_kind()
    {
        var grounding = Grounding([EmergencyContactBooks()]);
        var asks = new Asks();
        var calls = 0;
        var local = new BarcodeResolution(ResolutionVerdict.Matched, [new ResolutionOption(Guid.NewGuid(), null, 0.97)], null, []);
        var ambiguous = new BarcodeResolution(ResolutionVerdict.Ambiguous, [new ResolutionOption(Guid.NewGuid(), null, 0.7)], null, []);

        await grounding.RetryShotKindAsync(
            [Retry(grounding, asks, Vision("ONE"), local), Retry(grounding, asks, Vision("TWO"), local), Retry(grounding, asks, Vision("THREE"), ambiguous)],
            null,
            (_, _) =>
            {
                calls++;
                return NoKinds([], CancellationToken.None);
            },
            CancellationToken.None);

        calls.ShouldBe(0); // two matched spines, no print: no shelf, whatever the kinds would have been
    }

    /// <summary>A kind-less read whose own options already hold both the film (better scored) and the same-titled book.</summary>
    private static BarcodeResolution FilmAndBook() => Answer(
        ResolutionVerdict.Ambiguous,
        Option(Release("Emergency Contact", WorkKind.Film, "f1"), 0.9),
        Option(Release("Emergency Contact", WorkKind.Book, "b9"), 0.6));

    [Fact]
    public async Task A_same_titled_book_already_among_the_reads_options_comes_first_on_a_shelf_of_books_when_the_ask_finds_nothing()
    {
        var grounding = Grounding([new OfKind(WorkKind.Book), new OfKind(WorkKind.Film)]);
        var asks = new Asks();
        var read = Retry(grounding, asks, Vision("cnoi Emergency Contact o"), FilmAndBook());

        var answers = await RetryAsync(grounding, null, Shelf(grounding, asks, read));

        answers[3].Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        answers[3].Options.Select(o => o.Provider!.ExternalId).ShouldBe(["b9", "f1"]);
    }

    [Fact]
    public async Task A_same_titled_book_the_ask_finds_comes_before_a_better_scored_film()
    {
        var grounding = Grounding([new OfKind(WorkKind.Book).Know("cnoi Emergency Contact o", Release("Emergency Contact", WorkKind.Book, "b1")), new OfKind(WorkKind.Film)]);
        var asks = new Asks();
        var read = Retry(grounding, asks, Vision("cnoi Emergency Contact o"), FilmAndBook());

        var answers = await RetryAsync(grounding, null, Shelf(grounding, asks, read));

        answers[3].Options[0].Provider!.Kind.ShouldBe(WorkKind.Book);
        answers[3].Options.Select(o => o.Provider!.Kind).ShouldContain(WorkKind.Film);
    }

    [Fact]
    public async Task A_mixed_shelf_leaves_a_reads_options_in_their_order()
    {
        var grounding = Grounding([new OfKind(WorkKind.Book), new OfKind(WorkKind.Film)]);
        var asks = new Asks();
        var read = Retry(grounding, asks, Vision("cnoi Emergency Contact o"), FilmAndBook());
        var original = read.Answer;
        ShotKindRetry[] shelf =
        [
            Retry(grounding, asks, Vision("ONE"), MatchedBook("One", 1)),
            Retry(grounding, asks, Vision("TWO"), MatchedBook("Two", 2)),
            Retry(grounding, asks, Vision("THREE"), Answer(ResolutionVerdict.Matched, Option(Release("Three", WorkKind.Film, "m3"), 0.97))),
            Retry(grounding, asks, Vision("FOUR"), Answer(ResolutionVerdict.Matched, Option(Release("Four", WorkKind.Film, "m4"), 0.97))),
            read,
        ];

        var answers = await RetryAsync(grounding, null, shelf);

        answers[4].ShouldBeSameAs(original);
    }

    [Fact]
    public async Task A_read_that_names_its_own_kind_keeps_its_options_in_their_order_on_a_shelf_of_books()
    {
        var grounding = Grounding([EmergencyContactBooks(), new OfKind(WorkKind.Film)]);
        var asks = new Asks();
        var film = Answer(
            ResolutionVerdict.Ambiguous,
            Option(Release("Emergency Contact", WorkKind.Film, "f1"), 0.9),
            Option(Release("Emergency Contact", WorkKind.Book, "b1"), 0.6));
        var read = Retry(grounding, asks, Vision("Emergency Contact") with { Kind = WorkKind.Film }, film);

        var answers = await RetryAsync(grounding, null, Shelf(grounding, asks, read));

        answers[3].ShouldBeSameAs(film);
        asks.Facts.ShouldBeEmpty();
    }

    private static LocalHit LocalFilm(string title)
    {
        var work = new Work { Id = Guid.NewGuid(), Kind = WorkKind.Film, Title = title, NormalizedTitle = TitleNormalizer.Normalize(title), CreatorsJson = "[]" };
        return new LocalHit(new Release { Id = Guid.NewGuid(), WorkId = work.Id, Format = ReleaseFormat.Dvd }, work);
    }
}
