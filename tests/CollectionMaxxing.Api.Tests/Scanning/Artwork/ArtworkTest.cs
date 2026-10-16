using System.Net;
using System.Text;
using System.Text.Json;
using CollectionMaxxing.Api.Tests.Catalog;
using CollectionMaxxing.Api.Tests.Scanning.Identify;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Catalog;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Scanning.Artwork;

/// <summary>A scan whose top releases are a close call, set up through the real <c>/identify</c> endpoint.</summary>
/// <param name="Client">The editor who started the session (so the candidate can also be decided and committed).</param>
/// <param name="Candidate">The candidate as the identify response showed it.</param>
/// <param name="Releases">The catalogue releases seeded for it, with their cover assets.</param>
public sealed record CloseCall(HttpClient Client, TestLibrary Library, Guid SessionId, JsonElement Candidate, IReadOnlyList<SeededRelease> Releases)
{
    public Guid CandidateId => Candidate.GetProperty("id").GetGuid();
}

public sealed record SeededRelease(Guid Id, Guid? CoverAssetId);

/// <summary>Base of the <c>:art</c> tests: seeds look-alike releases, scans one, and posts feature-print distances.</summary>
public abstract class ArtworkTest(ApiFixture api) : IdentifyTest(api)
{
    protected const string N64 = "Nintendo 64";

    protected Task<Guid> PlatformAsync(string name = N64, string shortName = "N64") => CatalogSeed.AddPlatformAsync(Db, name, shortName);

    /// <summary>A global release with a cover asset (or a private one when <paramref name="ownerLibraryId"/> is set).</summary>
    protected async Task<SeededRelease> ReleaseAsync(
        string title, Guid platformId, string? edition = null, bool cover = true, Guid? ownerLibraryId = null, int year = 1999)
    {
        Guid? assetId = cover
            ? await CatalogSeed.AddAssetAsync(Db, $"covers/{Guid.NewGuid():N}.png", ownerLibraryId: ownerLibraryId, sha256: Guid.NewGuid().ToString("N"))
            : null;
        var (_, releaseId) = await CatalogSeed.AddAsync(
            Db, title, WorkKind.Game, year, platformId, ownerLibraryId: ownerLibraryId, coverAssetId: assetId, format: ReleaseFormat.GameCartridge);
        if (edition is not null)
        {
            await using var context = Db.CreateContext();
            await context.Releases.IgnoreQueryFilters().Where(r => r.Id == releaseId)
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.Edition, edition));
        }

        return new SeededRelease(releaseId, assetId);
    }

    /// <summary>
    /// Scans one item whose text fits every seeded release equally well, so the verdict is a close call (Ambiguous) until the
    /// phone reports artwork distances.
    /// </summary>
    protected async Task<CloseCall> ScanCloseCallAsync(
        string title, Func<Guid, Task<IReadOnlyList<SeededRelease>>> seed, string platform = N64, int? year = 1999)
    {
        var library = await CreateLibraryAsync();
        var platformId = await PlatformAsync();
        var releases = await seed(platformId);
        var client = As(library, LibraryRole.Editor);
        var sessionId = await StartSessionAsync(client, library.Id);
        var candidates = await IdentifyOkAsync(client, sessionId, Read(title: title, kind: "game", platform: platform, year: year));
        candidates.Count.ShouldBe(1);
        return new CloseCall(client, library, sessionId, candidates[0], releases);
    }

    /// <summary>The usual near-miss pair: the original cartridge and its <em>Player's Choice</em> re-release.</summary>
    protected Task<CloseCall> ScanOriginalAndPlayersChoiceAsync(string title = "Pokemon Stadium") => ScanCloseCallAsync(
        title,
        async platformId => [await ReleaseAsync(title, platformId), await ReleaseAsync(title, platformId, edition: "Player's Choice")]);

    protected static string ArtUrl(CloseCall scan) => ArtUrl(scan.SessionId, scan.CandidateId);

    protected static string ArtUrl(Guid sessionId, Guid candidateId) => $"{SessionUrl(sessionId)}/candidates/{candidateId}:art";

    protected static object Distance(Guid releaseId, double distance) => new { releaseId, distance };

    protected static Task<HttpResponseMessage> PostArtAsync(HttpClient client, CloseCall scan, params object[] distances) =>
        client.PostAsync(ArtUrl(scan), Json(new { distances }));

    protected static Task<HttpResponseMessage> PostArtRawAsync(HttpClient client, CloseCall scan, string json) =>
        client.PostAsync(ArtUrl(scan), new StringContent(json, Encoding.UTF8, "application/json"));

    /// <summary>The alternatives of a candidate, best first.</summary>
    protected static List<JsonElement> Alternatives(JsonElement candidate) => candidate.GetProperty("alternatives").EnumerateArray().ToList();

    protected static List<Guid> AlternativeIds(JsonElement candidate) =>
        Alternatives(candidate).Select(a => a.GetProperty("releaseId").GetGuid()).ToList();

    /// <summary>The candidate as the session shows it now.</summary>
    protected static async Task<JsonElement> CandidateNowAsync(HttpClient client, Guid sessionId, Guid candidateId) =>
        (await GetSessionAsync(client, sessionId)).GetProperty("candidates").EnumerateArray()
            .Single(c => c.GetProperty("id").GetGuid() == candidateId);

    protected static async Task<string> CandidateTextNowAsync(HttpClient client, Guid sessionId, Guid candidateId) =>
        (await CandidateNowAsync(client, sessionId, candidateId)).GetRawText();

    protected static async Task<JsonElement> ReadOkAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await ReadJsonAsync(response);
    }
}
