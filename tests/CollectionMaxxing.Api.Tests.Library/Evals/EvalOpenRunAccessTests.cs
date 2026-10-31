using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Api.Tests.Evals;

/// <summary>
/// The routes of the resumable run answer exactly who the other eval routes answer: only the platform administrator, and not even them (404) while
/// the evals are off. (The authorization matrix lists each route for every role too; this checks the answers with the evals on and off.)
/// </summary>
public class EvalOpenRunAccessTests(ApiFixture api) : OpenRunTest(api)
{
    private static readonly (string Name, Func<HttpClient, Task<HttpResponseMessage>> Call)[] Routes =
    [
        ("GET open run", c => c.GetAsync(OpenUrl)),
        ("POST open run", c => OpenAsync(c)),
        ("PUT photo", c => PutPhotoAsync(c, "c1", PhotoResult("c1", "apple_model", [StarFoxRead()]))),
        ("POST finish", c => FinishAsync(c)),
        ("DELETE open run", c => c.DeleteAsync(OpenUrl)),
    ];

    [Fact]
    public async Task Only_the_admin_can_reach_any_route_of_the_open_run()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        var library = await CreateLibraryAsync();
        var otherLibrary = await CreateLibraryAsync();
        using var admin = AdminClient();
        var callers = new List<(string Name, HttpClient Client, bool Allowed, HttpStatusCode Denied)>
        {
            ("anonymous", Factory.CreateClient(), false, HttpStatusCode.Unauthorized),
            ("library owner", As(library, LibraryRole.Owner), false, HttpStatusCode.Forbidden),
            ("library editor", As(library, LibraryRole.Editor), false, HttpStatusCode.Forbidden),
            ("library viewer", As(library, LibraryRole.Viewer), false, HttpStatusCode.Forbidden),
            ("another library's owner", As(otherLibrary, LibraryRole.Owner), false, HttpStatusCode.Forbidden),
            ("admin", admin, true, default),
        };

        foreach (var (callerName, client, allowed, denied) in callers)
        {
            foreach (var (routeName, call) in Routes)
            {
                using var response = await call(client);
                if (allowed)
                {
                    response.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized, $"{routeName} as {callerName}");
                    response.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden, $"{routeName} as {callerName}");
                }
                else
                {
                    response.StatusCode.ShouldBe(denied, $"{routeName} as {callerName}");
                }
            }
        }
    }

    [Fact]
    public async Task Nobody_opens_a_run_stores_a_photo_or_reads_the_run_without_being_the_admin()
    {
        Golden.AddSingle("c1", StarFoxTruth);
        var library = await CreateLibraryAsync();
        using var admin = AdminClient();
        await OpenOkAsync(admin);
        using var owner = As(library, LibraryRole.Owner);

        (await PutPhotoAsync(owner, "c1", PhotoResult("c1", "apple_model", [StarFoxRead()]))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await owner.GetAsync(OpenUrl)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await owner.DeleteAsync(OpenUrl)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        Received(await StatusAsync(admin)).ShouldBeEmpty();
    }

    [Fact]
    public async Task With_the_evals_off_every_route_of_the_open_run_is_a_404_even_for_the_admin()
    {
        Settings["Evals:Enabled"] = "false";
        using var admin = AdminClient();

        foreach (var (routeName, call) in Routes)
        {
            using var response = await call(admin);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, routeName);
        }

        Directory.Exists(OpenRunFolder).ShouldBeFalse();
    }

    [Fact]
    public async Task With_no_results_folder_a_run_cannot_be_opened_and_the_answer_says_why()
    {
        Settings["Evals:ResultsPath"] = null;
        Golden.AddSingle("c1", StarFoxTruth);
        using var admin = AdminClient();

        var response = await OpenAsync(admin);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("evals.results_path_missing");
    }
}
