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

// A spine that prints only a series' subtitle ("nEMESIS", "GENERATIONS", "ILVER FLAMES") is asked once more with the series its
// neighbour carries, after the whole shot has been grounded: "Star Trek nEMESIS" finds the film the bare word could not. What
// that finds is offered for the person to pick, never matched, and only when it names a release whose title holds the series and
// the words read. The reads are the misreads of a stored eval.
public class IdentifyGroundingSeriesRetryTests
{
    private static ReadBox At(double x) => new(x, 0.1, 0.05, 0.8);

    private static ReadFacts Film(string title) => new(title, title, null, null, null, WorkKind.Film, null, null, null);

    private static ProviderRelease Release(string title, int n) => new()
    {
        Provider = "scripted", ExternalId = $"r{n}", Title = title, Kind = WorkKind.Film, Format = ReleaseFormat.Dvd, Year = 2002,
    };

    private static BarcodeResolution Found(ResolutionVerdict verdict, string title, int n, double score) =>
        new(verdict, [new ResolutionOption(null, Release(title, n), score)], null, [new TraceStep("scripted", "hit", 0)]);

    private static BarcodeResolution Matched(string title, int n = 1) => Found(ResolutionVerdict.Matched, title, n, 0.97);

    private static BarcodeResolution Ambiguous(string title, int n = 2) => Found(ResolutionVerdict.Ambiguous, title, n, 0.7);

    private static BarcodeResolution Nothing => new(ResolutionVerdict.NotFound, [], null, [new TraceStep("scripted", "miss", 0)]);

    private static string Key(string text) => string.Concat(text.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    /// <summary>
    /// Providers that know the releases they were given and find one when every word asked for is inside its title, so a misread
    /// first letter still finds it (a real provider's search is that forgiving). Remembers what it was asked, and can be down.
    /// </summary>
    private sealed class Scripted(bool down = false, params ProviderRelease[] known) : ICatalogProvider
    {
        private readonly object _gate = new();

        public List<string> Titles { get; } = [];

        public string Name => "scripted";

        public bool Supports(WorkKind kind) => true;

        public Task<ProviderRelease?> LookupAsync(Identifier id, CancellationToken ct) => Task.FromResult<ProviderRelease?>(null);

        public Task<IReadOnlyList<ProviderRelease>> SearchAsync(CatalogQuery query, CancellationToken ct)
        {
            lock (_gate)
            {
                Titles.Add(query.Title);
            }

            if (down)
            {
                throw new InvalidOperationException("the provider is down");
            }

            var words = query.Title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Key).Where(w => w.Length > 0).ToList();
            return Task.FromResult<IReadOnlyList<ProviderRelease>>([.. known.Where(r => words.All(w => Key(r.Title).Contains(w, StringComparison.Ordinal)))]);
        }
    }

    private static readonly ProviderRelease[] Franchise =
    [
        Release("Star Trek: Nemesis", 10), Release("Star Trek Generations", 11), Release("Star Trek: First Contact", 12),
        Release("A Court of Silver Flames", 13), Release("Nemesis", 14),
    ];

    private static IdentifyGrounding Grounding(Scripted providers, Catalogue? catalogue = null) => new(
        new TextSearchResolver(null!, catalogue ?? new Catalogue(), [providers], new ScanningOptions(), NullLogger<TextSearchResolver>.Instance),
        new FixedScoring());

    private sealed class Asks
    {
        public List<ReadFacts> Facts { get; } = [];
    }

    private static SeriesRetry Retry(
        IdentifyGrounding grounding, Asks asks, string title, ReadBox? box, BarcodeResolution answer,
        ScoringProfileKind profile = ScoringProfileKind.ShelfSpine, CancellationToken budget = default) => new(
        Film(title), profile, box, answer,
        facts =>
        {
            asks.Facts.Add(facts);
            return grounding.AskProvidersAsync(facts, CancellationToken.None, budget.CanBeCanceled ? budget : CancellationToken.None);
        },
        budget);

    private static readonly Func<IReadOnlyCollection<Guid>, CancellationToken, Task<IReadOnlyDictionary<Guid, string>>> NoTitles =
        (_, _) => Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

    private static Task<IReadOnlyList<BarcodeResolution>> RetryAsync(IdentifyGrounding grounding, params SeriesRetry[] reads) =>
        grounding.RetrySeriesAsync(reads, NoTitles, CancellationToken.None);

    private static string[] Titles(BarcodeResolution answer) => [.. answer.Options.Select(o => o.Provider!.Title)];

