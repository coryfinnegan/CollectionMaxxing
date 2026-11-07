using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;
using Microsoft.Extensions.Logging.Abstractions;

namespace CollectionMaxxing.Application.Tests.Scanning;

/// <summary>
/// Stage 1 (#438): the providers are asked with title + author first; a title-only query is the fallback, not the
/// default. It fires only when the qualified round ran fine and found nothing — never on a hit, never on a failure.
/// </summary>
public class TextSearchFallbackTests
{
    private const string Title = "Suddenly You";
    private const string Author = "Lisa Kleypas";

    private static ProviderRelease KleypasBook() => new()
    {
        Provider = "openlibrary",
        ExternalId = "OL7353617M",
        Title = Title,
        Kind = WorkKind.Book,
        Year = 2001,
        Creators = [Author],
    };

    /// <summary>A provider that answers from a script and remembers every query it was asked.</summary>
    private sealed class RecordingProvider(Func<CatalogQuery, IReadOnlyList<ProviderRelease>> answer, string name = "openlibrary", WorkKind kind = WorkKind.Book) : ICatalogProvider
    {
        public List<CatalogQuery> Queries { get; } = [];

        public string Name => name;

        public bool Supports(WorkKind asked) => asked == kind;

        public Task<ProviderRelease?> LookupAsync(Identifier id, CancellationToken ct) => Task.FromResult<ProviderRelease?>(null);

        public Task<IReadOnlyList<ProviderRelease>> SearchAsync(CatalogQuery query, CancellationToken ct)
        {
            Queries.Add(query);
            return Task.FromResult(answer(query));
        }
    }

    private static TextSearchResolver Resolver(params ICatalogProvider[] providers) => new(
        null!, null!, providers, new ScanningOptions(), NullLogger<TextSearchResolver>.Instance);

    [Fact]
    public async Task An_author_qualified_search_that_finds_nothing_falls_back_to_title_only()
    {
        var book = KleypasBook();
        var provider = new RecordingProvider(query => query.Creator is null ? [book] : []);
        var resolver = Resolver(provider);

        var answers = await resolver.AskProvidersAsync(
            new CatalogQuery { Title = Title, Creator = Author, Kind = WorkKind.Book }, CancellationToken.None, CancellationToken.None);

        answers.Found.ShouldBe([book]);
        answers.AnyFailed.ShouldBeFalse();
        provider.Queries.Count.ShouldBe(2);
        provider.Queries[0].Creator.ShouldBe(Author);
        provider.Queries[1].Title.ShouldBe(Title);
        provider.Queries[1].Creator.ShouldBeNull();
    }

