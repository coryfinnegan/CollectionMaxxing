using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using Microsoft.Extensions.Logging.Abstractions;
using static CollectionMaxxing.Application.Tests.Scanning.Identify.TrimmedRetryHarness;

namespace CollectionMaxxing.Application.Tests.Scanning.Identify;

// A cover prints a possessive name in front of its title, and the model sometimes reads the tagline in with it: "WALT DISNEY'S
// MASTERPIECE PINOCCHIO" found the collection "Walt Disney Masterpieces" and never the film. A cover (or a spine) that found
// nothing, or found only a release it is not sure of, is asked once more without the name, and what that finds is put first and
// offered, never matched. The possessive can be the real title ("Bram Stoker's Dracula", "Ender's Game"), so the releases the read
// found stay among the options, and a read that matched is never touched.
public class IdentifyGroundingPossessiveRetryTests
{
    private const string Misread = "WALT DISNEY'S MASTERPIECE PINOCCHIO";

    private const string Collection = "Walt Disney Masterpieces";

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static ReadFacts FilmRead(string title) => new(
        title, title, null, null, null, WorkKind.Film, ReleaseFormat.Vhs, null, null);

    private static ReadFacts BookRead(string title) => new(
        title, title, null, null, null, WorkKind.Book, null, null, null);

    /// <summary>A catalogue that finds a release by its exact title, and the releases that share a word with the search where the test says so (as the trigram search finds a near title).</summary>
    private static Catalogue Near(string word, params LocalHit[] hits) => new(hits)
    {
        Finds = (hit, search) => hit.Work.NormalizedTitle == search.NormalizedTitle
            || (search.NormalizedTitle.Contains(word, StringComparison.Ordinal) && hit.Work.NormalizedTitle.Contains(word, StringComparison.Ordinal)),
    };

    private static IdentifyGrounding Grounding(Catalogue catalogue, Providers? providers = null) => new(
        new TextSearchResolver(null!, catalogue, [providers ?? new Providers()], new ScanningOptions(), NullLogger<TextSearchResolver>.Instance),
        new FixedScoring());

    private static Task<BarcodeResolution> GroundAsync(IdentifyGrounding grounding, ReadFacts facts, ScoringProfileKind profile) =>
        grounding.GroundAsync(facts, profile, Budget, CancellationToken.None);

    private static string Key(string title) => TitleNormalizer.Normalize(title);

    private static Guid[] Ids(BarcodeResolution answer) => [.. answer.Options.Select(o => o.LocalReleaseId!.Value)];

    /// <summary>What the read found on its own: the same read scored as a bare label, which is never asked again.</summary>
    private static Task<BarcodeResolution> WithoutTheRetryAsync(Catalogue catalogue, ReadFacts facts) =>
        GroundAsync(Grounding(catalogue), facts, ScoringProfileKind.BareLabel);

    // ---- a cover ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_cover_that_found_only_the_wrong_release_offers_the_title_without_the_name_first_and_keeps_what_it_found()
    {
        var pinocchio = Film("Pinocchio");
        var masterpieces = Film(Collection);
        var catalogue = Near("masterpiece", masterpieces, pinocchio);

        var answer = await GroundAsync(Grounding(catalogue), FilmRead(Misread), ScoringProfileKind.Cover);

        answer.Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        Ids(answer).ShouldBe([pinocchio.Release.Id, masterpieces.Release.Id]);
        answer.FoundByTrimmedSpan.ShouldBeTrue();
        catalogue.Lookups.ShouldBe([Key(Misread), "pinocchio"]);
    }

    [Fact]
    public async Task The_same_cover_read_is_ambiguous_on_the_wrong_release_without_the_retry_in_the_same_catalogue()
    {
        // Control: the read as it was is Ambiguous on the collection, so it is the retry, on an Ambiguous read, that found the film.
        var masterpieces = Film(Collection);
        var catalogue = Near("masterpiece", masterpieces, Film("Pinocchio"));

        var answer = await WithoutTheRetryAsync(catalogue, FilmRead(Misread));

        answer.Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        Ids(answer).ShouldBe([masterpieces.Release.Id]);
        answer.FoundByTrimmedSpan.ShouldBeFalse();
        catalogue.Lookups.ShouldBe([Key(Misread)]);
    }

