using System.Net;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Scanning;

// LOCKED acceptance test of SCAN-1a (issue #61): raw codes tied to a user and provider API keys never appear in logs or in
// resolution traces. Do not edit or weaken.
public class ScanLogHygieneTests(ApiFixture api) : ScanTest(api)
{
    private const string UpcKey = "upc-secret-key-7f3a9c";
    private const string GoogleKey = "google-secret-key-4b81d2";
    private const string TmdbKey = "tmdb-secret-key-90e6aa";
    private const string IgdbSecret = "igdb-secret-value-c35f17";

    [Fact]
    public async Task No_raw_code_and_no_provider_key_appears_in_the_logs_or_in_the_resolution_traces()
    {
        // The app's real provider adapters, configured with keys, and pointed at a local port nothing listens on: every
        // call fails the way a provider outage does, which is when the code and the key are most likely to be logged.
        UseRealProviders = true;
        Options.ResolveBudget = TimeSpan.FromSeconds(4);
        foreach (var provider in new[] { "OpenLibrary", "GoogleBooks", "UpcItemDb", "Tmdb", "Igdb" })
        {
            Settings[$"Providers:{provider}:BaseUrl"] = "http://127.0.0.1:9/";
            Settings[$"Providers:{provider}:RequestsPerSecond"] = "1000000000";
        }

        Settings["Providers:UpcItemDb:UserKey"] = UpcKey;
        Settings["Providers:GoogleBooks:ApiKey"] = GoogleKey;
        Settings["Providers:Tmdb:ApiKey"] = TmdbKey;
        Settings["Providers:Igdb:ClientId"] = "igdb-client-id";
        Settings["Providers:Igdb:ClientSecret"] = IgdbSecret;
        Settings["Providers:Igdb:TokenUrl"] = "http://127.0.0.1:9/oauth2/token";
        Settings["Providers:OpenLibrary:ContactEmail"] = "owner@example.test";
        Settings["Providers:Resilience:MaxRetryAttempts"] = "1";
        Settings["Providers:Resilience:RetryDelay"] = "00:00:00.050";
        Settings["Providers:Resilience:AttemptTimeout"] = "00:00:02";

        var library = await CreateLibraryAsync();
        var upc = ScanData.Upc(8001);
        var ean = ScanData.Gtin13(upc);
        var garbage = "zz-private-garbage-5521";
        var local = ScanData.Upc(8002);
        await CatalogAsync("Chrono Trigger", local);
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        var response = await SubmitAsync(viewer, sessionId, "hygiene", Code(upc), Code(ScanData.Isbn), Code(local), Code(garbage));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        await SubmitAsync(viewer, sessionId, "hygiene-2", Code(ean)); // the same code again, as an EAN-13

        var secrets = new[] { upc, ean, ScanData.Isbn, local, ScanData.Gtin13(local), garbage, UpcKey, GoogleKey, TmdbKey, IgdbSecret };
        Logs.Lines.Count.ShouldBeGreaterThan(0, "the capture should see the app's log lines");
        foreach (var secret in secrets)
        {
            Logs.Lines.Where(line => line.Contains(secret, StringComparison.Ordinal))
                .ShouldBeEmpty($"a log line contains '{secret[..6]}…'");
        }

        await using var context = Db.CreateContext();
        var traces = await context.ScanCandidates.IgnoreQueryFilters().Select(c => c.TraceJson).ToListAsync();
        traces.ShouldNotBeEmpty();
        foreach (var secret in secrets)
        {
            traces.Where(t => t is not null && t.Contains(secret, StringComparison.Ordinal))
                .ShouldBeEmpty($"a resolution trace contains '{secret[..6]}…'");
        }
    }

    [Fact]
    public async Task A_provider_failure_message_is_logged_without_the_code()
    {
        var library = await CreateLibraryAsync();
        var code = ScanData.Upc(8010);
        Products.Behaviour = (id, _) => throw new HttpRequestException($"Response status code does not indicate success: 500 for {id.Value}");
        using var viewer = As(library, LibraryRole.Viewer);
        var sessionId = await StartSessionAsync(viewer, library.Id);

        await ScanAsync(viewer, sessionId, code);

        Logs.Lines.Where(line => line.Contains(ScanData.Gtin13(code), StringComparison.Ordinal) || line.Contains(code, StringComparison.Ordinal))
            .ShouldBeEmpty("an exception message that carried the code must not reach the log");
    }
}
