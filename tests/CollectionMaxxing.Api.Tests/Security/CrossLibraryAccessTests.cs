using System.Net;
using System.Text;
using CollectionMaxxing.Api.Tests.Catalog;
using CollectionMaxxing.Api.Tests.Support;
using CollectionMaxxing.Domain.Libraries;
using CollectionMaxxing.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace CollectionMaxxing.Api.Tests.Security;

// LOCKED acceptance tests of QA-1a (issue #166), object-level authorization (OWASP API1). Do not edit, weaken, skip or delete: add new tests instead.
public class CrossLibraryAccessTests(ApiFixture api) : CatalogTest(api)
{
    /// <summary>Library A is the victim's; the attacker is a member (every role) of library B and nothing else.</summary>
    private sealed record World(
        TestLibrary A,
        TestLibrary B,
        Guid ItemId,
        Guid DeletedItemId,
        Guid TagId,
        Guid LocationId,
        Guid InviteId,
        Guid SessionId,
        Guid CandidateId,
        Guid AttackerSessionId,
        Guid PrivateAssetId,
        Guid PrivateReleaseId);

    private async Task<World> SeedAsync()
    {
        var a = await CreateLibraryAsync();
        var b = await CreateLibraryAsync();
        var privateRelease = (await CatalogSeed.AddAsync(Db, "Victim private release", ownerLibraryId: a.Id)).ReleaseId;
        var itemId = await TestData.CreateItemAsync(Db, a);
        var deletedId = await TestData.CreateItemAsync(Db, a);
        var tagId = await TestData.CreateTagAsync(Db, a.Id, "Victim tag");
        var locationId = await TestData.CreateLocationAsync(Db, a.Id, "Victim shelf");
        var sessionId = await TestData.CreateScanSessionAsync(Db, a, a.EditorId);
        var attackerSessionId = await TestData.CreateScanSessionAsync(Db, b, b.EditorId);
        // The file must exist: without it the store answers "not found" for every caller, and a missing library filter would go unnoticed.
        var blob = Path.Combine(CoversRoot, "ab", "victim.png");
        Directory.CreateDirectory(Path.GetDirectoryName(blob)!);
        await File.WriteAllBytesAsync(blob, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3]);
        var assetId = await CatalogSeed.AddAssetAsync(Db, "ab/victim.png", ownerLibraryId: a.Id);
        var inviteId = Guid.CreateVersion7();
        var candidateId = Guid.CreateVersion7();

