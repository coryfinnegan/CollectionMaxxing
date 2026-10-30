using System.Text.Json;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Api.Tests.Scanning.Identify;

// A shelf of films read by the Vision tier names no kinds, so every provider is asked and a same-titled book can come first.
// The rest of the snap usually says what kind of shelf it is: when most of the reads that name a kind agree (at least three, and
// 70%), a read that names none lists releases of that kind first among the ones that score level. Ordering only: the verdict, the
// scores and a read's own kind are untouched. Neutral titles throughout.
public class IdentifyKindContextTests(ApiFixture api) : IdentifyTest(api)
{
    /// <summary>A title no other test can have left in the shared catalogue: a release a snap ensured stays there, and would sort first next time.</summary>
    private string Title { get; } = $"Quiet Harbor Atlas {Guid.NewGuid():N}";

    private const int BookYear = 2001;
    private const int FilmYear = 2002;

    /// <summary>
    /// The title as a book and as a film, the book first as the providers answered them. The read below names no kind, so nothing
    /// but the order tells the two apart; their years do.
    /// </summary>
    private void ArrangeTiedBookAndFilm()
    {
        Games.Results.Add(new ProviderRelease { Provider = "igdb", ExternalId = "b1", Title = Title, Kind = WorkKind.Book, Year = BookYear });
        Games.Results.Add(new ProviderRelease { Provider = "igdb", ExternalId = "f1", Title = Title, Kind = WorkKind.Film, Year = FilmYear });
    }

    private static object[] FilmReads(int count) =>
        [.. Enumerable.Range(1, count).Select(i => Read(title: $"Lantern Row Part {i}", kind: "film", format: "vhs"))];

    private static object[] BookReads(int count) =>
        [.. Enumerable.Range(1, count).Select(i => Read(title: $"Orchard Gate Volume {i}", kind: "book"))];

    private static List<int> AlternativeYears(JsonElement candidate) =>
        [.. candidate.GetProperty("alternatives").EnumerateArray().Select(a => a.GetProperty("year").GetInt32())];

    private async Task<(HttpClient Client, Guid SessionId)> StartAsync()
    {
        var library = await CreateLibraryAsync();
        var client = As(library, LibraryRole.Editor);
        return (client, await StartSessionWithoutAutoCommitAsync(client, library.Id));
    }

    [Fact]
    public async Task A_read_with_no_kind_among_reads_of_films_lists_the_film_of_that_title_first()
    {
        ArrangeTiedBookAndFilm();
        var (client, sessionId) = await StartAsync();

        var candidates = await IdentifyOkAsync(client, sessionId, [.. FilmReads(4), Read(title: Title)]);

        var kindless = candidates[4];
        Resolution(kindless).ShouldBe("ambiguous");
        AlternativeYears(kindless).ShouldBe([FilmYear, BookYear]);
    }

    [Fact]
    public async Task The_kind_context_leaves_the_verdict_and_every_score_as_the_scorer_made_them()
    {
        ArrangeTiedBookAndFilm();
        var (client, sessionId) = await StartAsync();

        var kindless = (await IdentifyOkAsync(client, sessionId, [.. FilmReads(4), Read(title: Title)]))[4];

        Resolution(kindless).ShouldBe("ambiguous");
        kindless.GetProperty("score").GetDouble().ShouldBe(1.0);
        kindless.GetProperty("alternatives").EnumerateArray().Select(a => a.GetProperty("score").GetDouble()).ShouldBe([1.0, 1.0]);
    }

    [Fact]
    public async Task A_snap_with_no_clear_majority_leaves_the_order_alone()
    {
        ArrangeTiedBookAndFilm();
        var (client, sessionId) = await StartAsync();

        var candidates = await IdentifyOkAsync(client, sessionId, [.. FilmReads(2), .. BookReads(2), Read(title: Title)]);

        AlternativeYears(candidates[4]).ShouldBe([BookYear, FilmYear]);
    }

    [Fact]
    public async Task Two_reads_that_name_a_kind_are_not_a_shelf()
    {
        ArrangeTiedBookAndFilm();
        var (client, sessionId) = await StartAsync();

        var candidates = await IdentifyOkAsync(client, sessionId, [.. FilmReads(2), Read(title: Title)]);

        AlternativeYears(candidates[2]).ShouldBe([BookYear, FilmYear]);
    }

    [Fact]
    public async Task A_read_that_names_its_own_kind_is_not_reordered_by_the_shelf()
    {
        ArrangeTiedBookAndFilm();
        var (client, sessionId) = await StartAsync();

        var candidates = await IdentifyOkAsync(client, sessionId, [.. FilmReads(4), Read(title: Title, kind: "book")]);

        AlternativeYears(candidates[4])[0].ShouldBe(BookYear, "a lone book among films is still a book");
    }

    [Fact]
    public async Task A_read_the_job_resolves_later_sees_the_same_kind_first()
    {
        Options.ResolveBudget = TimeSpan.FromMilliseconds(300);
        ArrangeTiedBookAndFilm();
        var slow = true;
        Games.Behaviour = async ct =>
        {
            if (slow)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
        };
        var (client, sessionId) = await StartAsync();
        var pending = await IdentifyOkAsync(client, sessionId, [.. FilmReads(4), Read(title: Title)]);
        pending.ShouldAllBe(c => Resolution(c) == "pending");

        slow = false;
        for (var i = 0; i < pending.Count; i++)
        {
            await RunNextJobAsync();
        }

        var resolved = (await GetSessionAsync(client, sessionId)).GetProperty("candidates");
        Resolution(resolved[4]).ShouldBe("ambiguous");
        AlternativeYears(resolved[4]).ShouldBe([FilmYear, BookYear]);
    }
}
