using System.Collections.Concurrent;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Catalog;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Application.ShelfReading;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Identifiers;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.Extensions.DependencyInjection;

namespace CollectionMaxxing.Api.Tests.Scanning.ShelfRead;

// #828: the film catalogue's alternative titles are asked for the server's own shelf reads only, one ask per read, after the read's place is
// given back. The phone's identify never asks. A fake film catalogue lists the Highlander films (public titles) and counts what it is asked.
public class ShelfReadAlternativeTitlesTests(ApiFixture api) : ShelfReadTest(api)
{
    private const string BoxTitle = "HIGHLANDER THE FINAL DIMENSION";

    private sealed class FilmCatalogue : ICatalogProvider, ISearchableCatalogProvider, IAlternativeTitleProvider
    {
        public ConcurrentQueue<string> Searched { get; } = new();

        public ConcurrentQueue<string> AlternativeAsked { get; } = new();

        /// <summary>When set, an alternative-titles call waits for it.</summary>
        public TaskCompletionSource? Hold { get; set; }

        /// <summary>When set, the films found score too low to be offered, so the read goes on to its trimmed spans.</summary>
        public bool Weak { get; set; }

        public string Name => "films";

        public bool Supports(WorkKind kind) => kind is WorkKind.Film or WorkKind.Tv;

        public bool HasAlternativeTitles(ProviderRelease release) => release.Provider == Name;

        public Task<ProviderRelease?> LookupAsync(Identifier id, CancellationToken ct) => Task.FromResult<ProviderRelease?>(null);

        public Task<IReadOnlyList<ProviderRelease>> SearchAsync(CatalogQuery query, CancellationToken ct)
        {
            Searched.Enqueue(query.Title);
            if (Weak)
            {
                return Task.FromResult<IReadOnlyList<ProviderRelease>>(
                [
                    Film("movie/1", "Highlander Chronicles Of Distant Worlds", 2000),
                    Film("movie/2", "Highlander Saga Of The Northern Isles", 1991),
                    Film("movie/3", "Highlander Tales From Another Century", 1994),
                ]);
            }

            return Task.FromResult<IReadOnlyList<ProviderRelease>>(
            [
                Film("movie/1", "Highlander: Endgame", 2000),
                Film("movie/2", "Highlander II: The Quickening", 1991),
                Film("movie/3", "Highlander III: The Sorcerer", 1994),
            ]);
        }

        public async Task<IReadOnlyList<string>?> AlternativeTitlesAsync(ProviderRelease release, CancellationToken ct)
        {
            AlternativeAsked.Enqueue(release.ExternalId);
            if (Hold is { } hold)
            {
                await hold.Task.WaitAsync(ct);
            }

            return release.ExternalId == "movie/3" ? ["Highlander: The Final Dimension"] : [];
        }

        private static ProviderRelease Film(string id, string title, int year) => new()
        {
            Provider = "films",
            ExternalId = id,
            Title = title,
            Kind = WorkKind.Film,
            Format = ReleaseFormat.Dvd,
            Year = year,
        };
    }

    private FilmCatalogue UseFilms()
    {
        var films = new FilmCatalogue();
        AddServices(services => services.AddSingleton<ICatalogProvider>(films));
        return films;
    }

    private static ShelfReadResult FilmShelf(params string[] titles) =>
        new([.. titles.Select(t => new ShelfReadItem(t, string.Empty, ShelfItemKind.Film))], ShelfReadStatus.Ok);

    private async Task<(HttpClient Editor, Guid SessionId, Guid Snap)> ArrangeAsync()
    {
        var library = await CreateLibraryAsync();
        var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(editor, library.Id);
        return (editor, sessionId, await IdentifyShelfSnapAsync(editor, sessionId));
    }

    [Fact]
    public async Task A_film_the_server_read_by_the_title_on_its_box_is_matched_to_the_film_that_lists_it()
    {
        var films = UseFilms();
        var (editor, sessionId, snap) = await ArrangeAsync();
        using var _ = editor;
        Reader.Answer = _ => FilmShelf(BoxTitle);

        var added = await CandidatesOfAsync(await ReadShelfAsync(editor, sessionId, snap));

        ReleaseTitle(added.Single(c => SourceOf(c) == "picture_server_model")).ShouldBe("Highlander III: The Sorcerer");
        Resolution(added.Single(c => SourceOf(c) == "picture_server_model")).ShouldBe("matched");
        films.AlternativeAsked.Order().ShouldBe(["movie/1", "movie/2", "movie/3"]);
    }

    [Fact]
    public async Task The_phones_identify_of_the_same_film_never_asks_for_alternative_titles()
    {
        var films = UseFilms();
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(editor, library.Id);

        var rows = await IdentifyOkAsync(editor, sessionId, Read(title: BoxTitle, kind: "film"));

        films.Searched.ShouldNotBeEmpty();
        films.AlternativeAsked.ShouldBeEmpty();
        Resolution(rows.Single()).ShouldNotBe("matched");
    }

    [Fact]
    public async Task A_read_waiting_for_a_place_is_not_held_up_by_another_reads_alternative_title_calls()
    {
        var films = UseFilms();
        films.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Options.Parallelism = 1;
        var (editor, sessionId, snap) = await ArrangeAsync();
        using var _ = editor;
        Reader.Answer = _ => FilmShelf(BoxTitle, "HIGHLANDER THE SORCERER RETURNS");

        var reading = ReadShelfAsync(editor, sessionId, snap);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (films.Searched.Select(t => t).Distinct().Count() < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        // Both reads have been searched while the first one's alternative-title calls are still held: its place was given back.
        films.Searched.ShouldContain(BoxTitle);
        films.Searched.ShouldContain("HIGHLANDER THE SORCERER RETURNS");
        films.AlternativeAsked.ShouldNotBeEmpty();
        reading.IsCompleted.ShouldBeFalse();
        films.Hold.SetResult();
        (await reading).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A_server_read_that_goes_on_to_its_trimmed_spans_asks_for_alternative_titles_once_in_all()
    {
        var films = UseFilms();
        films.Weak = true;
        var (editor, sessionId, snap) = await ArrangeAsync();
        using var _ = editor;
        Reader.Answer = _ => FilmShelf(BoxTitle);

        (await ReadShelfAsync(editor, sessionId, snap)).EnsureSuccessStatusCode();

        films.Searched.Distinct().Count().ShouldBeGreaterThan(1, "the read was asked again, trimmed");
        films.AlternativeAsked.Count.ShouldBe(3, "once for the read, not once for each time it is asked");
    }
}
