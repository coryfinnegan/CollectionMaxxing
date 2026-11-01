using System.Net;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Items;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Application.Catalog.Providers;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;

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

    /// <summary>The kind the stored read ended up with, or null when it has none (the read's own kind was taken off it, or never named).</summary>
    private static string? InferredKind(JsonElement candidate) =>
        candidate.GetProperty("inferred").TryGetProperty("kind", out var kind) ? kind.GetString() : null;

    private async Task<(HttpClient Client, Guid SessionId)> StartAsync()
    {
        var library = await CreateLibraryAsync();
        var client = As(library, LibraryRole.Editor);
        return (client, await StartSessionWithoutAutoCommitAsync(client, library.Id));
    }

    /// <summary>Posts the items with the kind the shelf's own print said, and returns the candidates of the 200 response, in order.</summary>
    private static async Task<List<JsonElement>> IdentifyPrintedAsync(HttpClient client, Guid sessionId, string printKind, params object[] items)
    {
        var body = Body(Guid.NewGuid(), items);
        body["printKind"] = printKind;
        var response = await client.PostAsync(IdentifyUrl(sessionId), Json(body));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().ToList();
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

    // ---- the shelf's own print ---------------------------------------------------------------------------------------

    [Fact]
    public async Task The_kind_the_shelf_printed_lists_that_kind_first_though_no_read_names_one()
    {
        ArrangeTiedBookAndFilm();
        var (client, sessionId) = await StartAsync();

        var candidates = await IdentifyPrintedAsync(client, sessionId, "film", Read(title: "Lantern Row Part 1"), Read(title: Title));

        Resolution(candidates[1]).ShouldBe("ambiguous");
        AlternativeYears(candidates[1]).ShouldBe([FilmYear, BookYear]);
    }

    [Fact]
    public async Task The_kind_the_shelf_printed_wins_over_the_kinds_the_reads_name()
    {
        ArrangeTiedBookAndFilm();
        var (client, sessionId) = await StartAsync();

        var candidates = await IdentifyPrintedAsync(client, sessionId, "film", [.. BookReads(4), Read(title: Title)]);

        AlternativeYears(candidates[4]).ShouldBe([FilmYear, BookYear], "the picture model called every tape on one such shelf a book");
    }

    [Fact]
    public async Task A_read_the_print_denies_the_kind_of_is_ranked_by_the_print_instead()
    {
        // On a shelf the scanner lends the model's kind to the Vision read of the same spine before it sends the snap, so the read
        // arrives calling a tape a book. The print says film, which is better evidence of what the shelf holds than that guess.
        ArrangeTiedBookAndFilm();
        var (client, sessionId) = await StartAsync();

        var candidates = await IdentifyPrintedAsync(client, sessionId, "film", [.. BookReads(4), Read(title: Title, kind: "book")]);

        AlternativeYears(candidates[4]).ShouldBe([FilmYear, BookYear]);
        Resolution(candidates[4]).ShouldBe("ambiguous");
    }

    [Fact]
    public async Task A_print_that_one_read_agrees_with_takes_no_kind_off_the_other_reads()
    {
        // One film read among four books: the engines can tell a film from a book on this shelf, so the print of film has nothing to add.
        // Every book read keeps its kind, and a title-only read goes by the kind most of the shelf's reads name.
        ArrangeTiedBookAndFilm();
        var (client, sessionId) = await StartAsync();

        var candidates = await IdentifyPrintedAsync(
            client, sessionId, "film", [.. BookReads(4), Read(title: "Lantern Row Part 1", kind: "film", format: "vhs"), Read(title: Title)]);

        candidates.Take(4).Select(InferredKind).ShouldAllBe(kind => kind == "book");
        InferredKind(candidates[4]).ShouldBe("film");
        AlternativeYears(candidates[5]).ShouldBe([BookYear, FilmYear]);
    }

    [Fact]
    public async Task A_read_the_print_agrees_with_keeps_its_own_kind()
    {
        ArrangeTiedBookAndFilm();
        var (client, sessionId) = await StartAsync();

        var candidates = await IdentifyPrintedAsync(client, sessionId, "book", [.. BookReads(4), Read(title: Title, kind: "book")]);

        AlternativeYears(candidates[4])[0].ShouldBe(BookYear);
    }

    [Fact]
    public async Task A_read_a_product_code_identified_keeps_its_own_kind_under_a_print_that_denies_it()
    {
        // The code names Clay Fighter, a game, whatever the shelf's print says; a film of the same title must not be able to tie with it.
        Games.Results.Add(ScanData.Game("Clay Fighter", "Super Nintendo Entertainment System", 1993, "g-clay-fighter"));
        Games.Results.Add(new ProviderRelease { Provider = "igdb", ExternalId = "f-clay-fighter", Title = "Clay Fighter", Kind = WorkKind.Film, Year = FilmYear });
        var (client, sessionId) = await StartAsync();
        var coded = Read(title: "Wrangler's 2", platform: "SUPER NINTENDO");
        coded["productCode"] = "SNS-8C-USA";

        var candidate = (await IdentifyPrintedAsync(client, sessionId, "film", coded)).Single();

        InferredKind(candidate).ShouldBe("game", "the kind came from the serial table, not from a guess");
        Resolution(candidate).ShouldBe("matched");
        candidate.GetProperty("release").GetProperty("year").GetInt32().ShouldBe(1993);
    }

    [Fact]
    public async Task The_print_is_ignored_on_a_cover_shot_whose_read_keeps_its_own_kind()
    {
        ArrangeTiedBookAndFilm();
        var (client, sessionId) = await StartAsync();
        var body = Body(Guid.NewGuid(), [Read(title: Title, kind: "book")]);
        body["kind"] = "cover";
        body["shotKind"] = "single_cover";
        body["printKind"] = "film";

        var response = await client.PostAsync(IdentifyUrl(sessionId), Json(body));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var candidate = (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().Single();

        InferredKind(candidate).ShouldBe("book", "a print kind belongs to a shelf, and one item's own kind already carries its print");
        candidate.GetProperty("inferred").TryGetProperty("snapKind", out _).ShouldBeFalse();
        AlternativeYears(candidate)[0].ShouldBe(BookYear);
    }

    [Fact]
    public async Task The_print_is_ignored_on_an_edition_proof_whose_read_keeps_its_own_kind()
    {
        var library = await CreateLibraryAsync();
        var bookId = await ItemSeed.AddReleaseAsync(Db, Title, WorkKind.Book, ReleaseFormat.Paperback, BookYear);
        await ItemSeed.AddReleaseAsync(Db, Title, WorkKind.Film, ReleaseFormat.Dvd, FilmYear);
        var itemId = await TestData.CreateItemAsync(Db, library, bookId);
        await ItemSeed.SetEditionUnverifiedAsync(Db, itemId);
        var client = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionWithoutAutoCommitAsync(client, library.Id);
        var body = Body(Guid.NewGuid(), [Read(title: Title, kind: "book", format: "paperback", year: BookYear)]);
        body["kind"] = "edition_proof";
        body["shotKind"] = "single_cover";
        body["itemId"] = itemId;
        body["printKind"] = "film";

        var response = await client.PostAsync(IdentifyUrl(sessionId), Json(body));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var candidate = (await ReadJsonAsync(response)).GetProperty("candidates").EnumerateArray().Single();

        InferredKind(candidate).ShouldBe("book");
        candidate.GetProperty("inferred").TryGetProperty("snapKind", out _).ShouldBeFalse();
        Resolution(candidate).ShouldBe("matched");
        candidate.GetProperty("release").GetProperty("id").GetGuid().ShouldBe(bookId);
    }

    [Fact]
    public async Task A_read_the_job_resolves_later_sees_the_kind_the_shelf_printed()
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
        var pending = await IdentifyPrintedAsync(client, sessionId, "film", Read(title: Title));
        pending.ShouldAllBe(c => Resolution(c) == "pending");

        slow = false;
        await RunNextJobAsync();

        var resolved = (await GetSessionAsync(client, sessionId)).GetProperty("candidates");
        Resolution(resolved[0]).ShouldBe("ambiguous");
        AlternativeYears(resolved[0]).ShouldBe([FilmYear, BookYear]);
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
