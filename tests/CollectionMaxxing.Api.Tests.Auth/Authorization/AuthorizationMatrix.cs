using CollectionMaxxing.Domain.Libraries;

namespace CollectionMaxxing.Api.Tests.Authorization;

/// <summary>The kinds of caller the matrix tries against every endpoint.</summary>
public enum Actor
{
    /// <summary>Not signed in.</summary>
    Anonymous,

    /// <summary>Signed in, but a member of some other library only.</summary>
    OtherUser,

    /// <summary>A viewer of the library under test.</summary>
    Viewer,

    /// <summary>An editor of the library under test.</summary>
    Editor,

    /// <summary>The owner of the library under test.</summary>
    Owner,

    /// <summary>A signed-in platform administrator who belongs to no library: the admin role is not a library role.</summary>
    Admin,

    /// <summary>Signed in with a brand-new account that belongs to no library yet (docs/15, the Welcome screen).</summary>
    NoLibraries,
}

/// <summary>
/// One endpoint and the HTTP status each kind of caller must get. <c>Route</c> is the route template exactly as
/// the endpoint declares it; route values are filled in with seeded ids (see <see cref="AuthorizationMatrixTests"/>
/// for how to seed more). <c>Query</c> and <c>Headers</c> are what a valid call needs besides its body.
/// While an endpoint is a stub, "allowed" means 501; when a stream builds it, change that column to its real status.
/// </summary>
public sealed record EndpointCase(
    string Method,
    string Route,
    string? Body,
    int Anonymous,
    int OtherUser,
    int Viewer,
    int Editor,
    int Owner)
{
    /// <summary>What a platform admin gets. Defaults to what any other non-member gets.</summary>
    public int? Admin { get; init; }

    /// <summary>What a signed-in account with no libraries gets. Defaults to what any other non-member gets.</summary>
    public int? NoLibraries { get; init; }

    public string? Query { get; init; }

    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    public string Name => $"{Method} {Route}";

    public int ExpectedFor(Actor actor) => actor switch
    {
        Actor.Anonymous => Anonymous,
        Actor.OtherUser => OtherUser,
        Actor.Viewer => Viewer,
        Actor.Editor => Editor,
        Actor.Owner => Owner,
        Actor.Admin => Admin ?? OtherUser,
        Actor.NoLibraries => NoLibraries ?? OtherUser,
        _ => throw new ArgumentOutOfRangeException(nameof(actor)),
    };
}

/// <summary>
/// THE authorization matrix (docs/07, test 2). Every endpoint of the API has a row here; a test fails when an
/// endpoint is missing. To cover a new endpoint, add one row: no other test code changes.
/// </summary>
public static class AuthorizationMatrix
{
    /// <summary>
    /// Endpoints that may be called without signing in (docs/07, "Proving it: tests"). Anything else must require a
    /// signed-in caller. Each of these is a row below whose Anonymous column is not 401.
    /// </summary>
    public static readonly IReadOnlyList<string> AnonymousAllowList =
    [
        "/health/live",
        "/health/ready",
        "/openapi/{documentName}.json",

        // Sign-in (Auth0), recovery, sign-out and the invite page (AUTH-1 #228, ONB-1 #249). The Auth0 callback, /signin-oidc, is the
        // OpenID Connect handler's and not an endpoint.
        "/account/login",
        "/account/logout",
        "/account/recover",
        "/join/{token}",

        // The join page asks what the invite is for (rate-limited; the same 404 for every token that isn't usable).
        "/api/v1/invites/{token}",
    ];

    /// <summary>Platform plumbing that isn't part of the API contract, so it has no matrix row.</summary>
    public static readonly IReadOnlyList<string> NotInMatrix =
    [
        "/health/live",
        "/health/ready",
        "/openapi/{documentName}.json",
    ];

    private const string Libraries = "/api/v1/libraries/{libraryId:guid}";
    private const string Sessions = "/api/v1/scan-sessions/{id:guid}";

    private const int Ok = 200;
    private const int Found = 302;
    private const int Created = 201;
    private const int Accepted = 202;
    private const int NoContent = 204;
    private const int Unauthorized = 401;
    private const int Forbidden = 403;
    private const int NotFound = 404;
    private const int Stub = 501;
    private const int BadRequest = 400;
    private const int Conflict = 409;
    private const int PreconditionFailed = 412;
    private const int UnsupportedMediaType = 415;

