using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace CollectionMaxxing.Api.Tests.Scanning.Identify;

/// <summary>
/// The app's real book adapters, pointed at a WireMock server. A read asked while the book providers are down is left Pending,
/// so the job asks again, instead of being called unknown; the same read with the providers up and answering nothing is not
/// found. The adapters give up quietly (they never throw), so this is the whole path: adapter, search, verdict.
/// </summary>
public class IdentifyOutageTests : IdentifyTest
{
    private const string OpenLibrarySearch = "/search.json";
    private const string GoogleBooksSearch = "/books/v1/volumes";

    private readonly WireMockServer _books = LoopbackWireMock.Start();

    public IdentifyOutageTests(ApiFixture api)
        : base(api)
    {
        UseRealProviders = true;
        foreach (var provider in new[] { "UpcItemDb", "Tmdb", "Igdb" })
        {
            Settings[$"Providers:{provider}:BaseUrl"] = "http://127.0.0.1:9/"; // never asked for a book; never the real one
        }

        foreach (var provider in new[] { "OpenLibrary", "GoogleBooks" })
        {
            Settings[$"Providers:{provider}:BaseUrl"] = _books.Url + "/";
            Settings[$"Providers:{provider}:RequestsPerSecond"] = "1000000000";
        }

        Settings["Providers:GoogleBooks:ApiKey"] = "google-test-key";
        Settings["Providers:OpenLibrary:ContactEmail"] = "owner@example.test";
    }

    /// <summary>A title no earlier run can have left in the shared provider cache. Two words: a ladder of one rung.</summary>
    private static string FreshTitle() => $"Lanterns {Guid.NewGuid():N}";

    private void Answer(string path, int status, string? body = null)
    {
        var response = Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json");
        _books.Given(Request.Create().WithPath(path).UsingGet()).RespondWith(body is null ? response : response.WithBody(body));
    }

    private int Requests(string path) => _books.LogEntries.Count(e => e.RequestMessage?.Path == path);

    [Fact]
    public async Task A_read_asked_while_the_book_providers_are_down_is_left_pending_for_the_job()
    {
        Answer(OpenLibrarySearch, 503);
        Answer(GoogleBooksSearch, 503);
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var candidates = await IdentifyOkAsync(editor, sessionId, Read(title: FreshTitle(), kind: "book"));

        Resolution(candidates.ShouldHaveSingleItem()).ShouldBe("pending");
        Requests(OpenLibrarySearch).ShouldBe(1); // given up on at once: no general search, no plainer rung
        Requests(GoogleBooksSearch).ShouldBe(1);
    }

    [Fact]
    public async Task The_same_read_with_the_providers_up_and_answering_nothing_is_not_found()
    {
        Answer(OpenLibrarySearch, 200, """{"numFound":0,"start":0,"docs":[]}""");
        Answer(GoogleBooksSearch, 200, """{"kind":"books#volumes","totalItems":0}""");
        var library = await CreateLibraryAsync();
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);

        var candidates = await IdentifyOkAsync(editor, sessionId, Read(title: FreshTitle(), kind: "book"));

        Resolution(candidates.ShouldHaveSingleItem()).ShouldBe("not_found");
    }

    public override async ValueTask DisposeAsync()
    {
        _books.Stop();
        _books.Dispose();
        GC.SuppressFinalize(this);
        await base.DisposeAsync();
    }
}