    [Fact]
    public async Task The_trace_says_which_words_were_dropped_after_the_lookup_of_the_whole_read()
    {
        var catalogue = Near("masterpiece", Film(Collection), Film("Pinocchio"));

        var answer = await GroundAsync(Grounding(catalogue), FilmRead(Misread), ScoringProfileKind.Cover);

        answer.Steps.ShouldContain(new TraceStep("possessive", "PINOCCHIO (dropped: WALT DISNEY'S MASTERPIECE)", 0));
        var steps = answer.Steps.Select(s => s.Provider).ToList();
        steps.IndexOf("catalogue").ShouldBeLessThan(steps.IndexOf("possessive"));
        steps.Last().ShouldBe("profile");
    }

    [Fact]
    public async Task What_the_title_without_the_name_found_would_have_matched_but_is_only_offered()
    {
        var catalogue = Near("masterpiece", Film(Collection), Film("Pinocchio"));

        var answer = await GroundAsync(Grounding(catalogue), FilmRead(Misread), ScoringProfileKind.Cover);

        answer.Options[0].Score.ShouldBeGreaterThanOrEqualTo(new ScoringThresholds().MatchedScore);
        answer.Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
    }

    [Fact]
    public async Task A_cover_that_found_nothing_is_offered_the_release_the_title_without_the_name_found()
    {
        var pinocchio = Film("Pinocchio");
        var catalogue = new Catalogue(pinocchio);

        var answer = await GroundAsync(Grounding(catalogue), FilmRead(Misread), ScoringProfileKind.Cover);

        answer.Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        Ids(answer).ShouldBe([pinocchio.Release.Id]);
        answer.FoundByTrimmedSpan.ShouldBeTrue();
        answer.Steps.ShouldContain(s => s.Provider == "possessive");
        catalogue.Lookups.ShouldBe([Key(Misread), "pinocchio"]);
    }

    [Fact]
    public async Task A_title_without_the_name_is_asked_with_the_reads_other_facts_and_what_was_read_stays_the_original_text()
    {
        var grounding = Grounding(new Catalogue());
        var facts = FilmRead(Misread) with { Year = 1940, KindContext = WorkKind.Film };
        var notFound = grounding.Score(facts, ScoringProfileKind.Cover, LocalPool.Empty, ProviderAnswers.None);
        var asked = new List<ReadFacts>();

        var answers = await grounding.RetryTrimmedAsync(
            [new TrimmedRetry(facts, ScoringProfileKind.Cover, notFound, f => { asked.Add(f); return Task.FromResult(ProviderAnswers.None); }, CancellationToken.None)],
            CancellationToken.None);

        var without = asked.Single();
        without.Title.ShouldBe("PINOCCHIO");
        without.Text.ShouldBe(Misread);
        without.Kind.ShouldBe(WorkKind.Film);
        without.Format.ShouldBe(ReleaseFormat.Vhs);
        without.Year.ShouldBe(1940);
        without.KindContext.ShouldBe(WorkKind.Film);
        facts.Title.ShouldBe(Misread);
        answers.Single().ShouldBeSameAs(notFound); // and nothing found: the answer is the one it had
    }

