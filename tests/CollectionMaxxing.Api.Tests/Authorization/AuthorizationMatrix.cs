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

        // Sign-in, setup, recovery and joining by invite: the ceremonies that create the signed-in caller.
        "/setup",
        "/account/login",
        "/account/recover",
        "/account/passkey/creation-options",
        "/account/passkey/request-options",
        "/account/passkey/register",
        "/join/{token}",

        // The join page asks what the invite is for (rate-limited; the same 404 for every token that isn't usable).
        "/api/v1/invites/{token}",

        // OpenIddict's token endpoints (the iPhone redeems its pairing here).
        "/connect/token",
        "/connect/revoke",
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
    private const int Created = 201;
    private const int Accepted = 202;
    private const int NoContent = 204;
    private const int Unauthorized = 401;
    private const int Forbidden = 403;
    private const int NotFound = 404;
    private const int Stub = 501;
    private const int BadRequest = 400;
    private const int PreconditionFailed = 412;

    private const string Json = "{}";

    private static readonly IReadOnlyDictionary<string, string> Idempotent = new Dictionary<string, string> { ["Idempotency-Key"] = "test-key-1" };
    private static readonly IReadOnlyDictionary<string, string> IfMatch = new Dictionary<string, string> { ["If-Match"] = "\"1\"" };

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
        SignedIn("DELETE", "/api/v1/me", allowed: Unauthorized), // LIB-2c: only ever the caller's own account, and only with a passkey assertion made for the request (none here), so 401 reauth_required for every role
        SignedIn("GET", "/api/v1/me/sessions", allowed: Ok),
        SignedIn("DELETE", "/api/v1/me/sessions/{id:guid}", allowed: NotFound), // the seeded id is no device of theirs
        SignedIn("POST", "/api/v1/me/devices/pair", allowed: Created) with { Admin = Unauthorized }, // the matrix admin has no row in users, so has no account to pair
        SignedIn("GET", "/api/v1/me/export", allowed: Accepted), // LIB-2b: starts a job for the caller's own libraries; any signed-in person may
        SignedIn("GET", "/api/v1/me/export/{id:guid}", allowed: NotFound), // the seeded id is no export of theirs
        SignedIn("GET", "/api/v1/me/export/{id:guid}/download", allowed: NotFound),

        // ---- Sign-in, setup, recovery, tokens (ID): the only anonymous endpoints ---------------------------
        // OpenIddict now fronts the token endpoint and rejects a request that is not a form-encoded grant (400); a well-formed grant
        // reaches the stub (501), which Auth/TokenEndpointTests checks. Revocation is added with the grants (ID-2).
        Everyone("POST", "/connect/token", Json, BadRequest),
        Everyone("POST", "/connect/revoke", Json, BadRequest),

        // Built (ID-1). The world always has an owner, so setup is closed: 404 for everyone.
        Everyone("GET", "/setup", null, NotFound),
        Everyone("POST", "/setup", Json, NotFound),
        Everyone("GET", "/account/login", null, Ok),
        Everyone("POST", "/account/login", Json, BadRequest),
        SignedIn("POST", "/account/logout", allowed: NoContent),

        // Starting a passkey ceremony with no code or token is refused for a stranger and allowed for a signed-in user
        // (who just signed in, so the recent-passkey rule is met); the test admin has no row in the database.
        new EndpointCase("POST", "/account/passkey/creation-options", Json, BadRequest, Ok, Ok, Ok, Ok) { Admin = Unauthorized },
        Everyone("POST", "/account/passkey/request-options", null, Ok),
        Everyone("POST", "/account/passkey/register", Json, BadRequest),
        Everyone("POST", "/account/recover", Json, BadRequest),
        Everyone("GET", "/join/{token}", null, Ok),

        // Built (ID-3a). The matrix's token is no invite, so everyone gets the one "not found" answer.
        Everyone("GET", "/api/v1/invites/{token}", null, NotFound),

        // ---- Libraries and members (LIB), invites (ID-3a): built ----------------------------------------------
        SignedIn("GET", "/api/v1/libraries", allowed: Ok),
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
        InLibrary("GET", $"{Libraries}/stats", LibraryRole.Viewer, allowed: Ok),

        // ---- Tags (LIB): built ----------------------------------------------------------------------------
        InLibrary("GET", $"{Libraries}/tags", LibraryRole.Viewer, allowed: Ok),
        InLibrary("POST", $"{Libraries}/tags", LibraryRole.Editor, """{ "name": "New tag" }""", Created),
        InLibrary("DELETE", $"{Libraries}/tags/{{id:guid}}", LibraryRole.Editor, allowed: NoContent),

        // ---- Ownership (SCAN-1c): built ----------------------------------------------------------------------
        InLibrary("GET", $"{Libraries}/ownership", LibraryRole.Viewer, allowed: Ok, query: "code=0045496870010"),
        InLibrary("GET", $"{Libraries}/ownership/snapshot", LibraryRole.Viewer, allowed: Ok),

        // ---- Scanning (SCAN-1a): a viewer may scan, an editor commits. Addressed by session id; a session is its starter's own. ----
        // identify, :art and photo are still stubs (Wave 3).
        InLibrary("POST", $"{Libraries}/scan-sessions", LibraryRole.Viewer, Json, allowed: Created),
        InLibrary("GET", Sessions, LibraryRole.Viewer, allowed: Ok),
        InLibrary("GET", $"{Sessions}/events", LibraryRole.Viewer, allowed: Ok), // SSE: the matrix reads only the headers
        InLibrary("POST", $"{Sessions}/barcodes", LibraryRole.Viewer, "{\"codes\":[{\"value\":\"not-a-code\"}]}", allowed: Ok, headers: Idempotent),
        InLibrary("POST", $"{Sessions}/identify", LibraryRole.Viewer, Json),
        InLibrary("POST", $"{Sessions}/candidates/{{cid:guid}}:art", LibraryRole.Viewer, Json),
        InLibrary("POST", $"{Sessions}/candidates/{{cid:guid}}/photo", LibraryRole.Editor),
        InLibrary("PATCH", $"{Sessions}/candidates/{{cid:guid}}", LibraryRole.Editor, "{\"copies\":2}", allowed: Ok),
        InLibrary("POST", $"{Sessions}/candidates/{{cid:guid}}:search", LibraryRole.Editor, Json, allowed: BadRequest), // {} has no text: the handler is reached
        InLibrary("POST", $"{Sessions}/commit", LibraryRole.Editor, allowed: Ok, headers: Idempotent),
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
        InLibrary("GET", $"{Libraries}/feature-prints", LibraryRole.Viewer),
        InLibrary("PUT", $"{Libraries}/feature-prints", LibraryRole.Editor, Json),

        // ---- Admin: the platform admin role, not a library role ----------------------------------------------
        AdminOnly("POST", "/api/admin/catalog/releases/{id:guid}:verify"),
        AdminOnly("GET", "/api/admin/jobs", Ok),
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
