using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;

namespace CollectionMaxxing.Api.Tests.Scanning;

// LOCKED acceptance tests of SCAN-1a (issue #61): a session belongs to its library and its user. Do not edit or weaken.
public class ScanSessionIsolationTests(ApiFixture api) : ScanTest(api)
{
    /// <summary>Every endpoint under <c>/scan-sessions/{id}</c>, as (method, path after the session url, needs an Idempotency-Key).</summary>
    public static IEnumerable<TheoryDataRow<string, string, bool>> Endpoints() =>
    [
        new("GET", "", false),
        new("GET", "?since=0", false),
        new("GET", "/events", false),
        new("POST", "/barcodes", true),
        new("POST", "/identify", false),
        new("PATCH", "/candidates/{cid}", false),
        new("POST", "/candidates/{cid}:search", false),
        new("POST", "/candidates/{cid}:art", false),
        new("POST", "/candidates/{cid}/photo", false),
        new("POST", "/commit", true),
        new("DELETE", "", false),
    ];

    private static async Task<HttpResponseMessage> CallAsync(HttpClient client, Guid sessionId, Guid candidateId, string method, string path, bool idempotent)
    {
        var url = SessionUrl(sessionId) + path.Replace("{cid}", candidateId.ToString(), StringComparison.Ordinal);
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (method is not ("GET" or "DELETE") && !path.EndsWith("/photo", StringComparison.Ordinal) && !path.EndsWith("/commit", StringComparison.Ordinal))
        {
            request.Content = Json(path == "/barcodes" ? new { codes = new[] { Code(ScanData.Upc(1)) } } : new { });
        }

        if (idempotent)
        {
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        }

        return await client.SendAsync(request);
    }

    private async Task<(TestLibrary Library, Guid SessionId, Guid CandidateId)> SeedAsync()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(2001);
        await CatalogAsync("Star Fox 64", code);
        using var editor = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(editor, library.Id);
        var candidates = await ScanAsync(editor, sessionId, code);
        return (library, sessionId, Id(candidates[0]));
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task A_session_of_another_library_is_404_on_every_scan_endpoint(string method, string path, bool idempotent)
    {
        var (_, sessionId, candidateId) = await SeedAsync();
        var otherLibrary = await CreateLibraryAsync();

        // The other library's owner has every role there; only the library boundary can say no.
        using var stranger = As(otherLibrary, LibraryRole.Owner);
        using var response = await CallAsync(stranger, sessionId, candidateId, method, path, idempotent);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"{method} {path}");
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task A_session_of_another_user_in_the_same_library_is_404_on_every_scan_endpoint(string method, string path, bool idempotent)
    {
        var (library, sessionId, candidateId) = await SeedAsync();

        // The owner outranks the editor who started the session, but the session is the editor's.
        using var owner = As(library, LibraryRole.Owner);
        using var response = await CallAsync(owner, sessionId, candidateId, method, path, idempotent);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"{method} {path}");
    }

    [Fact]
    public async Task Another_users_session_is_left_untouched_by_the_attempts_on_it()
    {
        var (library, sessionId, candidateId) = await SeedAsync();
        using var owner = As(library, LibraryRole.Owner);

        (await DecideAsync(owner, sessionId, candidateId, new { decision = "rejected" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CommitAsync(owner, sessionId)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await owner.DeleteAsync(SessionUrl(sessionId))).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var editor = As(library, LibraryRole.Editor);
        var session = await GetSessionAsync(editor, sessionId);
        session.GetProperty("status").GetString().ShouldBe("open");
        Decision(session.GetProperty("candidates")[0]).ShouldBe("accepted");
        (await CountItemsAsync(library.Id)).ShouldBe(0);
    }

    [Fact]
    public async Task A_candidate_id_from_another_session_is_404()
    {
        var (library, firstSession, candidateId) = await SeedAsync();
        using var editor = As(library, LibraryRole.Editor);
        var secondSession = await StartSessionAsync(editor, library.Id);

        (await DecideAsync(editor, secondSession, candidateId, new { decision = "rejected" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await GetSessionAsync(editor, firstSession)).GetProperty("candidates")[0].GetProperty("decision").GetString().ShouldBe("accepted");
    }

    [Fact]
    public async Task A_member_cannot_start_a_session_in_a_library_they_do_not_belong_to()
    {
        var library = await CreateLibraryAsync();
        var otherLibrary = await CreateLibraryAsync();
        using var stranger = As(otherLibrary, LibraryRole.Owner);

        (await stranger.PostAsync(SessionsUrl(library.Id), Json(new { }))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
