using System.Net;
using CollectionMaxxing.Api.Tests.Catalog;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.TestSupport;

namespace CollectionMaxxing.Api.Tests.Security;

// LOCKED acceptance tests of QA-1a (issue #166), no redirects from media. Do not edit, weaken, skip or delete: add new tests instead.
//
// The phone downloads artwork with expo-file-system's downloadFileAsync, which cannot refuse a redirect. So the server must never
// send one from a route that serves an image, whatever it is asked, and the phone only fetches from its own server (ownArtworkUrl).
// A conditional GET answered 304 is not a redirect: it has no Location and the client asked for it.
public class NoRedirectTests(ApiFixture api) : CatalogTest(api)
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    private sealed record Probe(string Method, string Path, Dictionary<string, string>? Headers = null)
    {
        public override string ToString() => $"{Method} {Path} {(Headers is null ? string.Empty : string.Join(' ', Headers.Select(h => $"{h.Key}: {h.Value}")))}";
    }

    private void WriteBlob(string relativePath)
    {
        var full = Path.Combine(CoversRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, Png);
    }

    private static readonly string[] Hostile =
    [
        "?redirect=https://evil.test/", "?returnUrl=//evil.test", "?next=https%3A%2F%2Fevil.test", "?url=http://169.254.169.254/",
        "?redirect_uri=https://evil.test&state=x", "?continue=//evil.test", "?format=jpg", "?w=100&h=100", "?download=1",
    ];

    private static IEnumerable<Probe> ProbesFor(string path)
    {
        yield return new("GET", path);
        yield return new("HEAD", path);
        yield return new("GET", path + "/");
        yield return new("GET", path.ToUpperInvariant());
        yield return new("GET", path.Replace("/media/", "//media//", StringComparison.Ordinal));
        yield return new("GET", path + ".png");
        yield return new("GET", path + "%2F..%2F..");
        foreach (var query in Hostile)
        {
            yield return new("GET", path + query);
        }

        yield return new("GET", path, new() { ["Host"] = "evil.test" });
        yield return new("GET", path, new() { ["X-Forwarded-Host"] = "evil.test", ["X-Forwarded-Proto"] = "http" });
        yield return new("GET", path, new() { ["X-Forwarded-Proto"] = "https" });
        yield return new("GET", path, new() { ["X-Original-URL"] = "https://evil.test/", ["X-Rewrite-URL"] = "//evil.test" });
        yield return new("GET", path, new() { ["Referer"] = "https://evil.test/", ["Origin"] = "https://evil.test" });
        yield return new("GET", path, new() { ["Accept"] = "text/html" });
        yield return new("GET", path, new() { ["Range"] = "bytes=0-1" });
        yield return new("GET", path, new() { ["If-Modified-Since"] = "Wed, 21 Oct 2015 07:28:00 GMT" });
        yield return new("GET", path, new() { ["If-None-Match"] = "\"ab12cd34\"" });
        yield return new("GET", path, new() { ["If-None-Match"] = "*" });
    }

    private static async Task<List<string>> RunAsync(HttpClient client, IEnumerable<Probe> probes)
    {
        var failures = new List<string>();
        foreach (var probe in probes)
        {
            using var request = new HttpRequestMessage(new HttpMethod(probe.Method), probe.Path);
            foreach (var (name, value) in probe.Headers ?? [])
            {
                if (name == "Host")
                {
                    request.Headers.Host = value;
                }
                else
                {
                    request.Headers.TryAddWithoutValidation(name, value);
                }
            }

            using var response = await client.SendAsync(request);
            var status = (int)response.StatusCode;
            var conditional = probe.Headers?.ContainsKey("If-None-Match") == true || probe.Headers?.ContainsKey("If-Modified-Since") == true;
            var redirect = status is >= 300 and < 400 && !(status == 304 && conditional);
            if (redirect || response.Headers.Location is not null || response.Content.Headers.ContentLocation is not null)
            {
                failures.Add($"{probe}: {status} Location={response.Headers.Location}");
            }
        }

        return failures;
    }

    [Fact]
    public async Task No_cover_route_answers_a_redirect_for_any_input_signed_in_or_not()
    {
        var library = await CreateLibraryAsync();
        WriteBlob("ab/global.png");
        WriteBlob("cd/private.png");
        var global = await CatalogSeed.AddAssetAsync(Db, "ab/global.png", sha256: "ab12cd34");
        var mine = await CatalogSeed.AddAssetAsync(Db, "cd/private.png", ownerLibraryId: library.Id);
        var missingBlob = await CatalogSeed.AddAssetAsync(Db, "ab/not-there.png");

        // Assets whose stored location is a URL or a way out of the folder: the server reads files, it never forwards.
        var urlBlob = await CatalogSeed.AddAssetAsync(Db, "https://evil.test/a.png");
        var schemeless = await CatalogSeed.AddAssetAsync(Db, "//evil.test/a.png");
        var metadata = await CatalogSeed.AddAssetAsync(Db, "http://169.254.169.254/latest/meta-data");
        var traversal = await CatalogSeed.AddAssetAsync(Db, "../../etc/passwd");

        var paths = new[] { global, mine, missingBlob, urlBlob, schemeless, metadata, traversal, Guid.NewGuid() }
            .Select(id => $"/media/covers/{id}")
            .Append("/media/covers/not-a-guid")
            .Append("/media/covers/")
            .Append("/media/covers");

        var failures = new List<string>();
        foreach (var user in new Guid?[] { library.OwnerId, library.OutsiderId, null })
        {
            using var client = ClientWith(user);
            foreach (var path in paths)
            {
                failures.AddRange((await RunAsync(client, ProbesFor(path))).Select(f => $"[{(user is null ? "anonymous" : "signed in")}] {f}"));
            }
        }

        failures.ShouldBeEmpty();
    }

    [Fact]
    public async Task No_catalogue_artwork_route_answers_a_redirect_for_any_input()
    {
        var library = await CreateLibraryAsync();
        var (_, release) = await CatalogSeed.AddAsync(Db, "Some film");
        var (_, privateRelease) = await CatalogSeed.AddAsync(Db, "Private film", ownerLibraryId: library.Id);
        var paths = new[]
        {
            $"/api/v1/catalog/releases/{release}/artwork", $"/api/v1/catalog/releases/{privateRelease}/artwork", $"/api/v1/catalog/releases/{Guid.NewGuid()}/artwork",
            $"/api/v1/catalog/releases/{release}", $"/api/v1/catalog/releases/{privateRelease}",
        };

        var failures = new List<string>();
        foreach (var user in new Guid?[] { library.OwnerId, library.OutsiderId, null })
        {
            using var client = ClientWith(user);
            foreach (var path in paths)
            {
                failures.AddRange((await RunAsync(client, ProbesFor(path))).Select(f => $"[{(user is null ? "anonymous" : "signed in")}] {f}"));
            }
        }

        failures.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_stored_photo_has_no_route_that_could_redirect_because_it_is_only_ever_served_as_a_cover()
    {
        // Photos are written by POST …/candidates/{cid}/photo and read back through /media/covers/{assetId} (tested above): no GET exists
        // under the scan-session routes that serves picture bytes, so there is no second place for a redirect to come from.
        var library = await CreateLibraryAsync();
        var session = await TestData.CreateScanSessionAsync(Db, library);
        using var client = ClientWith(library.EditorId);

        foreach (var path in new[] { "photo", "photos", "cover", "image", "media" })
        {
            using var response = await client.GetAsync($"/api/v1/scan-sessions/{session}/candidates/{Guid.NewGuid()}/{path}");
            ((int)response.StatusCode).ShouldBeInRange(400, 499, path);
            response.Headers.Location.ShouldBeNull(path);
        }

        using var anonymous = ClientWith(null);
        using var refused = await anonymous.GetAsync($"/media/covers/{Guid.NewGuid()}");
        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        refused.Headers.Location.ShouldBeNull();
    }
}
