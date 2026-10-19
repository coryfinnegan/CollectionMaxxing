# Endpoint authorization audit

Made by QA-1a (issue #166), October 2026. It answers one question for every endpoint of the API: who may call it, what happens to everyone else, and is that proven by a test that runs on every pull request.

**Result.** Every endpoint is deny-by-default. Every one declares how it is protected, has a row in the authorization matrix, and is checked against what the matrix expects of each kind of caller. No endpoint was found that answered a caller it should have refused. The audit did find four weaknesses in the token and sign-in code (all fixed, with tests) and one in the web session (fixed in #177). See [Findings](#findings).

## How this stays true

This file is checked by the build. `tests/CollectionMaxxing.Api.Tests/Security/EndpointAuditDocTests.cs` fails when an endpoint is missing from the table below, or the table lists one that does not exist. The rest of the audit is code:

| Test | What it proves |
|---|---|
| `Authorization/EndpointPolicyTests` | Every endpoint says how it is protected; only the allow-list is anonymous; every library, scan-session and admin route carries its own declaration. |
| `Authorization/AuthorizationMatrixTests` | One row per endpoint, six kinds of caller, the real pipeline, the exact status each gets. |
| `Security/MatrixCompletenessTests` (locked) | The matrix and the routing table agree both ways, and every row agrees with what its endpoint declares (a non-member is 404, a role below the minimum is 403, anonymous is 401). The audit logic is tried on made-up endpoints with one fault each, so it cannot pass by seeing nothing. |
| `Security/MatrixMutationTests` | The hand-mutation proof, done on the real routing table: take one declaration away from one real endpoint at a time (library role, scan-session role, admin policy, authorization, or make it anonymous) and the audit names that endpoint. |

To add an endpoint: add its row to `AuthorizationMatrix.Cases`, then add its row to the table below. Both tests tell you which is missing.

## The layers (docs/07), and where each is proven

| Layer | Rule | Proof |
|---|---|---|
| 1. Authentication | Cookie (web) or bearer token (phone), otherwise 401 | `Security/BearerTokenAbuseTests`, `Auth/*` |
| 2. Fallback policy | Authenticated user required unless `AllowAnonymous` and on the allow-list | `EndpointPolicyTests`, `MatrixCompletenessTests` |
| 3. Scope | A bearer token must carry the `api` scope and the API audience | `BearerTokenAbuseTests` (fixed by QA-1a, see F2 and F3) |
| 4. Route group | `/api/admin/**` needs the platform admin role | `Security/AdminAccessTests` |
| 5. Resource | Library membership and minimum role: not a member 404, role too low 403 | `Security/CrossLibraryAccessTests` (locked), `AuthorizationMatrixTests` |
| 6. Data filter | EF Core global query filter on `library_id` | `Security/CrossLibraryAccessTests`: the attacker names the victim's ids through their own library and gets 404 |

## Endpoints

"IDOR test: Y" means a caller who belongs to another library, or to none, is tried against the endpoint with the victim's real ids and gets 404 (`Security/CrossLibraryAccessTests`, plus `Scanning/ScanSessionIsolationTests`, `Pairing/DeviceSessionTests`, `Exports/ExportTests`, `Catalog/CoverEndpointTests`). "n/a" says why there is no object to attack.

| Method | Route | Policy | Who may call | Matrix row | IDOR test | DTO leaks internals (API3) |
|---|---|---|---|---|---|---|
| GET | `/account/login` | anonymous (allow-listed) | everyone | Y | n/a (no object id; acts on the caller) | passkey options, or SignedInDto (user id, display name, library id) |
| POST | `/account/login` | anonymous (allow-listed) | everyone | Y | n/a (no object id; acts on the caller) | passkey options, or SignedInDto (user id, display name, library id) |
| POST | `/account/logout` | signed in | any signed-in caller (own data only) | Y | n/a (no object id; acts on the caller) | none (204, no body) |
| POST | `/account/passkey/creation-options` | anonymous (allow-listed) | everyone | Y | n/a (no object id; acts on the caller) | passkey options, or SignedInDto (user id, display name, library id) |
| POST | `/account/passkey/register` | anonymous (allow-listed) | everyone | Y | n/a (no object id; acts on the caller) | passkey options, or SignedInDto (user id, display name, library id) |
| POST | `/account/passkey/request-options` | anonymous (allow-listed) | everyone | Y | n/a (no object id; acts on the caller) | passkey options, or SignedInDto (user id, display name, library id) |
| POST | `/account/recover` | anonymous (allow-listed) | everyone | Y | n/a (no object id; acts on the caller) | passkey options, or SignedInDto (user id, display name, library id) |
| POST | `/api/admin/catalog/releases/{id:guid}:verify` | admin policy | platform admin only | Y | n/a (admin only; no per-user object) | stub (501) |
| GET | `/api/admin/evals/golden` | admin policy | platform admin only | Y | n/a (admin only; no per-user object) | eval report only; admin only, off by default |
| GET | `/api/admin/evals/golden/{id}/image` | admin policy | platform admin only | Y | n/a (admin only; no per-user object) | eval report only; admin only, off by default |
| POST | `/api/admin/evals/runs` | admin policy | platform admin only | Y | n/a (admin only; no per-user object) | eval report only; admin only, off by default |
| GET | `/api/admin/evals/runs/latest` | admin policy | platform admin only | Y | n/a (admin only; no per-user object) | eval report only; admin only, off by default |
| GET | `/api/admin/jobs` | admin policy | platform admin only | Y | n/a (admin only; no per-user object) | AdminJobDto: `LastError` is raw job error text; admin only (accepted) |
| GET | `/api/v1/catalog/lookup` | signed in | any signed-in caller (own data only) | Y | n/a (no object id; acts on the caller) | ReleaseDto / WorkDto: catalogue fields, no provider payloads. Unsaved provider results carry the provider's own cover URL (see accepted risks) |
| GET | `/api/v1/catalog/platforms` | signed in | any signed-in caller (own data only) | Y | n/a (no object id; acts on the caller) | PlatformDto: reference data |
| GET | `/api/v1/catalog/releases/{id:guid}` | signed in | any signed-in caller (own data only) | Y | Y (private releases) | ReleaseDto / WorkDto: catalogue fields, no provider payloads. Unsaved provider results carry the provider's own cover URL (see accepted risks) |
| GET | `/api/v1/catalog/releases/{id:guid}/artwork` | signed in | any signed-in caller (own data only) | Y | Y (private releases) | ReleaseDto / WorkDto: catalogue fields, no provider payloads. Unsaved provider results carry the provider's own cover URL (see accepted risks) |
| GET | `/api/v1/catalog/search` | signed in | any signed-in caller (own data only) | Y | n/a (no object id; acts on the caller) | ReleaseDto / WorkDto: catalogue fields, no provider payloads. Unsaved provider results carry the provider's own cover URL (see accepted risks) |
| GET | `/api/v1/invites/{token}` | anonymous (allow-listed) | everyone | Y | n/a (anonymous; one answer for every unusable token) | InvitePreviewDto: owner name, library name, role, expiry. Same 404 for every unusable token |
| GET | `/api/v1/libraries` | signed in | any signed-in caller (own data only) | Y | n/a (lists only the caller's own libraries) | LibrarySummaryDto: id, name, the caller's own role |
| GET | `/api/v1/libraries/{libraryId:guid}` | library role | viewer and above | Y | Y | library data of the caller's own library |
| PATCH | `/api/v1/libraries/{libraryId:guid}` | library role | owner and above | Y | Y | library data of the caller's own library |
| GET | `/api/v1/libraries/{libraryId:guid}/feature-prints` | library role | viewer and above | Y | Y | FeaturePrintDto: opaque bytes of the library's own items |
| PUT | `/api/v1/libraries/{libraryId:guid}/feature-prints` | library role | editor and above | Y | Y | FeaturePrintDto: opaque bytes of the library's own items |
| GET | `/api/v1/libraries/{libraryId:guid}/invites` | library role | owner and above | Y | Y | InviteDto: no token or hash (only the creation response has the link, once) |
| POST | `/api/v1/libraries/{libraryId:guid}/invites` | library role | owner and above | Y | Y | InviteDto: no token or hash (only the creation response has the link, once) |
| DELETE | `/api/v1/libraries/{libraryId:guid}/invites/{id:guid}` | library role | owner and above | Y | Y | none (204, no body) |
| GET | `/api/v1/libraries/{libraryId:guid}/items` | library role | viewer and above | Y | Y | ItemDto / ItemSummaryDto: library data and `createdBy` (a user id inside the same library) |
| POST | `/api/v1/libraries/{libraryId:guid}/items` | library role | editor and above | Y | Y | ItemDto / ItemSummaryDto: library data and `createdBy` (a user id inside the same library) |
| DELETE | `/api/v1/libraries/{libraryId:guid}/items/{id:guid}` | library role | editor and above | Y | Y | none (204, no body) |
| GET | `/api/v1/libraries/{libraryId:guid}/items/{id:guid}` | library role | viewer and above | Y | Y | ItemDto / ItemSummaryDto: library data and `createdBy` (a user id inside the same library) |
| PATCH | `/api/v1/libraries/{libraryId:guid}/items/{id:guid}` | library role | editor and above | Y | Y | ItemDto / ItemSummaryDto: library data and `createdBy` (a user id inside the same library) |
| POST | `/api/v1/libraries/{libraryId:guid}/items/{id:guid}:rematch` | library role | editor and above | Y | Y | ItemDto / ItemSummaryDto: library data and `createdBy` (a user id inside the same library) |
| POST | `/api/v1/libraries/{libraryId:guid}/items/{id:guid}:restore` | library role | editor and above | Y | Y | ItemDto / ItemSummaryDto: library data and `createdBy` (a user id inside the same library) |
| POST | `/api/v1/libraries/{libraryId:guid}/items:bulk` | library role | editor and above | Y | Y | ItemDto / ItemSummaryDto: library data and `createdBy` (a user id inside the same library) |
| GET | `/api/v1/libraries/{libraryId:guid}/items:deleted` | library role | editor and above | Y | Y | ItemDto / ItemSummaryDto: library data and `createdBy` (a user id inside the same library) |
| POST | `/api/v1/libraries/{libraryId:guid}/items:restore` | library role | editor and above | Y | Y | ItemDto / ItemSummaryDto: library data and `createdBy` (a user id inside the same library) |
| GET | `/api/v1/libraries/{libraryId:guid}/locations` | library role | viewer and above | Y | Y | library data of the caller's own library |
| POST | `/api/v1/libraries/{libraryId:guid}/locations` | library role | editor and above | Y | Y | library data of the caller's own library |
| DELETE | `/api/v1/libraries/{libraryId:guid}/locations/{id:guid}` | library role | editor and above | Y | Y | none (204, no body) |
| PATCH | `/api/v1/libraries/{libraryId:guid}/locations/{id:guid}` | library role | editor and above | Y | Y | library data of the caller's own library |
| GET | `/api/v1/libraries/{libraryId:guid}/members` | library role | viewer and above | Y | Y | MemberDto: id, display name, role, joined. Devices and last-seen for the owner only |
| DELETE | `/api/v1/libraries/{libraryId:guid}/members/{userId:guid}` | library role | owner and above | Y | Y | none (204, no body) |
| PATCH | `/api/v1/libraries/{libraryId:guid}/members/{userId:guid}` | library role | owner and above | Y | Y | MemberDto: id, display name, role, joined. Devices and last-seen for the owner only |
| GET | `/api/v1/libraries/{libraryId:guid}/ownership` | library role | viewer and above | Y | Y | OwnershipDto: titles, formats, locations of the library's own copies |
| GET | `/api/v1/libraries/{libraryId:guid}/ownership/snapshot` | library role | viewer and above | Y | Y | OwnershipDto: titles, formats, locations of the library's own copies |
| POST | `/api/v1/libraries/{libraryId:guid}/scan-sessions` | library role | viewer and above | Y | Y | CandidateDto / ScanSessionDto: library data, no provider payloads |
| GET | `/api/v1/libraries/{libraryId:guid}/stats` | library role | viewer and above | Y | Y | ItemDto / ItemSummaryDto: library data and `createdBy` (a user id inside the same library) |
| GET | `/api/v1/libraries/{libraryId:guid}/tags` | library role | viewer and above | Y | Y | library data of the caller's own library |
| POST | `/api/v1/libraries/{libraryId:guid}/tags` | library role | editor and above | Y | Y | library data of the caller's own library |
| DELETE | `/api/v1/libraries/{libraryId:guid}/tags/{id:guid}` | library role | editor and above | Y | Y | none (204, no body) |
| DELETE | `/api/v1/me` | signed in | any signed-in caller (own data only) | Y | n/a (no object id; acts on the caller) | none (204, no body) |
| GET | `/api/v1/me` | signed in | any signed-in caller (own data only) | Y | n/a (no object id; acts on the caller) | MeResponse: id and memberships. No email, username or stamps |
| PATCH | `/api/v1/me` | signed in | any signed-in caller (own data only) | Y | n/a (no object id; acts on the caller) | MeResponse: id and memberships. No email, username or stamps |
| POST | `/api/v1/me/devices/pair` | signed in | any signed-in caller (own data only) | Y | n/a (no object id; acts on the caller) | PairDeviceDto: token and code, returned once to their owner |
| GET | `/api/v1/me/export` | signed in | any signed-in caller (own data only) | Y | n/a (no object id; acts on the caller) | ExportDto, or the caller's own zip |
| GET | `/api/v1/me/export/{id:guid}` | signed in | any signed-in caller (own data only) | Y | Y | ExportDto, or the caller's own zip |
| GET | `/api/v1/me/export/{id:guid}/download` | signed in | any signed-in caller (own data only) | Y | Y | ExportDto, or the caller's own zip |
| GET | `/api/v1/me/sessions` | signed in | any signed-in caller (own data only) | Y | n/a (no object id; acts on the caller) | SessionDto: name, times, status. No token or hash |
| DELETE | `/api/v1/me/sessions/{id:guid}` | signed in | any signed-in caller (own data only) | Y | Y | none (204, no body) |
| GET | `/api/v1/me/web-sessions` | signed in | any signed-in caller (own data only) | Y | n/a (no object id; acts on the caller) | SessionDto: label, times, status. No cookie or session secret |
| DELETE | `/api/v1/me/web-sessions/{id:guid}` | signed in | any signed-in caller (own data only) | Y | Y | none (204, no body) |
| DELETE | `/api/v1/me/web-sessions` | signed in | any signed-in caller (own data only) | Y | n/a (no object id; acts on the caller) | none (204, no body) |
| GET | `/api/v1/recognition/config` | signed in | any signed-in caller (own data only) | Y | n/a (no object id; acts on the caller) | prompt and thresholds (no secrets) |
| DELETE | `/api/v1/scan-sessions/{id:guid}` | scan-session role | editor and above (in the session's library) | Y | Y | none (204, no body) |
| GET | `/api/v1/scan-sessions/{id:guid}` | scan-session role | viewer and above (in the session's library) | Y | Y | CandidateDto / ScanSessionDto: library data, no provider payloads |
| POST | `/api/v1/scan-sessions/{id:guid}/barcodes` | scan-session role | viewer and above (in the session's library) | Y | Y | CandidateDto / ScanSessionDto: library data, no provider payloads |
| PATCH | `/api/v1/scan-sessions/{id:guid}/candidates/{cid:guid}` | scan-session role | editor and above (in the session's library) | Y | Y | CandidateDto / ScanSessionDto: library data, no provider payloads |
| POST | `/api/v1/scan-sessions/{id:guid}/candidates/{cid:guid}/photo` | scan-session role | editor and above (in the session's library) | Y | Y | CandidateDto / ScanSessionDto: library data, no provider payloads |
| POST | `/api/v1/scan-sessions/{id:guid}/candidates/{cid:guid}:art` | scan-session role | viewer and above (in the session's library) | Y | Y | CandidateDto / ScanSessionDto: library data, no provider payloads |
| POST | `/api/v1/scan-sessions/{id:guid}/candidates/{cid:guid}:search` | scan-session role | editor and above (in the session's library) | Y | Y | CandidateDto / ScanSessionDto: library data, no provider payloads |
| POST | `/api/v1/scan-sessions/{id:guid}/commit` | scan-session role | editor and above (in the session's library) | Y | Y | CandidateDto / ScanSessionDto: library data, no provider payloads |
| POST | `/api/v1/scan-sessions/{id:guid}/commit:undo` | scan-session role | editor and above (in the session's library) | Y | Y | CandidateDto / ScanSessionDto: library data, no provider payloads |
| GET | `/api/v1/scan-sessions/{id:guid}/events` | scan-session role | viewer and above (in the session's library) | Y | Y | CandidateDto / ScanSessionDto: library data, no provider payloads |
| POST | `/api/v1/scan-sessions/{id:guid}/identify` | scan-session role | viewer and above (in the session's library) | Y | Y | CandidateDto / ScanSessionDto: library data, no provider payloads |
| POST | `/connect/revoke` | anonymous (allow-listed) | everyone | Y | n/a (no object id; acts on the caller) | OAuth token response (the caller's own tokens) |
| POST | `/connect/token` | anonymous (allow-listed) | everyone | Y | n/a (no object id; acts on the caller) | OAuth token response (the caller's own tokens) |
| GET | `/health/live` | anonymous (platform plumbing) | everyone | N (NotInMatrix) | n/a (no data) | none (no data) |
| GET | `/health/ready` | anonymous (platform plumbing) | everyone | N (NotInMatrix) | n/a (no data) | none (no data) |
| GET | `/join/{token}` | anonymous (allow-listed) | everyone | Y | n/a (no object id; acts on the caller) | passkey options, or SignedInDto (user id, display name, library id) |
| GET | `/media/covers/{assetId:guid}` | signed in | any signed-in caller (own data only) | Y | Y | image bytes, nosniff, private caching for library covers |
| GET | `/api/v1/app/ipa` | signed in | any signed-in caller (own data only) | Y | n/a (no object id; the path is fixed, never from the request) | the .ipa bytes, attachment, no-store |
| HEAD | `/api/v1/app/ipa` | signed in | any signed-in caller (own data only) | Y | n/a (no object id; the path is fixed, never from the request) | size and date headers only, no body |
| GET | `/api/v1/app/ipa/info` | signed in | any signed-in caller (own data only) | Y | n/a (no object id; the path is fixed, never from the request) | IpaInfoDto (size, date, SHA-256), no-store |
| GET | `/openapi/{documentName}.json` | anonymous (platform plumbing) | everyone | N (NotInMatrix) | n/a (no data) | none (no data) |
| GET | `/setup` | anonymous (allow-listed) | everyone | Y | n/a (no object id; acts on the caller) | passkey options, or SignedInDto (user id, display name, library id) |
| POST | `/setup` | anonymous (allow-listed) | everyone | Y | n/a (no object id; acts on the caller) | passkey options, or SignedInDto (user id, display name, library id) |

## Endpoints that are not in the routing table

Two anonymous endpoints are served by OpenIddict's middleware, not by endpoint routing, so the audit above cannot list them. Both are public on purpose, and `Security/TokenServerSurfaceTests` pins what they may say.

| Path | Why anonymous | What is pinned |
|---|---|---|
| `GET /.well-known/openid-configuration` | A token client reads it | Advertises only the token and revocation endpoints and the two grants (`urn:cm:device_pairing`, `refresh_token`); no authorization, userinfo, device or introspection endpoint |
| `GET /.well-known/jwks` | A client verifies signatures with it | Public signing key only (`use: sig`, no `d`, `p`, `q`, `dp`, `dq`, `qi`); the encryption key is never published |

The OpenAPI document (`/openapi/{documentName}.json`) is anonymous and exists only in Development; `Security/ProductionErrorTests` shows that in Production it, Scalar and Swagger answer 404 (401 when not signed in) and never 200.

## What a hostile tailnet member cannot do, test by test

| Brief item | Test |
|---|---|
| Matrix completeness, both ways | `MatrixCompletenessTests`, `MatrixMutationTests` |
| IDOR: item, scan session, candidate, cover, private release, feature prints, export, trash entry, invite, device; viewer writes get 403; a library id, `createdBy`, `role` or `ownerLibraryId` in a body never overrides the route | `CrossLibraryAccessTests`, `BodyCannotOverrideRouteTests` |
| Token abuse (expired, wrong audience, wrong issuer, no scope, `alg: none`, another key, HS256 with the public key, unknown token id, tampered, truncated, revoked device, removed member), against one route from every group | `BearerTokenAbuseTests` |
| Refresh: a replayed token revokes the chain; two concurrent refreshes cannot both win; refresh after sign-out, device revoke and member removal fails | `RefreshTokenAbuseTests` (plus the locked `Pairing/RefreshRotationTests`) |
| Authorization-code flow: `state`, `nonce`, PKCE, `redirect_uri` variants, no open redirect after sign-in | There is no such flow: `TokenServerSurfaceTests` pins that `/connect/authorize` and the other OpenID Connect endpoints do not exist, that no other grant is accepted whatever it carries, and that no sign-in step redirects. The passkey ceremony is the server's `state`/`nonce`/PKCE; `PasskeyCeremonyAbuseTests` tampers with, swaps, ages and replays it |
| Pairing, setup and invites | `HouseholdAbuseTests`, `PasskeyCeremonyAbuseTests`, and the existing `Pairing/PairingLockoutTests` (6-digit brute force locks the guesser), `RateLimiting/TrustedProxyRateLimitTests` (a forged `X-Forwarded-For` is ignored), `Pairing/PairingRedemptionTests` (expiry on `FakeTimeProvider`), `Auth/SetupTests` (setup is 404 once an owner exists), `Invites/InviteRulesTests` (an invite works once) |
| CSRF on every write route group; bearer writes unaffected | `CsrfEveryWriteRouteTests` (every write route read from the routing table) |
| Admin: only a platform admin gets in | `AdminAccessTests` |
| No redirects from media, cover, photo or catalogue-image routes | `NoRedirectTests` (locked) |
| Errors: problem+json, no stack trace, exception type or SQL in Production | `ProductionErrorTests` |

## Findings

| # | Severity | What | Status |
|---|---|---|---|
| F1 | Medium | **Two refreshes at the same moment with one token both won.** OpenIddict ignores a failed redemption on a refresh grant ("that may be caused by concurrent requests"), so six parallel requests each got a live token pair. A thief and the phone racing would each hold a chain, and a later replay of an old token would not tell them apart. | Fixed. The token endpoint now spends the refresh token itself, atomically, before issuing; a loser is a replay and revokes the device's chain. `Auth/Pairing/TokenEndpoint.cs`, test `RefreshTokenAbuseTests.Two_refreshes_at_the_same_moment...` |
| F2 | Low | **The token audience was not checked.** A token signed by the server but made for another audience was accepted. | Fixed. Tokens are issued for `collectionmaxxing-api` and validation requires it. `OpenIddictSetup.cs`, `TokenEndpoint.cs` |
| F3 | Low | **The `api` scope was not enforced** although docs/07 layer 3 says it is. | Fixed. A bearer call without the scope is 401. `CurrentUserMiddleware.cs`, `AuthSetup.IsBearerCall` |
| F4 | Low | **A form with a NUL character (`%00`) in it made `/connect/token` and `/connect/revoke` answer 500** (an unhandled exception from ASP.NET's form reader) and log a stack trace. | Fixed. 400 problem document. `Problems/MalformedFormExceptionHandler.cs` |
| F5 | Low to medium | **A copied web session cookie keeps working after sign-out** until the 8-hour idle or 30-day absolute limit. The cookie is a sealed ticket with nothing on the server to revoke. Phones are not affected. | Fixed: [#177](https://github.com/coryfinnegan/CollectionMaxxing/issues/177). The cookie names a `web_sessions` row, checked on every request; sign-out, "sign out everywhere", removing a member and deleting an account revoke it (`Security/WebSessionTests`) |
| F6 | Info | The audit's routing-table read cannot see the two OpenIddict discovery endpoints. | Documented above and pinned by tests |

## Accepted risks

1. **Redirects from media (the phone cannot refuse one).** The phone downloads artwork with `expo-file-system`'s `downloadFileAsync`, which cannot refuse a redirect. So the server must never send one, and the phone must only fetch from its own server. Both halves are defended: `NoRedirectTests` proves no `/media`, cover, or catalogue route answers a redirect (or sets `Location`) for any input, signed in or not, including assets whose stored path is a URL or a path out of the folder; and the phone's path allow-list (`ownArtworkUrl`) refuses any address that is not `/media/covers/{guid}` on its own server. Accepted because a redirect-free server plus the allow-list means a single mistake on one side is caught by the other. The 304 answer to a conditional GET is not a redirect and is allowed.
2. **Unsaved provider results carry the provider's own cover URL.** `CatalogProjection` fills `coverUrl` of a catalogue search result that has not been saved yet with the provider's address. The phone's `ownArtworkUrl` allow-list is what keeps it from fetching that; saved releases always use `/media/covers/{assetId}`. SSRF and fetching policy are QA-1b's.
3. **A token minted with the server's own keys, with no database entry, is accepted.** Such a token (no `oi_tkn_id`, no `oi_au_id`) passes validation and cannot be revoked. An attacker needs the PFX private keys, which is already the end of the game (docs/07 "Keys and secrets"; the files are readable only by the owner's account). The claimed `exp` is ignored in favour of the database entry, so a forger cannot extend a real token.
4. **A phone's token carries no platform role.** The owner's phone cannot reach `/api/admin/**` (403); only the owner's web session can. Intended; `AdminAccessTests` pins it.
5. **`AdminJobDto.LastError`** is the raw text of a job's last error. Admin only.

## Not done here

- The issuer in tokens is taken from the request's host (`"iss": "http://localhost/"` in tests). Behind Tailscale Serve that is the Tailscale name. Setting `SetIssuer(PublicUrl)` would make it fixed, which suits the "server address comes from configuration, never the Host header" rule already used for pairing. Not changed, because it invalidates every issued token; listed for the lead.
- The hand-mutation of three product endpoints (delete a `RequireLibraryRole` call and watch the matrix fail) was not done on the source files; `MatrixMutationTests` does the same on the real routing table for every endpoint instead.