        await using var context = Db.CreateContext();
        var deleted = await context.Items.IgnoreQueryFilters().SingleAsync(i => i.Id == deletedId);
        deleted.DeletedAt = DateTimeOffset.UtcNow;
        context.LibraryInvites.Add(new LibraryInvite
        {
            Id = inviteId,
            LibraryId = a.Id,
            Role = LibraryRole.Viewer,
            Name = "Victim invitee",
            TokenHash = Guid.NewGuid().ToString("N"),
            CreatedBy = a.OwnerId,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        });
        context.ScanCandidates.Add(new Domain.Scanning.ScanCandidate
        {
            Id = candidateId,
            LibraryId = a.Id,
            SessionId = sessionId,
            Seq = 1,
            Source = Domain.Scanning.CandidateSource.Barcode,
            RawCode = "0045496870010",
            Resolution = Domain.Scanning.CandidateResolution.NotFound,
            Decision = Domain.Scanning.CandidateDecision.Undecided,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();
        return new World(a, b, itemId, deletedId, tagId, locationId, inviteId, sessionId, candidateId, attackerSessionId, assetId, privateRelease);
    }

    /// <summary>Everything that belongs to library A and could be disturbed, as a string to compare before and after.</summary>
    private async Task<string> FingerprintOfAAsync(World w)
    {
        await using var context = Db.CreateContext();
        var items = await context.Items.IgnoreQueryFilters().Where(i => i.LibraryId == w.A.Id).OrderBy(i => i.Id)
            .Select(i => $"{i.Id}|{i.Version}|{i.DeletedAt != null}|{i.Notes}|{i.LocationId}|{i.Status}").ToListAsync();
        var tags = await context.Tags.IgnoreQueryFilters().Where(t => t.LibraryId == w.A.Id).Select(t => t.Id.ToString()).OrderBy(x => x).ToListAsync();
        var locations = await context.Locations.IgnoreQueryFilters().Where(l => l.LibraryId == w.A.Id).Select(l => l.Id.ToString()).OrderBy(x => x).ToListAsync();
        var invites = await context.LibraryInvites.IgnoreQueryFilters().Where(i => i.LibraryId == w.A.Id).OrderBy(i => i.Id)
            .Select(i => $"{i.Id}|{i.RevokedAt != null}|{i.AcceptedAt != null}").ToListAsync();
        var members = await context.LibraryMembers.IgnoreQueryFilters().Where(m => m.LibraryId == w.A.Id).OrderBy(m => m.UserId)
            .Select(m => $"{m.UserId}|{m.Role}").ToListAsync();
        var library = await context.Libraries.IgnoreQueryFilters().Where(l => l.Id == w.A.Id).Select(l => l.Name).SingleAsync();
        var sessions = await context.ScanSessions.IgnoreQueryFilters().Where(s => s.LibraryId == w.A.Id).OrderBy(s => s.Id)
            .Select(s => $"{s.Id}|{s.Status}").ToListAsync();
        var candidates = await context.ScanCandidates.IgnoreQueryFilters().Where(c => c.LibraryId == w.A.Id).OrderBy(c => c.Id)
            .Select(c => $"{c.Id}|{c.Decision}").ToListAsync();
        var prints = await context.ItemFeaturePrints.IgnoreQueryFilters().CountAsync(p => p.LibraryId == w.A.Id);
        return string.Join("\n", [library, .. items, .. tags, .. locations, .. invites, .. members, .. sessions, .. candidates, prints.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
    }

    private sealed record Attack(string Method, string Url, string? Body = null, bool Idempotent = false, bool IfMatch = false, bool MustBeNotFound = true)
    {
        public override string ToString() => $"{Method} {Url}";
    }

    private static string L(Guid id) => $"/api/v1/libraries/{id}";

    private static string S(Guid id) => $"/api/v1/scan-sessions/{id}";

    /// <summary>Library A's route, with every id of A: the attacker is not a member of A.</summary>
    private static List<Attack> ThroughVictimsRoute(World w)
    {
        var a = L(w.A.Id);
        return
        [
            new("GET", a), new("PATCH", a, """{ "name": "Pwned" }"""),
            new("GET", $"{a}/members"), new("PATCH", $"{a}/members/{w.A.EditorId}", """{ "role": "viewer" }"""), new("DELETE", $"{a}/members/{w.A.EditorId}"),
            new("PATCH", $"{a}/members/{w.A.OwnerId}", """{ "role": "viewer" }"""), new("DELETE", $"{a}/members/{w.A.OwnerId}"),
            new("GET", $"{a}/invites"), new("POST", $"{a}/invites", """{ "name": "Mallory", "role": "editor" }"""), new("DELETE", $"{a}/invites/{w.InviteId}"),
            new("GET", $"{a}/locations"), new("POST", $"{a}/locations", """{ "name": "Pwned" }"""),
            new("PATCH", $"{a}/locations/{w.LocationId}", """{ "name": "Pwned" }"""), new("DELETE", $"{a}/locations/{w.LocationId}"),
            new("GET", $"{a}/items"), new("POST", $"{a}/items", """{ "manual": { "title": "Planted" } }""", Idempotent: true),
            new("GET", $"{a}/items/{w.ItemId}"), new("PATCH", $"{a}/items/{w.ItemId}", """{ "notes": "Pwned" }""", IfMatch: true),
            new("DELETE", $"{a}/items/{w.ItemId}", IfMatch: true),
            new("POST", $"{a}/items/{w.DeletedItemId}:restore"), new("POST", $"{a}/items/{w.ItemId}:rematch", $$"""{ "releaseId": "{{w.PrivateReleaseId}}" }"""),
            new("GET", $"{a}/items:deleted"),
            new("POST", $"{a}/items:bulk", $$"""{ "ids": ["{{w.ItemId}}"], "action": "delete" }"""),
            new("POST", $"{a}/items:restore", $$"""{ "ids": ["{{w.DeletedItemId}}"] }"""),
            new("GET", $"{a}/stats"), new("GET", $"{a}/tags"), new("POST", $"{a}/tags", """{ "name": "Pwned" }"""), new("DELETE", $"{a}/tags/{w.TagId}"),
            new("GET", $"{a}/ownership?code=0045496870010"), new("GET", $"{a}/ownership/snapshot"),
            new("GET", $"{a}/feature-prints"), new("PUT", $"{a}/feature-prints", """{ "prints": [] }"""),
            new("POST", $"{a}/scan-sessions", "{ }"),
        ];
    }

    /// <summary>The attacker's OWN library route with A's ids: the library in the URL is theirs, the thing they name is not.</summary>
    private static List<Attack> ThroughOwnRouteWithVictimsIds(World w)
    {
        var b = L(w.B.Id);
        return
        [
            new("PATCH", $"{b}/members/{w.A.OwnerId}", """{ "role": "viewer" }"""), new("DELETE", $"{b}/members/{w.A.OwnerId}"),
            new("PATCH", $"{b}/members/{w.A.EditorId}", """{ "role": "viewer" }"""), new("DELETE", $"{b}/members/{w.A.EditorId}"),
            new("DELETE", $"{b}/invites/{w.InviteId}"),
            new("PATCH", $"{b}/locations/{w.LocationId}", """{ "name": "Pwned" }"""), new("DELETE", $"{b}/locations/{w.LocationId}"),
            new("GET", $"{b}/items/{w.ItemId}"), new("PATCH", $"{b}/items/{w.ItemId}", """{ "notes": "Pwned" }""", IfMatch: true),
            new("DELETE", $"{b}/items/{w.ItemId}", IfMatch: true),
            new("POST", $"{b}/items/{w.DeletedItemId}:restore"), new("POST", $"{b}/items/{w.ItemId}:rematch", $$"""{ "releaseId": "{{w.PrivateReleaseId}}" }"""),
            new("DELETE", $"{b}/tags/{w.TagId}"),

            // Several ids at once: the answer is a per-id result, not one 404, but nothing of A's may change.
            new("POST", $"{b}/items:bulk", $$"""{ "ids": ["{{w.ItemId}}"], "action": "delete" }""", MustBeNotFound: false),
            new("POST", $"{b}/items:restore", $$"""{ "ids": ["{{w.DeletedItemId}}"] }""", MustBeNotFound: false),

            // A's scan session and candidate, named from B's own library or session.
            new("GET", S(w.SessionId)), new("GET", $"{S(w.SessionId)}/events"), new("DELETE", S(w.SessionId)),
            new("POST", $"{S(w.SessionId)}/commit", Idempotent: true), new("POST", $"{S(w.SessionId)}/commit:undo", Idempotent: true),
            new("POST", $"{S(w.SessionId)}/barcodes", """{ "codes": [ { "value": "0045496870010" } ] }""", Idempotent: true),
            new("POST", $"{S(w.SessionId)}/identify", "{ }"),
            new("PATCH", $"{S(w.SessionId)}/candidates/{w.CandidateId}", """{ "copies": 2 }"""),
            new("POST", $"{S(w.SessionId)}/candidates/{w.CandidateId}:search", """{ "text": "x" }"""),
            new("POST", $"{S(w.SessionId)}/candidates/{w.CandidateId}:art", $$"""{ "distances": [ { "releaseId": "{{w.PrivateReleaseId}}", "distance": 0.5 } ] }"""),
            new("POST", $"{S(w.SessionId)}/candidates/{w.CandidateId}/photo"),

            // A's candidate named through the attacker's own session.
            new("PATCH", $"{S(w.AttackerSessionId)}/candidates/{w.CandidateId}", """{ "copies": 2 }"""),
            new("POST", $"{S(w.AttackerSessionId)}/candidates/{w.CandidateId}:search", """{ "text": "x" }"""),
            new("POST", $"{S(w.AttackerSessionId)}/candidates/{w.CandidateId}:art", $$"""{ "distances": [ { "releaseId": "{{w.PrivateReleaseId}}", "distance": 0.5 } ] }"""),
            new("POST", $"{S(w.AttackerSessionId)}/candidates/{w.CandidateId}/photo"),

            // Covers, photos and private releases of A. (Photos are served only as covers: there is no other route to a stored photo.)
            new("GET", $"/media/covers/{w.PrivateAssetId}"),
            new("GET", $"/api/v1/catalog/releases/{w.PrivateReleaseId}"),
            new("GET", $"/api/v1/catalog/releases/{w.PrivateReleaseId}/artwork"),
        ];
    }

    private HttpClient Mallory(World w, LibraryRole role = LibraryRole.Owner) => ClientWith(w.B.UserWithRole(role));

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, Attack attack)
    {
        using var request = new HttpRequestMessage(new HttpMethod(attack.Method), attack.Url);
        if (attack.Body is not null)
        {
            request.Content = new StringContent(attack.Body, Encoding.UTF8, "application/json");
        }
        else if (attack.Url.EndsWith("/photo", StringComparison.Ordinal))
        {
            // A real upload, so the request gets as far as asking whose candidate it is.
            var file = new ByteArrayContent(CollectionMaxxing.TestSupport.JpegFixtures.Plain());
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            request.Content = new MultipartFormDataContent { { file, "file", "photo.jpg" } };
        }

        if (attack.Idempotent)
        {
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        }

        if (attack.IfMatch)
        {
            request.Headers.Add("If-Match", "\"1\"");
        }

        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    private static readonly string[] Secrets = ["Jurassic", "Victim", "Pwned", "Planted"];

    private static async Task<List<string>> RunAsync(HttpClient client, IEnumerable<Attack> attacks)
    {
        var failures = new List<string>();
        foreach (var attack in attacks)
        {
            using var response = await SendAsync(client, attack);
            var status = (int)response.StatusCode;
            var expected = attack.MustBeNotFound ? status == 404 : status is >= 200 and < 500;
            if (!expected)
            {
                failures.Add($"{attack}: got {status}, wanted {(attack.MustBeNotFound ? "404" : "a non-5xx answer")}");
                continue;
            }

            if (response.Content.Headers.ContentType?.MediaType is "text/event-stream")
            {
                continue; // never ends; a 404 is a problem document, not a stream
            }

            var body = await response.Content.ReadAsStringAsync();
            if (Secrets.FirstOrDefault(s => body.Contains(s, StringComparison.OrdinalIgnoreCase) && !attack.Body?.Contains(s, StringComparison.OrdinalIgnoreCase) == true) is { } leaked)
            {
                failures.Add($"{attack}: the answer mentions '{leaked}'");
            }
        }

        return failures;
    }

    [Fact]
    public async Task A_member_of_another_library_gets_404_for_every_route_of_a_library_that_is_not_theirs_and_changes_nothing()
    {
        var w = await SeedAsync();
        var before = await FingerprintOfAAsync(w);

        foreach (var role in new[] { LibraryRole.Owner, LibraryRole.Editor, LibraryRole.Viewer })
        {
            using var client = Mallory(w, role);
            var failures = await RunAsync(client, ThroughVictimsRoute(w));
            failures.ShouldBeEmpty($"as a {role} of the other library");
        }

        (await FingerprintOfAAsync(w)).ShouldBe(before);
    }

    [Fact]
    public async Task A_member_of_another_library_cannot_read_or_write_an_item_scan_session_candidate_cover_or_release_of_it_by_id_through_their_own_library()
    {
        var w = await SeedAsync();
        var before = await FingerprintOfAAsync(w);

        foreach (var role in new[] { LibraryRole.Owner, LibraryRole.Editor })
        {
            using var client = Mallory(w, role);

            // An editor of the other library may not manage its members or invites at all: that is a 403 in their own library, before A is in question.
            var attacks = ThroughOwnRouteWithVictimsIds(w)
                .Where(a => role == LibraryRole.Owner || !(a.Url.Contains("/members/", StringComparison.Ordinal) || a.Url.Contains("/invites/", StringComparison.Ordinal)));
            var failures = await RunAsync(client, attacks);
            failures.ShouldBeEmpty($"as a {role} of the other library");
        }

        (await FingerprintOfAAsync(w)).ShouldBe(before);
    }

    [Fact]
    public async Task A_stranger_in_no_library_at_all_gets_404_for_the_same_routes()
    {
        var w = await SeedAsync();
        using var client = ClientWith(w.A.OutsiderId);

        (await RunAsync(client, ThroughVictimsRoute(w))).ShouldBeEmpty();
        (await RunAsync(client, ThroughOwnRouteWithVictimsIds(w).Where(a => a.Url.Contains(w.B.Id.ToString(), StringComparison.Ordinal) is false))).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_export_of_someone_else_is_404_to_poll_and_to_download()
    {
        var w = await SeedAsync();
        using var victim = ClientWith(w.A.OwnerId);
        var started = await victim.GetAsync("/api/v1/me/export");
        started.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var jobId = (await ReadJsonAsync(started)).GetProperty("jobId").GetGuid();
        using var mallory = Mallory(w);

        (await mallory.GetAsync($"/api/v1/me/export/{jobId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await mallory.GetAsync($"/api/v1/me/export/{jobId}/download")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await mallory.GetAsync($"/api/v1/me/export/{jobId}/download?token=made-up")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_device_of_someone_else_is_404_to_revoke_and_missing_from_the_list()
    {
        var w = await SeedAsync();
        using var mallory = Mallory(w);

        (await mallory.DeleteAsync($"/api/v1/me/sessions/{Guid.NewGuid()}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var listed = await ReadJsonAsync(await mallory.GetAsync("/api/v1/me/sessions"));
        listed.GetProperty("items").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task A_viewer_who_writes_gets_403_and_changes_nothing()
    {
        var w = await SeedAsync();
        var own = await TestData.CreateScanSessionAsync(Db, w.A, w.A.ViewerId); // the viewer's own scan session
        var before = await FingerprintOfAAsync(w);
        var a = L(w.A.Id);
        var writes = new List<Attack>
        {
            new("PATCH", a, """{ "name": "Pwned" }"""),
            new("PATCH", $"{a}/members/{w.A.EditorId}", """{ "role": "viewer" }"""), new("DELETE", $"{a}/members/{w.A.EditorId}"),
            new("GET", $"{a}/invites"), new("POST", $"{a}/invites", """{ "name": "Mallory", "role": "editor" }"""), new("DELETE", $"{a}/invites/{w.InviteId}"),
            new("POST", $"{a}/locations", """{ "name": "Pwned" }"""), new("PATCH", $"{a}/locations/{w.LocationId}", """{ "name": "Pwned" }"""), new("DELETE", $"{a}/locations/{w.LocationId}"),
            new("POST", $"{a}/items", """{ "manual": { "title": "Planted" } }""", Idempotent: true),
            new("PATCH", $"{a}/items/{w.ItemId}", """{ "notes": "Pwned" }""", IfMatch: true), new("DELETE", $"{a}/items/{w.ItemId}", IfMatch: true),
            new("POST", $"{a}/items/{w.DeletedItemId}:restore"), new("POST", $"{a}/items:bulk", $$"""{ "ids": ["{{w.ItemId}}"], "action": "delete" }"""),
            new("POST", $"{a}/items:restore", $$"""{ "ids": ["{{w.DeletedItemId}}"] }"""), new("GET", $"{a}/items:deleted"),
            new("POST", $"{a}/tags", """{ "name": "Pwned" }"""), new("DELETE", $"{a}/tags/{w.TagId}"),
            new("PUT", $"{a}/feature-prints", """{ "prints": [] }"""),
        };
        using var viewer = ClientWith(w.A.ViewerId);

        foreach (var write in writes)
        {
            using var response = await SendAsync(viewer, write);
            ((int)response.StatusCode).ShouldBe(403, write.ToString());
        }

        // The viewer's own scan session is theirs to use, but committing it is an editor's job.
        foreach (var write in new[] { new Attack("POST", $"{S(own)}/commit", Idempotent: true), new Attack("DELETE", S(own)), new Attack("POST", $"{S(own)}/commit:undo", Idempotent: true) })
        {
            using var response = await SendAsync(viewer, write);
            ((int)response.StatusCode).ShouldBe(403, write.ToString());
        }

        (await FingerprintOfAAsync(w)).ShouldBe(before);
    }
}