    // ---- the cases of the stored eval ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_nemesis_beside_First_Contact_that_found_the_novel_is_offered_the_film()
    {
        var grounding = Grounding(new Scripted(false, Franchise));
        var asks = new Asks();

        var answers = await RetryAsync(
            grounding,
            Retry(grounding, asks, "STAR TREK FIASI CONTACI", At(0.30), Matched("Star Trek: First Contact", 12)),
            Retry(grounding, asks, "nEMESIS", At(0.36), Ambiguous("Nemesis", 14)));

        answers[1].Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        Titles(answers[1]).ShouldBe(["Star Trek: Nemesis"]);
        answers[1].FoundByTrimmedSpan.ShouldBeTrue();
        answers[1].Steps.ShouldContain(new TraceStep("series", "Star Trek nEMESIS (series: Star Trek)", 0));
        asks.Facts.Select(f => f.Title).ShouldBe(["Star Trek nEMESIS"]);
    }

    [Fact]
    public async Task A_generations_beside_First_Contact_that_found_nothing_is_offered_the_film()
    {
        var grounding = Grounding(new Scripted(false, Franchise));
        var asks = new Asks();

        var neighbour = Retry(grounding, asks, "STAR TREK FIASI CONTACI", At(0.30), Matched("Star Trek: First Contact", 12));

        var answers = await RetryAsync(grounding, Retry(grounding, asks, "GENERATIONS", At(0.24), Nothing), neighbour);

        answers[0].Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        Titles(answers[0]).ShouldBe(["Star Trek Generations"]);
        answers[0].FoundByTrimmedSpan.ShouldBeTrue();
        answers[1].ShouldBeSameAs(neighbour.Answer);
    }

    [Fact]
    public async Task Silver_Flames_between_two_Court_books_is_offered_the_book_found_with_their_leading_words()
    {
        var grounding = Grounding(new Scripted(false, Franchise));
        var asks = new Asks();

        var answers = await RetryAsync(
            grounding,
            Retry(grounding, asks, "A COURT OF MIST AND FURY", At(0.20), Matched("A Court of Mist and Fury", 20)),
            Retry(grounding, asks, "ILVER FLAMES", At(0.26), Nothing),
            Retry(grounding, asks, "A COURT OF WINGS AND RUIN", At(0.32), Matched("A Court of Wings and Ruin", 21)));

        answers[1].Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        Titles(answers[1]).ShouldBe(["A Court of Silver Flames"]);
        answers[1].Steps.ShouldContain(new TraceStep("series", "A Court of ILVER FLAMES (series: A Court of)", 0));
        asks.Facts.Select(f => f.Title).ShouldBe(["A Court of ILVER FLAMES"]);
    }

    [Fact]
    public async Task What_the_ask_found_would_have_matched_but_is_only_offered()
    {
        var grounding = Grounding(new Scripted(false, Franchise));
        var asks = new Asks();

        var answers = await RetryAsync(
            grounding,
            Retry(grounding, asks, "STAR TREK FIASI CONTACI", At(0.30), Matched("Star Trek: First Contact", 12)),
            Retry(grounding, asks, "nEMESIS", At(0.36), Nothing));

        answers[1].Options.Single().Score.ShouldBeGreaterThanOrEqualTo(new ScoringThresholds().MatchedScore);
        answers[1].Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
    }

    [Fact]
    public async Task The_ask_is_the_read_with_the_series_in_front_and_everything_else_about_the_read_unchanged()
    {
        var grounding = Grounding(new Scripted(false, Franchise));
        var asks = new Asks();
        var read = Film("nEMESIS") with { Text = "nEMESIS (2002)", Format = ReleaseFormat.Dvd, Year = 2002, KindContext = WorkKind.Film };

        await RetryAsync(
            grounding,
            Retry(grounding, asks, "STAR TREK FIASI CONTACI", At(0.30), Matched("Star Trek: First Contact", 12)),
            Retry(grounding, asks, "nEMESIS", At(0.36), Nothing) with { Facts = read });

        var asked = asks.Facts.Single();
        asked.Title.ShouldBe("Star Trek nEMESIS");
        asked.Text.ShouldBe("nEMESIS (2002)");
        asked.Kind.ShouldBe(WorkKind.Film);
        asked.Format.ShouldBe(ReleaseFormat.Dvd);
        asked.Year.ShouldBe(2002);
        asked.KindContext.ShouldBe(WorkKind.Film);
        read.Title.ShouldBe("nEMESIS");
    }