    private const string Json = "{}";

    private static readonly IReadOnlyDictionary<string, string> Idempotent = new Dictionary<string, string> { ["Idempotency-Key"] = "test-key-1" };
    private static readonly IReadOnlyDictionary<string, string> IfMatch = new Dictionary<string, string> { ["If-Match"] = "\"1\"" };
    private static readonly IReadOnlyDictionary<string, string> Csrf = new Dictionary<string, string> { ["X-CSRF"] = "1" };
    private static readonly IReadOnlyDictionary<string, string> InstallId = new Dictionary<string, string> { ["X-Install-Id"] = "5d1f7c52-8d0e-4a35-9d4c-3a3a8c1f2b10" };

    /// <summary>A session whose last sign-in was long ago (see <c>TestAuthHandler.AuthTimeHeader</c>).</summary>
    private static readonly IReadOnlyDictionary<string, string> StaleSignIn = new Dictionary<string, string> { ["X-Test-Auth-Time"] = "0" };

    /// <summary>Anonymous: 401. Any signed-in caller: <paramref name="allowed"/>.</summary>
    private static EndpointCase SignedIn(string method, string route, string? body = null, int allowed = Stub, string? query = null) =>
        new(method, route, body, Unauthorized, allowed, allowed, allowed, allowed) { Admin = allowed, Query = query };

    /// <summary>Anyone at all, anonymous included, gets <paramref name="allowed"/>.</summary>
    private static EndpointCase Anyone(string method, string route, string? body = null) =>
        new(method, route, body, Stub, Stub, Stub, Stub, Stub) { Admin = Stub };

    /// <summary>Anyone at all, anonymous included, gets exactly <paramref name="status"/>.</summary>
    private static EndpointCase Everyone(string method, string route, string? body, int status) =>
        new(method, route, body, status, status, status, status, status) { Admin = status };

    /// <summary>Library endpoint: not a member 404, role below <paramref name="minimum"/> 403, otherwise <paramref name="allowed"/>.</summary>
    private static EndpointCase InLibrary(
        string method,
        string route,
        LibraryRole minimum,
        string? body = null,
        int allowed = Stub,
        string? query = null,
        IReadOnlyDictionary<string, string>? headers = null) =>
        new(
            method,
            route,
            body,
            Unauthorized,
            NotFound,
            minimum <= LibraryRole.Viewer ? allowed : Forbidden,
            minimum <= LibraryRole.Editor ? allowed : Forbidden,
            allowed)
        { Query = query, Headers = headers };

    /// <summary>Platform admin only: any other signed-in caller, library owners included, gets 403.</summary>
    private static EndpointCase AdminOnly(string method, string route, int allowed = Stub) =>
        new(method, route, null, Unauthorized, Forbidden, Forbidden, Forbidden, Forbidden) { Admin = allowed };