    // ---- the possessive is the real title ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("BRAM STOKER'S DRACULA", "Bram Stoker's Dracula", "Dracula")]
    [InlineData("BRAM STOKER’S DRACULA", "Bram Stoker's Dracula", "Dracula")]
    [InlineData("SCHINDLER'S LIST", "Schindler's List", "List")]
    [InlineData("CHARLOTTE'S WEB", "Charlotte's Web", "Web")]
    [InlineData("OCEAN'S ELEVEN", "Ocean's Eleven", "Eleven")]
    [InlineData("PAN'S LABYRINTH", "Pan's Labyrinth", "Labyrinth")]
    [InlineData("ENDER'S GAME", "Ender's Game", "Game")]
    public async Task An_ambiguous_read_whose_best_release_already_holds_the_possessive_name_is_not_asked_again(string read, string real, string generic)
    {
        // Two releases of the real title tie (two tapes, two remakes), and a release of the shorter title exists: asking would put it first.
        var first = Film(real);
        var second = Film(real);
        var catalogue = new Catalogue(first, second, Film(generic));
        var asRead = await WithoutTheRetryAsync(new Catalogue(first, second, Film(generic)), FilmRead(read));
        asRead.Verdict.ShouldBe(ResolutionVerdict.Ambiguous);

        var answer = await GroundAsync(Grounding(catalogue), FilmRead(read), ScoringProfileKind.Cover);

        answer.Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        Ids(answer).ShouldBe(Ids(asRead)); // both ties, in the order the read had them
        answer.FoundByTrimmedSpan.ShouldBeFalse();
        answer.Steps.ShouldNotContain(s => s.Provider == "possessive");
        catalogue.Lookups.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Two_reads_of_a_real_possessive_title_still_collapse_into_one_item()
    {
        var catalogue = new Catalogue(Film("Bram Stoker's Dracula"), Film("Bram Stoker's Dracula"), Film("Dracula"));
        var grounding = Grounding(catalogue);

        var visionRead = await GroundAsync(grounding, FilmRead("BRAM STOKER'S DRACULA"), ScoringProfileKind.Cover);
        var modelRead = await GroundAsync(grounding, FilmRead("Bram Stoker's Dracula"), ScoringProfileKind.Cover);

        RepeatReads.Of([visionRead, modelRead]).Count.ShouldBe(1);
        visionRead.FoundByTrimmedSpan.ShouldBeFalse(); // so each can still confirm the other engine's read
        modelRead.FoundByTrimmedSpan.ShouldBeFalse();
    }

    [Fact]
    public async Task A_one_word_left_still_asks_a_read_that_found_nothing()
    {
        var game = Film("Game");
        var catalogue = new Catalogue(game);

        var answer = await GroundAsync(Grounding(catalogue), FilmRead("ENDER'S GAME"), ScoringProfileKind.Cover);

        Ids(answer).ShouldBe([game.Release.Id]);
        answer.FoundByTrimmedSpan.ShouldBeTrue();
        catalogue.Lookups.ShouldBe([Key("ENDER'S GAME"), "game"]);
    }

    [Fact]
    public async Task The_best_release_the_read_found_keeps_a_place_when_the_title_without_the_name_finds_a_full_offer()
    {
        var first = Film(Collection);
        var second = Film(Collection);
        var pinocchios = new[] { Film("Pinocchio"), Film("Pinocchio"), Film("Pinocchio") }; // three tie: the find alone fills the offer
        var catalogue = Near("masterpiece", [first, second, .. pinocchios]);
        var original = await WithoutTheRetryAsync(Near("masterpiece", [first, second, .. pinocchios]), FilmRead(Misread));
        original.Options.Count.ShouldBe(2); // control: the two collections tie, in some order

        var answer = await GroundAsync(Grounding(catalogue), FilmRead(Misread), ScoringProfileKind.Cover);

        answer.Options.Count.ShouldBe(3);
        Ids(answer)[..2].ShouldAllBe(id => pinocchios.Any(p => p.Release.Id == id));
        Ids(answer)[2].ShouldBe(original.Options[0].LocalReleaseId!.Value);
    }

    [Fact]
    public async Task A_release_the_title_without_the_name_found_again_is_offered_once()
    {
        // The whole read finds both releases, and so does the shorter title.
        var catalogue = Near("pinocchio", Film("Pinocchio"), Film("Walt Disney Masterpieces Pinocchio"));

        var answer = await GroundAsync(Grounding(catalogue), FilmRead(Misread), ScoringProfileKind.Cover);

        Ids(answer).Distinct().Count().ShouldBe(answer.Options.Count);
        answer.Options.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_find_that_scores_below_the_best_release_the_read_found_is_put_after_it_and_the_read_keeps_its_standing()
    {
        // The read's two tied releases do not hold "Tess Harrowgate's" in their title, but they are the better answer: the title
        // without the name only finds a longer title that merely starts with the words.
        var first = Book("Tess Harrowgate Copper Lantern");
        var second = Book("Tess Harrowgate Copper Lantern");
        var tales = Book("Copper Lantern Tales");
        const string Read = "TESS HARROWGATE'S COPPER LANTERN";
        Catalogue Holding() => new(first, second, tales)
        {
            Finds = (hit, search) => hit.Work.NormalizedTitle == search.NormalizedTitle
                || (search.NormalizedTitle.Contains("harrowgate", StringComparison.Ordinal) && hit.Work.NormalizedTitle.Contains("harrowgate", StringComparison.Ordinal))
                || (search.NormalizedTitle == "copper lantern" && hit.Work.NormalizedTitle.StartsWith("copper lantern", StringComparison.Ordinal)),
        };
        var asRead = await WithoutTheRetryAsync(Holding(), BookRead(Read));
        asRead.Verdict.ShouldBe(ResolutionVerdict.Ambiguous);

        var catalogue = Holding();
        var answer = await GroundAsync(Grounding(catalogue), BookRead(Read), ScoringProfileKind.Cover);

        catalogue.Lookups.ShouldBe([Key(Read), "copper lantern"]);
        answer.Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        Ids(answer).ShouldBe([asRead.Options[0].LocalReleaseId!.Value, tales.Release.Id, asRead.Options[1].LocalReleaseId!.Value]);
        answer.FoundByTrimmedSpan.ShouldBeFalse();
        answer.Steps.ShouldContain(s => s.Provider == "possessive");
        RepeatReads.Of([answer, asRead]).Count.ShouldBe(1); // the same best release as the read had
    }

    // ---- reads that are never asked again without a name ------------------------------------------------------------------------------

    [Fact]
    public async Task A_cover_that_already_matched_is_never_asked_again()
    {
        var catalogue = new Catalogue(Film("Stephen King's It"), Film("It"));

        var answer = await GroundAsync(Grounding(catalogue), FilmRead("STEPHEN KING'S IT"), ScoringProfileKind.Cover);

        answer.Verdict.ShouldBe(ResolutionVerdict.Matched);
        answer.FoundByTrimmedSpan.ShouldBeFalse();
        answer.Steps.ShouldNotContain(s => s.Provider == "possessive");
        catalogue.Lookups.ShouldBe([Key("STEPHEN KING'S IT")]);
    }

    [Theory]
    [InlineData("WALT DISNEY PINOCCHIO")]
    [InlineData("PINOCCHIO")]
    [InlineData("THE KING'S SPEECH")]
    public async Task A_cover_with_no_possessive_name_at_its_front_is_not_asked_again_whether_it_found_a_release_or_not(string title)
    {
        var lost = new Catalogue();
        var tied = new Catalogue(Film(title), Film(title)); // two releases tie: Ambiguous

        var notFound = await GroundAsync(Grounding(lost), FilmRead(title), ScoringProfileKind.Cover);
        var ambiguous = await GroundAsync(Grounding(tied), FilmRead(title), ScoringProfileKind.Cover);

        notFound.Verdict.ShouldBe(ResolutionVerdict.NotFound);
        ambiguous.Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        lost.Lookups.Count.ShouldBe(1);
        tied.Lookups.Count.ShouldBe(1);
        ambiguous.FoundByTrimmedSpan.ShouldBeFalse();
        ambiguous.Steps.ShouldNotContain(s => s.Provider == "possessive");
    }

    [Theory]
    [InlineData(ScoringProfileKind.BareLabel)]
    [InlineData(ScoringProfileKind.LooseCartridge)]
    [InlineData(ScoringProfileKind.BarcodeProduct)]
    public async Task A_read_scored_with_any_profile_but_the_cover_and_spine_ones_does_not_retry(ScoringProfileKind profile)
    {
        var catalogue = new Catalogue(Film("Pinocchio"));

        var answer = await GroundAsync(Grounding(catalogue), FilmRead(Misread), profile);

        answer.Verdict.ShouldBe(ResolutionVerdict.NotFound);
        catalogue.Lookups.ShouldBe([Key(Misread)]);
    }

    [Fact]
    public async Task A_cover_read_whose_title_a_product_code_decided_is_never_asked_again()
    {
        var catalogue = new Catalogue(Film("Pinocchio"));

        var answer = await GroundAsync(Grounding(catalogue), FilmRead(Misread) with { ProductCode = "SLUS-00001" }, ScoringProfileKind.Cover);

        answer.Verdict.ShouldBe(ResolutionVerdict.NotFound);
        catalogue.Lookups.ShouldBe([Key(Misread)]);
    }

    [Fact]
    public async Task A_cover_read_a_provider_could_not_answer_is_pending_and_does_not_retry()
    {
        var catalogue = new Catalogue(Book("Pinocchio"));

        var answer = await GroundAsync(Grounding(catalogue, new Providers(down: true)), BookRead(Misread), ScoringProfileKind.Cover);

        answer.Verdict.ShouldBe(ResolutionVerdict.Pending);
        catalogue.Lookups.ShouldBe([Key(Misread)]);
    }

    // ---- with the sequel number ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_cover_that_found_nothing_is_asked_without_its_name_before_it_is_asked_without_its_sequel_number()
    {
        var catalogue = new Catalogue();
        const string Title = "TESS HARROWGATE'S COPPER LANTERN II";

        var answer = await GroundAsync(Grounding(catalogue), FilmRead(Title), ScoringProfileKind.Cover);

        answer.Verdict.ShouldBe(ResolutionVerdict.NotFound);
        catalogue.Lookups.ShouldBe([Key(Title), Key("COPPER LANTERN II"), Key("TESS HARROWGATE'S COPPER LANTERN")]);
    }

    [Fact]
    public async Task A_cover_that_the_title_without_its_name_found_is_not_asked_without_its_sequel_number()
    {
        var lantern = Film("Copper Lantern II");
        var catalogue = new Catalogue(lantern);

        var answer = await GroundAsync(Grounding(catalogue), FilmRead("TESS HARROWGATE'S COPPER LANTERN II"), ScoringProfileKind.Cover);

        Ids(answer).ShouldBe([lantern.Release.Id]);
        answer.Steps.ShouldContain(s => s.Provider == "possessive");
        answer.Steps.ShouldNotContain(s => s.Provider == "numeral");
    }

    // ---- a spine ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_spine_that_found_nothing_asks_the_title_without_the_name_before_its_trimmed_spans()
    {
        var lantern = Book("Copper Lantern");
        var catalogue = new Catalogue(lantern);
        const string Title = "TESS HARROWGATE'S COPPER LANTERN";

        var answer = await GroundAsync(Grounding(catalogue), BookRead(Title), ScoringProfileKind.ShelfSpine);

        Ids(answer).ShouldBe([lantern.Release.Id]);
        answer.FoundByTrimmedSpan.ShouldBeTrue();
        answer.Steps.ShouldContain(new TraceStep("possessive", "COPPER LANTERN (dropped: TESS HARROWGATE'S)", 0));
        catalogue.Lookups.ShouldBe([Key(Title), "copper lantern"]);
    }

    [Fact]
    public async Task A_title_a_spine_span_would_ask_anyway_is_not_asked_a_second_time_when_the_name_was_dropped_first()
    {
        var grounding = Grounding(new Catalogue());
        var facts = BookRead("TESS HARROWGATE'S COPPER LANTERN");
        var notFound = grounding.Score(facts, ScoringProfileKind.ShelfSpine, LocalPool.Empty, ProviderAnswers.None);
        var asked = new List<string?>();

        await grounding.RetryTrimmedAsync(
            [new TrimmedRetry(facts, ScoringProfileKind.ShelfSpine, notFound, f => { asked.Add(f.Title); return Task.FromResult(ProviderAnswers.None); }, CancellationToken.None)],
            CancellationToken.None);

        asked.Count(title => string.Equals(title, "COPPER LANTERN", StringComparison.OrdinalIgnoreCase)).ShouldBe(1);
        asked[0].ShouldBe("COPPER LANTERN");
    }

    [Fact]
    public async Task A_spine_that_found_only_a_release_it_is_not_sure_of_offers_the_title_without_the_name_first()
    {
        var collection = Book(Collection);
        var pinocchio = Book("Pinocchio");
        var catalogue = Near("masterpiece", collection, pinocchio);

        var answer = await GroundAsync(Grounding(catalogue), BookRead(Misread), ScoringProfileKind.ShelfSpine);

        answer.Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        Ids(answer).ShouldBe([pinocchio.Release.Id, collection.Release.Id]);
        answer.FoundByTrimmedSpan.ShouldBeTrue();
        catalogue.Lookups.ShouldBe([Key(Misread), "pinocchio"]);
    }

    // ---- the budget: a retry the time ran out on is not an answer -------------------------------------------------------------------

    /// <summary>An Ambiguous cover read of a collection, and the retry of it with <paramref name="ask"/> as the providers' answer.</summary>
    private static async Task<(BarcodeResolution Original, BarcodeResolution Retried)> RetryAmbiguousAsync(
        Func<CancellationTokenSource, Task<ProviderAnswers>> ask, bool budgetSpentAlready = false)
    {
        var catalogue = Near("masterpiece", Film(Collection)); // nothing for the title without the name, so only a provider could have found it
        var grounding = Grounding(catalogue);
        var facts = FilmRead(Misread);
        var original = grounding.Score(facts, ScoringProfileKind.Cover, await grounding.FindLocalAsync(facts, CancellationToken.None), ProviderAnswers.None);
        original.Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        using var budget = new CancellationTokenSource();
        if (budgetSpentAlready)
        {
            await budget.CancelAsync();
        }

        var answers = await grounding.RetryTrimmedAsync(
            [new TrimmedRetry(facts, ScoringProfileKind.Cover, original, _ => ask(budget), budget.Token)],
            CancellationToken.None);
        return (original, answers.Single());
    }

    [Fact]
    public async Task An_ambiguous_cover_whose_budget_was_spent_before_the_retry_was_tried_keeps_its_answer()
    {
        var (original, retried) = await RetryAmbiguousAsync(_ => Task.FromResult(ProviderAnswers.None), budgetSpentAlready: true);

        retried.ShouldBeSameAs(original);
    }

    [Fact]
    public async Task An_ambiguous_cover_whose_title_without_the_name_the_providers_could_not_answer_keeps_its_answer()
    {
        var (original, retried) = await RetryAmbiguousAsync(_ => Task.FromResult(new ProviderAnswers([], [new TraceStep("igdb", "failed", 0)], true)));

        retried.ShouldBeSameAs(original);
    }

    [Fact]
    public async Task An_ambiguous_cover_whose_provider_call_the_budget_cancelled_keeps_its_answer()
    {
        var (original, retried) = await RetryAmbiguousAsync(async budget =>
        {
            await budget.CancelAsync();
            return new ProviderAnswers([], [new TraceStep("igdb", "failed", 0)], true);
        });

        retried.ShouldBeSameAs(original);
    }

    [Fact]
    public async Task A_cover_that_found_nothing_and_whose_budget_was_spent_before_the_retry_was_tried_goes_pending()
    {
        var grounding = Grounding(new Catalogue());
        var facts = FilmRead(Misread);
        var notFound = grounding.Score(facts, ScoringProfileKind.Cover, LocalPool.Empty, ProviderAnswers.None);
        using var budget = new CancellationTokenSource();
        await budget.CancelAsync();

        var answers = await grounding.RetryTrimmedAsync(
            [new TrimmedRetry(facts, ScoringProfileKind.Cover, notFound, _ => Task.FromResult(ProviderAnswers.None), budget.Token)],
            CancellationToken.None);

        answers.Single().Verdict.ShouldBe(ResolutionVerdict.Pending);
        answers.Single().Steps.ShouldBe([.. notFound.Steps, new TraceStep("trimmed", "cut_short", 0)]);
    }
}