    [Fact]
    public async Task An_author_qualified_search_that_hits_is_never_asked_twice()
    {
        var book = KleypasBook();
        var provider = new RecordingProvider(_ => [book]);
        var resolver = Resolver(provider);

        var answers = await resolver.AskProvidersAsync(
            new CatalogQuery { Title = Title, Creator = Author, Kind = WorkKind.Book }, CancellationToken.None, CancellationToken.None);

        answers.Found.ShouldBe([book]);
        provider.Queries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task An_author_qualified_search_that_finds_only_unrelated_books_falls_back_to_title_only_and_keeps_both()
    {
        // A bare-words search with a misread author (Google Books) still answers, with books that have nothing to do with the
        // title; that used to stop the fallback, so a spine whose author was misread found nothing (first eval run).
        var book = KleypasBook();
        var unrelated = KleypasBook() with { ExternalId = "OL1M", Title = "A Field Guide to North American Birds" };
        var provider = new RecordingProvider(query => query.Creator is null ? [book] : [unrelated]);
        var resolver = Resolver(provider);

        var answers = await resolver.AskProvidersAsync(
            new CatalogQuery { Title = Title, Creator = "LISA KLEYPAs NOVEL", Kind = WorkKind.Book }, CancellationToken.None, CancellationToken.None);

        answers.Found.ShouldBe([unrelated, book]);
        provider.Queries.Count.ShouldBe(2);
        provider.Queries[1].Creator.ShouldBeNull();
    }

    [Fact]
    public async Task A_failed_provider_is_not_asked_twice()
    {
        var provider = new RecordingProvider(_ => throw new InvalidOperationException("the provider is down"));
        var resolver = Resolver(provider);

        var answers = await resolver.AskProvidersAsync(
            new CatalogQuery { Title = Title, Creator = Author, Kind = WorkKind.Book }, CancellationToken.None, CancellationToken.None);

        answers.Found.ShouldBeEmpty();
        answers.AnyFailed.ShouldBeTrue();
        provider.Queries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Without_an_author_there_is_nothing_to_fall_back_from()
    {
        var provider = new RecordingProvider(_ => []);
        var resolver = Resolver(provider);

        var answers = await resolver.AskProvidersAsync(
            new CatalogQuery { Title = Title, Kind = WorkKind.Book }, CancellationToken.None, CancellationToken.None);

        answers.Found.ShouldBeEmpty();
        provider.Queries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Each_provider_falls_back_on_its_own_so_a_close_companion_elsewhere_does_not_stop_it()
    {
        // Second eval run: with a misread author, one provider found nothing while the other's bare-words search answered with
        // a companion volume of the same title. That counted as close for both, so the real book was never asked for.
        var book = KleypasBook();
        var companion = KleypasBook() with { Provider = "googlebooks", ExternalId = "gb-1", Title = Title + ": A Reader's Companion" };
        var strict = new RecordingProvider(query => query.Creator is null ? [book] : []);
        var loose = new RecordingProvider(_ => [companion], "googlebooks");
        var resolver = Resolver(strict, loose);

        var answers = await resolver.AskProvidersAsync(
            new CatalogQuery { Title = Title, Creator = "LISA KLEYPOS", Kind = WorkKind.Book }, CancellationToken.None, CancellationToken.None);

        answers.Found.ShouldContain(book);
        answers.Found.ShouldContain(companion);
        strict.Queries.Count.ShouldBe(2);
        loose.Queries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_read_with_junk_after_the_title_is_asked_again_without_its_last_words()
    {
        // OCR runs a spine's other print into the title ("... NIGHT QXRTVZ"): the title search finds nothing with it.
        var book = KleypasBook();
        var provider = new RecordingProvider(query => query.Title == Title ? [book] : []);
        var resolver = Resolver(provider);

        var answers = await resolver.AskProvidersAsync(
            new CatalogQuery { Title = Title + " AVON QXRTVZ", Kind = WorkKind.Book }, CancellationToken.None, CancellationToken.None);

        answers.Found.ShouldBe([book]);
        provider.Queries.Select(q => q.Title).ShouldBe([Title + " AVON QXRTVZ", Title + " AVON", Title]);
    }

    [Fact]
    public async Task At_most_two_words_are_dropped_and_at_least_two_are_left()
    {
        var provider = new RecordingProvider(_ => []);
        var resolver = Resolver(provider);

        await resolver.AskProvidersAsync(new CatalogQuery { Title = "one two three four five", Kind = WorkKind.Book }, CancellationToken.None, CancellationToken.None);
        await resolver.AskProvidersAsync(new CatalogQuery { Title = "one two three", Kind = WorkKind.Book }, CancellationToken.None, CancellationToken.None);

        provider.Queries.Select(q => q.Title).ShouldBe(
        [
            "one two three four five", "one two three four", "one two three", "two three four five", "three four five",
            "one two three", "one two", "two three",
        ]);
    }

    [Fact]
    public async Task A_two_word_read_is_not_asked_without_its_first_word_but_a_three_word_read_is()
    {
        // A sticker number, a stray pair of letters or an author's first name stands in front of plenty of three-word
        // reads, and the rest of the read finds the title. A two-word read is all title: dropping one leaves a word that
        // finds anything.
        (await TitlesAskedAsync("TNX EVERYONE")).ShouldBe(["TNX EVERYONE"]);
        (await TitlesAskedAsync("TNX for EVERYONE")).ShouldBe(["TNX for EVERYONE", "TNX for", "for EVERYONE"]);
        (await TitlesAskedAsync("TNX for EVERYONE now")).ShouldBe(["TNX for EVERYONE now", "TNX for EVERYONE", "TNX for", "for EVERYONE now", "EVERYONE now"]);
    }

    [Fact]
    public async Task A_three_word_read_with_a_number_is_still_asked_with_the_number_written_the_other_way()
    {
        (await TitlesAskedAsync("Camp 3 Wasteland"))
            .ShouldBe(["Camp 3 Wasteland", "Camp 3", "Camp III Wasteland", "3 Wasteland", "III Wasteland"]);
    }

    [Fact]
    public async Task The_shortened_titles_are_asked_without_the_author()
    {
        var provider = new RecordingProvider(_ => []);
        var resolver = Resolver(provider);

        await resolver.AskProvidersAsync(
            new CatalogQuery { Title = Title + " AVON", Creator = Author, Kind = WorkKind.Book }, CancellationToken.None, CancellationToken.None);

        provider.Queries.Select(q => (q.Title, q.Creator))
            .ShouldBe([(Title + " AVON", Author), (Title + " AVON", null), (Title, null), ("You AVON", null)]);
    }

    [Fact]
    public async Task A_provider_that_answers_close_to_a_shortened_title_is_not_asked_shorter()
    {
        var book = KleypasBook();
        var provider = new RecordingProvider(query => query.Title == Title + " AVON" ? [book] : []);
        var resolver = Resolver(provider);

        var answers = await resolver.AskProvidersAsync(
            new CatalogQuery { Title = Title + " AVON XQZT", Kind = WorkKind.Book }, CancellationToken.None, CancellationToken.None);

        answers.Found.ShouldBe([book]);
        provider.Queries.Count.ShouldBe(2);
    }

    private const string SleepawayRead = "SLEEP AWAY CAMP 3 TÉENAGE WASTELAND";

    private static ProviderRelease SleepawayCampIii() => new()
    {
        Provider = "tmdb",
        ExternalId = "sleepaway-camp-iii",
        Title = "Sleepaway Camp III: Teenage Wasteland",
        Kind = WorkKind.Film,
        Year = 1989,
    };

    /// <summary>Every title a provider that finds nothing is asked, in order.</summary>
    private static async Task<List<string>> TitlesAskedAsync(string title)
    {
        var provider = new RecordingProvider(_ => [], kind: WorkKind.Film);
        await Resolver(provider).AskProvidersAsync(
            new CatalogQuery { Title = title, Kind = WorkKind.Film }, CancellationToken.None, CancellationToken.None);
        return [.. provider.Queries.Select(q => q.Title)];
    }

    [Fact]
    public async Task An_accent_on_an_english_word_is_dropped_from_every_rung()
    {
        var titles = await TitlesAskedAsync("Café Du Monde Trailer");

        titles.ShouldBe(["Cafe Du Monde Trailer", "Cafe Du Monde", "Cafe Du", "Du Monde Trailer", "Monde Trailer"]);
    }

    [Fact]
    public async Task A_read_with_a_numeral_and_a_misread_first_word_is_tried_with_the_numeral_written_the_other_way_then_without_its_leading_words()
    {
        var titles = await TitlesAskedAsync(SleepawayRead);

        titles.ShouldBe(
        [
            "SLEEP AWAY CAMP 3 TEENAGE WASTELAND",
            "SLEEP AWAY CAMP 3 TEENAGE",
            "SLEEP AWAY CAMP 3",
            "SLEEP AWAY CAMP III TEENAGE WASTELAND",
            "AWAY CAMP 3 TEENAGE WASTELAND",
            "AWAY CAMP III TEENAGE WASTELAND",
            "CAMP 3 TEENAGE WASTELAND",
            "CAMP III TEENAGE WASTELAND",
        ]);
    }

    [Fact]
    public async Task A_film_whose_catalogue_title_has_a_roman_numeral_is_found_from_a_read_with_the_number_an_accent_and_a_misread_first_word()
    {
        var film = SleepawayCampIii();
        var provider = new RecordingProvider(
            query => string.Equals(query.Title, "Camp III Teenage Wasteland", StringComparison.OrdinalIgnoreCase) ? [film] : [], "tmdb", WorkKind.Film);

        var answers = await Resolver(provider).AskProvidersAsync(
            new CatalogQuery { Title = SleepawayRead, Kind = WorkKind.Film }, CancellationToken.None, CancellationToken.None);

        answers.Found.ShouldBe([film]);
        answers.AnyFailed.ShouldBeFalse();
        provider.Queries.Last().Title.ShouldBe("CAMP III TEENAGE WASTELAND");
    }

    [Fact]
    public async Task A_provider_that_answers_close_to_a_rung_with_the_numeral_swapped_is_not_asked_the_rungs_after_it()
    {
        var film = SleepawayCampIii();
        var provider = new RecordingProvider(
            query => string.Equals(query.Title, "SLEEP AWAY CAMP III TEENAGE WASTELAND", StringComparison.OrdinalIgnoreCase) ? [film] : [], "tmdb", WorkKind.Film);

        var answers = await Resolver(provider).AskProvidersAsync(
            new CatalogQuery { Title = SleepawayRead, Kind = WorkKind.Film }, CancellationToken.None, CancellationToken.None);

        answers.Found.ShouldBe([film]);
        provider.Queries.Select(q => q.Title).ShouldBe(
            ["SLEEP AWAY CAMP 3 TEENAGE WASTELAND", "SLEEP AWAY CAMP 3 TEENAGE", "SLEEP AWAY CAMP 3", "SLEEP AWAY CAMP III TEENAGE WASTELAND"]);
    }

    [Fact]
    public async Task A_roman_numeral_is_also_tried_as_an_arabic_one()
    {
        var titles = await TitlesAskedAsync("Sleepaway Camp III Wasteland");

        titles.ShouldBe(
        [
            "Sleepaway Camp III Wasteland", "Sleepaway Camp III", "Sleepaway Camp", "Sleepaway Camp 3 Wasteland",
            "Camp III Wasteland", "Camp 3 Wasteland", "III Wasteland", "3 Wasteland",
        ]);
    }

    [Fact]
    public async Task A_1_and_a_10_are_tried_as_I_and_X_but_a_lone_I_or_X_is_never_tried_as_a_number()
    {
        (await TitlesAskedAsync("Final Fantasy 10")).ShouldContain("Final Fantasy X");
        (await TitlesAskedAsync("Resident Evil 1")).ShouldContain("Resident Evil I");

        (await TitlesAskedAsync("I Am Legend")).ShouldBe(["I Am Legend", "I Am", "Am Legend"]);
        (await TitlesAskedAsync("Malcolm X")).ShouldBe(["Malcolm X"]);
        (await TitlesAskedAsync("Mega Man X Legacy")).ShouldBe(["Mega Man X Legacy", "Mega Man X", "Mega Man", "Man X Legacy", "X Legacy"]);
    }

    [Fact]
    public async Task Only_a_whole_word_number_up_to_twenty_is_written_as_a_roman_numeral()
    {
        var titles = new List<string>();
        titles.AddRange(await TitlesAskedAsync("Catch 22 Again"));
        titles.AddRange(await TitlesAskedAsync("Blade Runner 2049 Again"));
        titles.AddRange(await TitlesAskedAsync("Se7en Again Now"));
        titles.AddRange(await TitlesAskedAsync("Stage 3D Again"));

        // No word of any rung is made only of roman digits.
        titles.SelectMany(title => title.Split(' ')).ShouldAllBe(word => word.Any(letter => "IVX".IndexOf(letter) < 0));
    }

    [Fact]
    public async Task A_rung_that_is_identical_to_an_earlier_one_is_not_asked_again()
    {
        (await TitlesAskedAsync("Camp Camp Camp")).ShouldBe(["Camp Camp Camp", "Camp Camp"]);
    }

    [Fact]
    public async Task The_rungs_after_the_first_two_are_asked_without_the_author()
    {
        var provider = new RecordingProvider(_ => [], kind: WorkKind.Film);

        await Resolver(provider).AskProvidersAsync(
            new CatalogQuery { Title = "Camp 3 Wasteland Now", Creator = "Some Director", Kind = WorkKind.Film }, CancellationToken.None, CancellationToken.None);

        provider.Queries.Select(q => (q.Title, q.Creator)).ShouldBe(
        [
            ("Camp 3 Wasteland Now", "Some Director"), ("Camp 3 Wasteland Now", null), ("Camp 3 Wasteland", null), ("Camp 3", null),
            ("Camp III Wasteland Now", null), ("3 Wasteland Now", null), ("III Wasteland Now", null), ("Wasteland Now", null),
        ]);
    }
}
