using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.Scanning;
using CollectionMaxxing.Application.Scanning.Identify;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Catalog.Matching;
using CollectionMaxxing.Domain.Identifiers;
using Microsoft.Extensions.Logging.Abstractions;
using static CollectionMaxxing.Application.Tests.Scanning.Identify.TrimmedRetryHarness;

namespace CollectionMaxxing.Application.Tests.Scanning.Identify;

// #815: the server's reader read "HIGHLANDER THE FINAL DIMENSION" on a shelf of films. Nothing the whole title found scored, so the
// spine retry asked it without its first word, and "THE FINAL DIMENSION" found "The Final Destination": a film with no "Highlander"
// in it, offered as the answer. A read that is only the reader's title (no text, no lines) has no stray print at its ends, so a span's
// find is kept only when it carries the read's leading word (in its title, or as its author's name; TrimmedSpansTests has the word rules).
public class IdentifyGroundingLeadingWordTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private const string Read = "HIGHLANDER THE FINAL DIMENSION";

    /// <summary>A film provider that answers each title it is asked (case aside) with the films the test lists for it, and nothing else.</summary>
    private sealed class Films(Dictionary<string, ProviderRelease[]> byTitle) : ICatalogProvider
    {
        public List<string> Titles { get; } = [];

        public string Name => "tmdb";

        public bool Supports(WorkKind kind) => kind == WorkKind.Film;

        public Task<ProviderRelease?> LookupAsync(Identifier id, CancellationToken ct) => Task.FromResult<ProviderRelease?>(null);

        public Task<IReadOnlyList<ProviderRelease>> SearchAsync(CatalogQuery query, CancellationToken ct)
        {
            lock (Titles)
            {
                Titles.Add(query.Title);
            }

            return Task.FromResult<IReadOnlyList<ProviderRelease>>(byTitle.TryGetValue(query.Title.ToUpperInvariant(), out var found) ? found : []);
        }
    }

    private static ProviderRelease FilmRelease(string title, int id, int year) => new()
    {
        Provider = "tmdb", ExternalId = $"movie/{id}", WorkKey = $"movie/{id}", Title = title, Kind = WorkKind.Film, Year = year,
    };

    private static Films FinalFilms() => new(new()
    {
        ["THE FINAL DIMENSION"] = [FilmRelease("The Final Destination", 1, 2009), FilmRelease("The Final Countdown", 2, 1980)],
        ["FINAL DIMENSION"] = [FilmRelease("The Final Destination", 1, 2009)],
        ["THE FINAL"] = [FilmRelease("The Final", 3, 2010)],
    });

    /// <summary>The server's reader sends a title and a kind, and no legible text; a phone's read always carries its text.</summary>
    private static ReadFacts FilmRead(string? text = null) => new(Read, text, null, null, null, WorkKind.Film, null, null, null);

    private static Task<BarcodeResolution> GroundAsync(ReadFacts facts, Films films, Catalogue? catalogue = null) =>
        new IdentifyGrounding(
                new TextSearchResolver(null!, catalogue ?? new Catalogue(), [films], new ScanningOptions(), NullLogger<TextSearchResolver>.Instance),
                new FixedScoring())
            .GroundAsync(facts, ScoringProfileKind.ShelfSpine, Budget, CancellationToken.None);

    [Fact]
    public async Task A_candidate_without_the_reads_leading_franchise_word_does_not_beat_one_that_has_it()
    {
        // The catalogue holds a film with the franchise word that only a later span names: the first span's "The Final ..." films must not stop the retry.
        var franchise = Film("Highlander: The Final");

        var answer = await GroundAsync(FilmRead(), FinalFilms(), new Catalogue(franchise));

        answer.Verdict.ShouldBe(ResolutionVerdict.Ambiguous);
        answer.Options[0].LocalReleaseId.ShouldBe(franchise.Release.Id);
        answer.Options.ShouldNotContain(o => o.Provider != null && o.Provider.Title == "The Final Destination");
    }

    [Fact]
    public async Task When_no_candidate_has_the_leading_word_the_read_is_not_answered_with_a_wrong_title()
    {
        var films = FinalFilms();

        var answer = await GroundAsync(FilmRead(), films);

        answer.Verdict.ShouldBe(ResolutionVerdict.NotFound);
        answer.Options.ShouldBeEmpty();
        films.Titles.ShouldContain("THE FINAL DIMENSION"); // the span was asked; what it found was not kept
    }

    [Fact]
    public async Task A_read_the_phone_sent_keeps_the_first_spans_find_as_before()
    {
        // Control, and the rule's edge: a phone's read carries its text, and a spine's leading word may be stray print beside the
        // title, so the plain rule stands.
        var answer = await GroundAsync(FilmRead(text: Read), FinalFilms(), new Catalogue(Film("Highlander: The Final")));

        answer.Options[0].Provider!.Title.ShouldBe("The Final Destination");
    }
}