    // ---- no neighbour, no ask ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_lone_spine_asks_nothing_and_keeps_its_answer()
    {
        var grounding = Grounding(new Scripted(false, Franchise));
        var asks = new Asks();
        var lone = Retry(grounding, asks, "nEMESIS", At(0.36), Ambiguous("Nemesis", 14));

        var answers = await RetryAsync(grounding, lone);

        answers.Single().ShouldBeSameAs(lone.Answer);
        asks.Facts.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_neighbour_without_a_series_asks_nothing()
    {
        var grounding = Grounding(new Scripted(false, Franchise));
        var asks = new Asks();
        var lost = Retry(grounding, asks, "nEMESIS", At(0.36), Nothing);

        var answers = await RetryAsync(grounding, Retry(grounding, asks, "CASABLANCA", At(0.30), Matched("Casablanca", 30)), lost);

        answers[1].ShouldBeSameAs(lost.Answer);
        asks.Facts.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_read_with_no_box_asks_nothing()
    {
        var grounding = Grounding(new Scripted(false, Franchise));
        var asks = new Asks();
        var lost = Retry(grounding, asks, "nEMESIS", null, Nothing);

        var answers = await RetryAsync(grounding, Retry(grounding, asks, "STAR TREK FIASI CONTACI", At(0.30), Matched("Star Trek: First Contact", 12)), lost);

        answers[1].ShouldBeSameAs(lost.Answer);
        asks.Facts.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_matched_read_is_never_asked_again()
    {
        var grounding = Grounding(new Scripted(false, Franchise));
        var asks = new Asks();
        var matched = Retry(grounding, asks, "nEMESIS", At(0.36), Matched("Nemesis", 14));

        var answers = await RetryAsync(grounding, Retry(grounding, asks, "STAR TREK FIASI CONTACI", At(0.30), Matched("Star Trek: First Contact", 12)), matched);

        answers[1].ShouldBeSameAs(matched.Answer);
        asks.Facts.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_cover_is_never_asked_with_a_series()
    {
        var grounding = Grounding(new Scripted(false, Franchise));
        var asks = new Asks();
        var cover = Retry(grounding, asks, "nEMESIS", At(0.36), Nothing, profile: ScoringProfileKind.Cover);

        var answers = await RetryAsync(grounding, Retry(grounding, asks, "STAR TREK FIASI CONTACI", At(0.30), Matched("Star Trek: First Contact", 12)), cover);

        answers[1].ShouldBeSameAs(cover.Answer);
        asks.Facts.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_read_is_asked_once_however_many_neighbours_carry_the_series()
    {
        var grounding = Grounding(new Scripted(false, Franchise));
        var asks = new Asks();

        await RetryAsync(
            grounding,
            Retry(grounding, asks, "STAR TREK FIASI CONTACI", At(0.20), Matched("Star Trek: First Contact", 12)),
            Retry(grounding, asks, "nEMESIS", At(0.26), Nothing),
            Retry(grounding, asks, "STAR TREK INSURRECTION", At(0.32), Matched("Star Trek: Insurrection", 15)));

        asks.Facts.Count.ShouldBe(1);
    }

    // ---- what the ask may name -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_ask_that_names_no_release_of_the_series_leaves_the_answer_as_it_was()
    {
        // The provider knows no Star Trek film called Nemesis: the book called Nemesis is not the series' entry.
        var grounding = Grounding(new Scripted(false, Release("Nemesis", 14), Release("Star Trek: First Contact", 12)));
        var asks = new Asks();
        var book = Retry(grounding, asks, "nEMESIS", At(0.36), Ambiguous("Nemesis", 14));

        var answers = await RetryAsync(grounding, Retry(grounding, asks, "STAR TREK FIASI CONTACI", At(0.30), Matched("Star Trek: First Contact", 12)), book);

        answers[1].ShouldBeSameAs(book.Answer);
        asks.Facts.Count.ShouldBe(1); // it did ask; nothing it found was the series' entry
    }

    [Fact]
    public async Task Only_the_releases_that_hold_the_series_and_the_words_are_offered()
    {
        var grounding = Grounding(new Scripted(false, Release("Star Trek: Nemesis", 10), Release("Star Trek: First Contact", 12)));
        var asks = new Asks();

        var answers = await RetryAsync(
            grounding,
            Retry(grounding, asks, "STAR TREK FIASI CONTACI", At(0.30), Matched("Star Trek: First Contact", 12)),
            Retry(grounding, asks, "nEMESIS", At(0.36), Nothing));

        Titles(answers[1]).ShouldBe(["Star Trek: Nemesis"]);
    }

    // ---- the budget ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_read_that_found_nothing_goes_pending_when_the_budget_ran_out_before_its_ask()
    {
        var grounding = Grounding(new Scripted(false, Franchise));
        var asks = new Asks();
        using var spent = new CancellationTokenSource();
        await spent.CancelAsync();

        var answers = await RetryAsync(
            grounding,
            Retry(grounding, asks, "STAR TREK FIASI CONTACI", At(0.30), Matched("Star Trek: First Contact", 12), budget: spent.Token),
            Retry(grounding, asks, "nEMESIS", At(0.36), Nothing, budget: spent.Token));

        answers[1].Verdict.ShouldBe(ResolutionVerdict.Pending);
        answers[1].Steps[^1].ShouldBe(new TraceStep("series", "cut_short", 0));
        asks.Facts.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_read_that_found_nothing_goes_pending_when_the_providers_could_not_answer_its_ask()
    {
        var grounding = Grounding(new Scripted(down: true));
        var asks = new Asks();

        var answers = await RetryAsync(
            grounding,
            Retry(grounding, asks, "STAR TREK FIASI CONTACI", At(0.30), Matched("Star Trek: First Contact", 12)),
            Retry(grounding, asks, "nEMESIS", At(0.36), Nothing));

        answers[1].Verdict.ShouldBe(ResolutionVerdict.Pending);
        answers[1].Steps[^1].ShouldBe(new TraceStep("series", "cut_short", 0));
        asks.Facts.Count.ShouldBe(1);
    }

    [Fact]
    public async Task An_ambiguous_read_keeps_its_answer_when_the_budget_cut_its_ask_short()
    {
        var grounding = Grounding(new Scripted(false, Franchise));
        var asks = new Asks();
        using var spent = new CancellationTokenSource();
        await spent.CancelAsync();
        var book = Retry(grounding, asks, "nEMESIS", At(0.36), Ambiguous("Nemesis", 14), budget: spent.Token);

        var answers = await RetryAsync(grounding, Retry(grounding, asks, "STAR TREK FIASI CONTACI", At(0.30), Matched("Star Trek: First Contact", 12), budget: spent.Token), book);

        answers[1].ShouldBeSameAs(book.Answer); // a pickable answer is not worth less because the extra ask had no time
    }

    [Fact]
    public async Task An_ambiguous_read_keeps_its_answer_when_the_providers_could_not_answer_its_ask()
    {
        var grounding = Grounding(new Scripted(down: true));
        var asks = new Asks();
        var book = Retry(grounding, asks, "nEMESIS", At(0.36), Ambiguous("Nemesis", 14));

        var answers = await RetryAsync(grounding, Retry(grounding, asks, "STAR TREK FIASI CONTACI", At(0.30), Matched("Star Trek: First Contact", 12)), book);

        answers[1].ShouldBeSameAs(book.Answer);
    }

    // ---- releases the catalogue holds ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_neighbour_the_catalogue_found_names_its_series_through_its_title_and_the_release_found_may_be_the_catalogues_too()
    {
        var firstContact = LocalFilm("Star Trek: First Contact");
        var nemesis = LocalFilm("Star Trek: Nemesis");
        var catalogue = new Catalogue(firstContact, nemesis);
        var grounding = Grounding(new Scripted(), catalogue);
        var asks = new Asks();
        var titleCalls = new List<Guid[]>();
        var neighbour = new BarcodeResolution(
            ResolutionVerdict.Matched, [new ResolutionOption(firstContact.Release.Id, null, 0.97)], null, []);

        var answers = await grounding.RetrySeriesAsync(
            [Retry(grounding, asks, "STAR TREK FIASI CONTACI", At(0.30), neighbour), Retry(grounding, asks, "nEMESIS", At(0.36), Nothing)],
            (ids, _) =>
            {
                titleCalls.Add([.. ids]);
                return Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string> { [firstContact.Release.Id] = firstContact.Work.Title });
            },
            CancellationToken.None);

        titleCalls.ShouldHaveSingleItem().ShouldBe([firstContact.Release.Id]);
        answers[1].Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        answers[1].Options.Single().LocalReleaseId.ShouldBe(nemesis.Release.Id);
        answers[1].FoundByTrimmedSpan.ShouldBeTrue();
        catalogue.Lookups.ShouldBe(["star trek nemesis"]);
    }

    [Fact]
    public async Task The_catalogue_is_not_asked_for_titles_when_no_read_can_ask()
    {
        var grounding = Grounding(new Scripted(false, Franchise));
        var asks = new Asks();
        var calls = 0;
        var local = new BarcodeResolution(ResolutionVerdict.Matched, [new ResolutionOption(Guid.NewGuid(), null, 0.97)], null, []);

        await grounding.RetrySeriesAsync(
            [Retry(grounding, asks, "STAR TREK FIASI CONTACI", At(0.30), local), Retry(grounding, asks, "CASABLANCA", At(0.36), local)],
            (_, _) =>
            {
                calls++;
                return NoTitles([], CancellationToken.None);
            },
            CancellationToken.None);

        calls.ShouldBe(0);
    }

    private static LocalHit LocalFilm(string title)
    {
        var work = new Work { Id = Guid.NewGuid(), Kind = WorkKind.Film, Title = title, NormalizedTitle = TitleNormalizer.Normalize(title), CreatorsJson = "[]" };
        return new LocalHit(new Release { Id = Guid.NewGuid(), WorkId = work.Id, Format = ReleaseFormat.Dvd }, work);
    }
}