    public static readonly IReadOnlyList<EndpointCase> Cases =
    [
        // ---- Account and session (ID) ----------------------------------------------------------------------
        new EndpointCase("GET", "/api/v1/me", null, Unauthorized, Ok, Ok, Ok, Ok) { Admin = Ok },
        SignedIn("PATCH", "/api/v1/me", Json),
        SignedIn("DELETE", "/api/v1/me", allowed: Unauthorized) with { Headers = StaleSignIn }, // LIB-2c, AUTH-1: only the caller's own account, and only with a sign-in from the last 5 minutes (none here), so 401 reauth_required for every role
        SignedIn("GET", "/api/v1/me/sign-in-methods", allowed: Ok), // AUTH-1: the caller's own linked identities
        SignedIn("DELETE", "/api/v1/me/sign-in-methods/{id:guid}", allowed: NotFound), // the seeded id is no sign-in method of theirs
        SignedIn("GET", "/api/v1/me/web-sessions", allowed: Ok), // the caller's own signed-in browsers
        SignedIn("DELETE", "/api/v1/me/web-sessions/{id:guid}", allowed: NotFound), // the seeded id is no browser of theirs
        SignedIn("DELETE", "/api/v1/me/web-sessions", allowed: NoContent), // signs the caller's own browsers out; for every role, nobody else's
        SignedIn("POST", "/api/v1/me/app-sign-ins", """{ "deviceName": "Test iPhone" }""", allowed: Created) with { Headers = InstallId, Admin = Conflict }, // AUTH-2: registers the caller's own phone, for every role (the matrix admin has no user row to hang it on, so the insert is refused 409; real users always have one)
        SignedIn("GET", "/api/v1/me/app-sign-ins", allowed: Ok), // the caller's own phones
        SignedIn("DELETE", "/api/v1/me/app-sign-ins", allowed: NoContent), // signs out the caller's own phones, nobody else's
        SignedIn("GET", "/api/v1/me/export", allowed: Accepted), // LIB-2b: starts a job for the caller's own libraries; any signed-in person may
        SignedIn("GET", "/api/v1/me/export/{id:guid}", allowed: NotFound), // the seeded id is no export of theirs
        SignedIn("GET", "/api/v1/me/export/{id:guid}/download", allowed: NotFound),

        // ---- Sign-in, recovery, sign-out (AUTH-1, Auth0): the only anonymous endpoints. No setup routes (ONB-1). ---------
        Everyone("GET", "/account/login", null, Found), // off to Auth0, signed in or not
        Everyone("POST", "/account/logout", null, Ok) with { Headers = Csrf }, // works without a session too, never without X-CSRF: 1 (contract addendum 2)
        Everyone("POST", "/account/recover", Json, BadRequest), // {} has no code
        Everyone("GET", "/join/{token}", null, Ok),

        // Built (ID-3a). The matrix's token is no invite, so everyone gets the one "not found" answer.
        Everyone("GET", "/api/v1/invites/{token}", null, NotFound),

        // ONB-1: joining while signed in, with a link, a token or a code. The matrix's code is no invite: the one "not found" answer.
        SignedIn("POST", "/api/v1/invites:preview", """{ "invite": "ABCD-EFGH-JK" }""", allowed: NotFound),
        SignedIn("POST", "/api/v1/invites:accept", """{ "invite": "ABCD-EFGH-JK" }""", allowed: NotFound),

        // ---- Libraries and members (LIB), invites (ID-3a): built ----------------------------------------------
        SignedIn("GET", "/api/v1/libraries", allowed: Ok),
        SignedIn("POST", "/api/v1/libraries", """{ "name": "Another library" }""", allowed: Created) with { Admin = Conflict }, // ONB-1: anyone signed in (the matrix admin has no user row to own it, so the insert is refused 409; real users always have one)
        new EndpointCase("DELETE", $"{Libraries}/membership", null, Unauthorized, NotFound, NoContent, NoContent, Conflict), // ONB-1: any member leaves; the owner can't (409 library.last_owner)
        InLibrary("GET", Libraries, LibraryRole.Viewer, allowed: Ok),
        InLibrary("PATCH", Libraries, LibraryRole.Owner, """{ "name": "Renamed" }""", Ok),
        InLibrary("GET", $"{Libraries}/members", LibraryRole.Viewer, allowed: Ok),
        InLibrary("PATCH", $"{Libraries}/members/{{userId:guid}}", LibraryRole.Owner, """{ "role": "viewer" }""", NotFound), // the seeded id is no member
        InLibrary("DELETE", $"{Libraries}/members/{{userId:guid}}", LibraryRole.Owner, allowed: NotFound), // the seeded id is no member
        InLibrary("GET", $"{Libraries}/invites", LibraryRole.Owner, allowed: Ok),
        InLibrary("POST", $"{Libraries}/invites", LibraryRole.Owner, """{ "name": "Sam", "role": "viewer" }""", Created),
        InLibrary("DELETE", $"{Libraries}/invites/{{id:guid}}", LibraryRole.Owner, allowed: NotFound), // the seeded id is no invite

        // ---- Locations (LIB): built ------------------------------------------------------------------------
        InLibrary("GET", $"{Libraries}/locations", LibraryRole.Viewer, allowed: Ok),
        InLibrary("POST", $"{Libraries}/locations", LibraryRole.Editor, """{ "name": "New shelf" }""", Created),
        InLibrary("PATCH", $"{Libraries}/locations/{{id:guid}}", LibraryRole.Editor, """{ "name": "Renamed" }""", Ok),
        InLibrary("DELETE", $"{Libraries}/locations/{{id:guid}}", LibraryRole.Editor, allowed: NoContent),

        // ---- Items, stats (LIB): built. A stale If-Match (412) and an empty body (400) prove the handler was reached. ----
        InLibrary("GET", $"{Libraries}/items", LibraryRole.Viewer, allowed: Ok),
        InLibrary("POST", $"{Libraries}/items", LibraryRole.Editor, """{ "manual": { "title": "Seeded" } }""", Created, headers: Idempotent),
        InLibrary("GET", $"{Libraries}/items/{{id:guid}}", LibraryRole.Viewer, allowed: Ok),
        InLibrary("PATCH", $"{Libraries}/items/{{id:guid}}", LibraryRole.Editor, """{ "notes": "x" }""", PreconditionFailed, headers: IfMatch),
        InLibrary("DELETE", $"{Libraries}/items/{{id:guid}}", LibraryRole.Editor, allowed: PreconditionFailed, headers: IfMatch),
        InLibrary("POST", $"{Libraries}/items/{{id:guid}}:restore", LibraryRole.Editor, allowed: Ok),
        InLibrary("POST", $"{Libraries}/items/{{id:guid}}:rematch", LibraryRole.Editor, Json, BadRequest),
        InLibrary("GET", $"{Libraries}/items:deleted", LibraryRole.Editor, allowed: Ok),
        InLibrary("POST", $"{Libraries}/items:bulk", LibraryRole.Editor, Json, BadRequest),
        InLibrary("POST", $"{Libraries}/items:restore", LibraryRole.Editor, Json, BadRequest), // SCAN-1d: {} has no ids, so the handler is reached
        InLibrary("GET", $"{Libraries}/stats", LibraryRole.Viewer, allowed: Ok),

        // ---- Provisionals (#345): kept rows outlive their session, so the lifecycle is library-scoped ----
        InLibrary("GET", $"{Libraries}/provisionals", LibraryRole.Viewer, allowed: Ok),
        InLibrary("POST", $"{Libraries}/provisionals/{{id:guid}}:promote", LibraryRole.Editor, Json, allowed: NotFound, headers: Idempotent), // the seeded id is no provisional
        InLibrary("POST", $"{Libraries}/provisionals/{{id:guid}}:resolve", LibraryRole.Editor, Json, allowed: BadRequest), // {} has no itemId: the handler is reached
        InLibrary("DELETE", $"{Libraries}/provisionals/{{id:guid}}", LibraryRole.Editor, allowed: NotFound), // the seeded id is no provisional

        // ---- Tags (LIB): built ----------------------------------------------------------------------------
        InLibrary("GET", $"{Libraries}/tags", LibraryRole.Viewer, allowed: Ok),
        InLibrary("POST", $"{Libraries}/tags", LibraryRole.Editor, """{ "name": "New tag" }""", Created),
        InLibrary("DELETE", $"{Libraries}/tags/{{id:guid}}", LibraryRole.Editor, allowed: NoContent),

        // ---- Ownership (SCAN-1c): built ----------------------------------------------------------------------
        InLibrary("GET", $"{Libraries}/ownership", LibraryRole.Viewer, allowed: Ok, query: "code=0045496870010"),
        InLibrary("GET", $"{Libraries}/ownership/snapshot", LibraryRole.Viewer, allowed: Ok),

        // ---- Scanning (SCAN-1a): a viewer may scan, an editor commits. Addressed by session id; a session is its starter's own. ----
        // identify (SCAN-2a) and :art (SCAN-2b) are built: {} carries no snapId / distances, so the handler is reached and says 400. The photo upload (SCAN-2c) is built: no multipart body is sent here, so the handler answers 415.
        InLibrary("POST", $"{Libraries}/scan-sessions", LibraryRole.Viewer, Json, allowed: Created),
        InLibrary("GET", Sessions, LibraryRole.Viewer, allowed: Ok),
        InLibrary("GET", $"{Sessions}/events", LibraryRole.Viewer, allowed: Ok), // SSE: the matrix reads only the headers
        InLibrary("POST", $"{Sessions}/barcodes", LibraryRole.Viewer, "{\"codes\":[{\"value\":\"not-a-code\"}]}", allowed: Ok, headers: Idempotent),
        InLibrary("POST", $"{Sessions}/identify", LibraryRole.Viewer, Json, allowed: BadRequest),
        InLibrary("POST", $"{Sessions}/candidates/{{cid:guid}}:art", LibraryRole.Viewer, Json, allowed: BadRequest),
        InLibrary("POST", $"{Sessions}/candidates/{{cid:guid}}/photo", LibraryRole.Editor, allowed: UnsupportedMediaType),
        InLibrary("POST", $"{Sessions}/candidates/{{cid:guid}}/crop", LibraryRole.Editor, allowed: UnsupportedMediaType), // #345: no multipart body is sent here, so the handler answers 415
        InLibrary("POST", $"{Sessions}/candidates/{{cid:guid}}:verify-cover", LibraryRole.Viewer, allowed: UnsupportedMediaType), // #438: no multipart body is sent here, so the handler answers 415
        InLibrary("POST", $"{Sessions}/provisionals", LibraryRole.Editor, Json, allowed: BadRequest), // #345: {} has no candidateId: the handler is reached
        InLibrary("PATCH", $"{Sessions}/candidates/{{cid:guid}}", LibraryRole.Editor, "{\"copies\":2}", allowed: Ok),
        InLibrary("POST", $"{Sessions}/candidates/{{cid:guid}}:search", LibraryRole.Editor, Json, allowed: BadRequest), // {} has no text: the handler is reached
        InLibrary("POST", $"{Sessions}/commit", LibraryRole.Editor, allowed: Ok, headers: Idempotent),
        InLibrary("POST", $"{Sessions}/commit:undo", LibraryRole.Editor, allowed: Conflict, headers: Idempotent), // SCAN-1e: the seeded session was never committed, so the handler is reached and says 409
        InLibrary("POST", $"{Sessions}/auto-commit:undo", LibraryRole.Editor, allowed: Conflict, headers: Idempotent), // #341: the seeded session never auto-added, so the handler is reached and says 409
        InLibrary("DELETE", Sessions, LibraryRole.Editor, allowed: NoContent),

        // ---- Recognition config (SCAN-2d): the phone fetches its prompt and thresholds; any signed-in member, not library-scoped ----
        SignedIn("GET", "/api/v1/recognition/config", allowed: Ok),

        // ---- Catalogue (CAT): any signed-in caller; feature prints are per library ----------------------------
        SignedIn("GET", "/api/v1/catalog/search", allowed: Ok, query: "q=jurassic"),
        SignedIn("GET", "/api/v1/catalog/lookup", allowed: Ok, query: "code=0045496870010"),

        // The matrix's ids are not releases or assets, so a signed-in caller gets the "no such thing" 404; who sees which
        // real release or cover (global, own library, another library) is covered by the catalogue tests.
        SignedIn("GET", "/api/v1/catalog/releases/{id:guid}", allowed: NotFound),
        SignedIn("GET", "/api/v1/catalog/releases/{id:guid}/artwork", allowed: NotFound),
        SignedIn("GET", "/api/v1/catalog/platforms", allowed: Ok),
        SignedIn("GET", "/media/covers/{assetId:guid}", allowed: NotFound),

        InLibrary("GET", $"{Libraries}/feature-prints", LibraryRole.Viewer, allowed: Ok), // SCAN-2c: built
        InLibrary("PUT", $"{Libraries}/feature-prints", LibraryRole.Editor, Json, allowed: BadRequest), // {} has no prints: the handler is reached

        // ---- Admin: the platform admin role, not a library role ----------------------------------------------
        AdminOnly("POST", "/api/admin/catalog/releases/{id:guid}:verify"),
        AdminOnly("GET", "/api/admin/jobs", Ok),

        // EVAL-1a: the eval server. The matrix app has the evals switched off, so even the admin gets the same 404 a stranger would
        // (who may reach each route with the evals on is tested in Evals/EvalsLockedTests).
        AdminOnly("GET", "/api/admin/evals/golden", NotFound),
        AdminOnly("GET", "/api/admin/evals/golden/{id}/image", NotFound),
        AdminOnly("POST", "/api/admin/evals/runs", NotFound),
        AdminOnly("GET", "/api/admin/evals/runs/latest", NotFound),
    ];

    /// <summary>One theory row per endpoint and caller, so a failure names both.</summary>
    public static TheoryData<string, Actor> Rows
    {
        get
        {
            var rows = new TheoryData<string, Actor>();
            foreach (var endpoint in Cases)
            {
                foreach (var actor in Enum.GetValues<Actor>())
                {
                    rows.Add(endpoint.Name, actor);
                }
            }

            return rows;
        }
    }
}
