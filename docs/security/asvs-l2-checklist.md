# OWASP ASVS 4.0.3 Level 2: walkthrough

Made by QA-1c (issue #169), October 2026, for the API and the web app. Each row is one ASVS section (V1.1, V1.2, and so on) with the requirements inside it grouped where they say the same thing. A row is **Pass** (with the test, or `file:line`, that proves it), **N/A** (with the reason) or **Gap** (with a severity). Where a row is Pass for most of its requirements and one is a Gap, the row says so. Tests are in `tests/CollectionMaxxing.Api.Tests` unless another project is named.

Context that decides many rows: the server is one PC on the owner's tailnet. Sign-in is **passkey only** (no passwords, no security questions, no SMS), the phone uses OAuth pairing tokens, TLS ends at Tailscale Serve, and the API listens on loopback. See `docs/07` and `docs/08`.

The other evidence for this file is in `docs/security/endpoint-audit.md` (QA-1a: who may call every endpoint) and the scans in `scripts/security` (QA-1c: ZAP baseline and API scan, Trivy, dependency audit; counts are in the PR).

## Summary

| | Pass | N/A | Gap |
|---|---|---|---|
| Sections (68) | 59 | 7 | 2 (V3.3 for sign-out, V14.2) |

Five of the 59 passes carry a small gap note from the list below (G4 to G7).

Gaps (all also listed under "Notes for the lead" in the PR):

| # | Severity | Where | Gap |
|---|---|---|---|
| G1 | Low to medium | V3.3, V3.7 | A copied web session cookie works until it expires, even after sign-out (QA-1a F5, issue #177). Phones are not affected. |
| G2 | Low | V14.2 | NuGet advisories (`System.Security.Cryptography.Pkcs` and `.Xml` 10.0.11, `MessagePack` 2.5.302) in test projects and the Aspire AppHost only. Nothing that ships. A pin is proposed. |
| G3 | Low | V14.2 | npm advisories (HIGH: `braces`, `node-forge`) in Expo/Metro/Jest build tooling only, not in any bundle. Accepted in `scripts/security/accepted-advisories.txt` until the next Expo SDK upgrade. |
| G4 | Low | V14.4 | `Cross-Origin-Resource-Policy` and `Cross-Origin-Embedder-Policy` are not sent (ZAP 90004). Ignored with a reason in `.zap/rules.tsv`. |
| G5 | Info | V3.2, V13.1 | The token issuer is the request host, not a fixed setting (QA-1a "Not done here"). A token minted for one host name is only valid under that name, so nothing is bypassed; setting `SetIssuer(PublicUrl)` would tidy it. |
| G6 | Info | V7.2 | The nightly ZAP API scan is mostly answered by the rate limiter (722 of 1,277 4xx answers were 429), so its active rules see little of each endpoint. Run it with higher limits in the scan stack if deeper coverage is wanted. |
| G7 | Info | V1.14, V10.2 | The app bundle is signed in CI with the owner's Apple Distribution certificate and installed only through TestFlight, which checks Apple's signature (ADR-024, docs/08). The signing secrets are limited to `main` and `v*` tags by a GitHub environment. |

## V1 Architecture, design and threat modelling

| Section | Status | Evidence |
|---|---|---|
| V1.1 Secure software development lifecycle (threat model, security in stories, tests) | Pass | `docs/08` "Threat model (STRIDE)", this file, `docs/09` (test first), `docs/13`. |
| V1.2 Authentication architecture (no default accounts, one policy) | Pass | One sign-in path per client: `Auth/SignInEndpoints.cs`, OpenID discovery pinned by `Security/TokenServerSurfaceTests`. No default account: setup code is single use, `Auth/SetupCodeSpentTests`. |
| V1.4 Access control architecture (trusted enforcement point, deny by default) | Pass | `Authorization/EndpointPolicyTests`, `Security/MatrixCompletenessTests`, `Security/MatrixMutationTests`. |
| V1.5 Input and output architecture (validate on a trusted service layer, serialization) | Pass | Request records, no entity binding: `Security/BodyCannotOverrideRouteTests`. |
| V1.6 Cryptographic architecture (key management) | Pass | Keys are PFX files outside the repo, Data Protection key ring on a volume: `KeyRing/KeyRingTests`, `Auth/TokenServerTests.The_development_signing_and_encryption_keys_are_generated_in_the_secrets_folder_not_the_repository`. |
| V1.7 Errors, logging and auditing architecture | Pass | `Security/ProductionErrorTests`, `Hardening/LogRedactionTests`, `CollectionMaxxing.Infrastructure.Tests/Audit/AuditTests`. |
| V1.8 Data protection and privacy architecture | Pass | `docs/08` "Privacy", `Deletion/*`, `Exports/ExportTests`. |
| V1.9 Communications architecture | Pass | TLS at Tailscale Serve, loopback bind, `deploy/docker-compose.yml` (API published on 127.0.0.1 only). |
| V1.10 Malicious software architecture | Pass | Locked restores, pinned actions, Dependabot, CodeQL (docs/08 "Supply chain"). |
| V1.11 Business logic architecture | Pass | `docs/08` "OWASP API Top 10", API6 controls: `Hardening/RateLimitGroupTests`. |
| V1.12 Secure file upload architecture | Pass | `Scanning/Photo/PhotoAcceptanceTests`, `PhotoUploadTests` (see V12). |
| V1.14 Configuration architecture (separation of build and deploy, secure defaults) | Pass, with G7 | `deploy/docker/Dockerfile.api` (chiseled, non-root), `ApiFoundation.cs:52` (OpenAPI only in Development). |

## V2 Authentication

| Section | Status | Evidence |
|---|---|---|
| V2.1 Password security (length, composition, breached-password checks, paste allowed) | N/A | There are no passwords. Passkeys only (`Auth/PasskeyRegistrationTests`). |
| V2.2 General authenticator security (resistant to phishing, replay, brute force) | Pass | WebAuthn is phishing-resistant by origin binding: `Auth/SignInServices.cs` `IsOurOrigin`; replay and tamper: `Security/PasskeyCeremonyAbuseTests`; counter: `Auth/PasskeyRegistrationTests`. |
| V2.3 Authenticator lifecycle (initial codes, expiry, revocation) | Pass | Setup, recovery and invite codes are single use and expire: `Auth/SetupCodeLifecycleTests`, `Auth/PasskeyRegistrationTests.An_expired_recovery_code_does_not_work`, `Invites/InviteRulesTests`. |
| V2.4 Credential storage (salted hashes) | N/A | No passwords stored. Pairing tokens, codes and invites are stored as hashes (`device_pairings.token_hash`, `code_hash`). |
| V2.5 Credential recovery | Pass | Recovery is a one-time code from the PC's command line: `Auth/TokenServerTests.The_recovery_command_prints_a_code_that_recovers_the_owner`; no security questions, no email reset. |
| V2.6 Look-up secrets | N/A | Not used. |
| V2.7 Out-of-band verifiers | N/A | No SMS or e-mail codes. The 6-digit pairing code is rate-limited per address: `Pairing/PairingLockoutTests`. |
| V2.8 One-time verifiers | Pass | Pairing and setup codes are single use, 2-minute and 24-hour lifetimes: `Pairing/PairingRedemptionTests`, `Auth/SetupCodeSpentTests`. |
| V2.9 Cryptographic verifiers (passkeys) | Pass | Attestation "none" accepted, signature verified by Identity's passkey handler: `Auth/PasskeyRegistrationTests.Sign_in_with_a_passkey_the_server_does_not_know_is_refused`. |
| V2.10 Service authentication | Pass | No service credentials in code or images; secrets come from the mounted secrets folder (`deploy/docker-compose.yml`). The repo has GitHub secret scanning and push protection. |

## V3 Session management

| Section | Status | Evidence |
|---|---|---|
| V3.1 Fundamental session management | Pass | No session id in URLs (ZAP "Session ID in URL Rewrite" passes in both scans). |
| V3.2 Session binding (new session on sign-in, unique, unguessable) | Pass, with G5 | A fresh sealed cookie per login: `Auth/SessionCookieTests.A_login_sets_the_same_strict_cookie`; tokens are database-backed: `Security/BearerTokenAbuseTests`. |
| V3.3 Session termination (idle, absolute) | Pass for idle and absolute; Gap G1 for sign-out | 8 hours idle and 30 days absolute: `Auth/SessionCookieTests.The_session_ends_after_eight_hours_without_use`, `...thirty_days...`. A copied cookie survives sign-out (F5, #177). |
| V3.4 Cookie-based session management (`Secure`, `HttpOnly`, `SameSite`, `__Host-`) | Pass | `Auth/SessionCookieTests.The_session_cookie_is_host_prefixed_http_only_secure_strict_with_no_domain_and_path_slash`. |
| V3.5 Token-based session management | Pass | Refresh rotation, reuse detection, concurrent refresh: `Security/RefreshTokenAbuseTests`, `Pairing/RefreshRotationTests`; revocation: `Security/BearerTokenAbuseTests`. |
| V3.7 Defenses against session management exploits (re-auth for sensitive actions) | Pass | Account deletion and adding a passkey need a fresh assertion: `Deletion/DeleteAccountTests`, `Auth/PasskeyRegistrationTests.Adding_a_passkey_needs_a_fresh_passkey_assertion`. Sign-out gap is G1. |

## V4 Access control

| Section | Status | Evidence |
|---|---|---|
| V4.1 General access control design (deny by default, server-side) | Pass | `Authorization/AuthorizationMatrixTests` (every endpoint, six callers), `Security/MatrixCompletenessTests`. |
| V4.2 Operation level access control (IDOR, CSRF) | Pass | `Security/CrossLibraryAccessTests` (locked), `Scanning/ScanSessionIsolationTests`, `Security/CsrfEveryWriteRouteTests`, `Auth/SessionCookieTests.Cookie_authenticated_writes_without_the_csrf_header_are_403_and_with_it_are_allowed`. |
| V4.3 Other access control (admin interfaces, directory browsing) | Pass | `Security/AdminAccessTests`; no directory browsing (ZAP passes "Directory Browsing" in both scans). |

## V5 Validation, sanitization and encoding

| Section | Status | Evidence |
|---|---|---|
| V5.1 Input validation (allow lists, limits) | Pass | `Hardening/RequestLimitTests` (batch of 50, page size clamp), validation problems on every write route. |
| V5.2 Sanitization and sandboxing | Pass | Provider text is rendered as text, never HTML; cover fetches only from allow-listed hosts: `Hardening/OutboundFetchTests`. |
| V5.3 Output encoding and injection prevention (SQL, command, template) | Pass | EF Core with parameters; the few raw statements use `ExecuteSqlInterpolatedAsync` (parameterised): `Infrastructure/Jobs/JobRunner.cs:64`, `Jobs/JobInserter.cs:32`, `Exports/ExportInfrastructure.cs:166`. No process launch. ZAP SQL/command/template injection rules pass in the API scan. |
| V5.4 Memory, string and unmanaged code | N/A | Managed code only; the native Swift module is on the phone (see MASVS checklist). |
| V5.5 Deserialization prevention | Pass | `System.Text.Json` into fixed records; no `BinaryFormatter`, type-name handling or process launch anywhere in `src` (checked by search). |

## V6 Stored cryptography

| Section | Status | Evidence |
|---|---|---|
| V6.1 Data classification | Pass | `docs/08` "What we're protecting". |
| V6.2 Algorithms (approved, no custom crypto) | Pass | RSA-2048 signing and encryption via OpenIddict, Data Protection for cookies and links, SHA-256 hashes. No custom crypto. |
| V6.3 Random values | Pass | `RandomNumberGenerator` for codes and tokens (`Application/Accounts/Devices/PairingSecrets.cs:21`, `Accounts/OneTimeCodes.cs:31`, `Invites/InviteSecrets.cs:15`). |
| V6.4 Secret management | Pass | Secrets in a folder with an owner-only ACL, mounted read at run time; `Auth/TokenServerTests` shows keys never go in the repo; the repo is scanned by GitHub push protection. |

## V7 Error handling and logging

| Section | Status | Evidence |
|---|---|---|
| V7.1 Log content (no secrets, no PII) | Pass | `Hardening/LogRedactionTests.No_secret_travelling_in_a_header_a_query_or_a_body_appears_in_any_log_line`, `Auth/LogHygieneTests`, `Scanning/ScanLogHygieneTests`. |
| V7.2 Log processing (security events logged) | Pass, with G6 | Audit events: `Infrastructure.Tests/Audit/AuditTests`; sign-in, refresh reuse, revoke are logged. G6 is about scan coverage, not logging. |
| V7.3 Log protection | Pass | Logs go to the Aspire Dashboard on loopback and to files on the owner's encrypted drive (docs/08). |
| V7.4 Error handling (generic message, no stack trace) | Pass | `Security/ProductionErrorTests`, `Hardening/SecurityHeadersTests.An_unhandled_exception_is_problem_json_with_no_detail_in_production`. The 501 stubs answer problem+json. |

## V8 Data protection

| Section | Status | Evidence |
|---|---|---|
| V8.1 General data protection (no caching of sensitive data) | Pass | `Hardening/SecurityHeadersTests.An_api_answer_is_never_cached`. |
| V8.2 Client-side data protection | Pass | Web stores nothing in local storage; the phone keeps only a refresh token (MASVS checklist). |
| V8.3 Sensitive private data (minimal, deletable, exportable) | Pass | `Deletion/DeleteAccountTests`, `Deletion/AccountPurgeTests`, `Exports/ExportTests`; photos stripped of EXIF: `Scanning/Photo/PhotoAcceptanceTests.An_uploaded_photo_has_no_exif_or_gps_after_storage_and_the_original_bytes_are_not_kept`. |

## V9 Communication

| Section | Status | Evidence |
|---|---|---|
| V9.1 Client communication security (TLS) | Pass by deployment | TLS 1.2+ is Tailscale Serve's; the API is plain HTTP on 127.0.0.1 (`deploy/docker-compose.yml`). HSTS is sent outside Development (`Security/SecurityOptions.cs:19`, `SecurityHeadersTests`). The app never speaks plain HTTP off the tailnet (ATS, see MASVS). |
| V9.2 Server communication security | Pass | Outbound fetches: HTTPS, allow-listed hosts, no private addresses, redirect limit, size and time caps: `Hardening/OutboundFetchTests`. Postgres is not published and the dashboard only on 127.0.0.1 (`deploy/docker-compose.yml`). |

## V10 Malicious code

| Section | Status | Evidence |
|---|---|---|
| V10.1 Code integrity (no hidden back doors, signed builds) | Pass, with G7 | Public repo, PR review; deploy accepts only images with a build attestation from this repo's workflow (docs/08). |
| V10.2 Malicious code search | Pass | CodeQL on every PR (docs/08); `Architecture.Tests/DependencyRulesTests`. |
| V10.3 Deployed application integrity | Pass | Chiseled, non-root, read-only-friendly image; Trivy finds no HIGH or CRITICAL (PR counts); `Architecture.Tests/TestServerBindingTests` guards the ports. |

## V11 Business logic

| Section | Status | Evidence |
|---|---|---|
| V11.1 Business logic security (sequence, limits, no automation abuse) | Pass | `Hardening/RateLimitGroupTests`, `Scanning/ScanIdempotencyTests`, `Auth/RateLimitTests`, `Pairing/PairingLockoutTests`. |

## V12 Files and resources

| Section | Status | Evidence |
|---|---|---|
| V12.1 File upload (size, zip bombs, storage) | Pass | 4 MB cap and 413: `Scanning/Photo/PhotoUploadTests.A_file_over_four_megabytes_is_refused_with_413_and_nothing_is_stored`; export zips expire after 24 h (`Exports/ExportTests`). |
| V12.2 File integrity (re-encode, no original kept) | Pass | `PhotoAcceptanceTests.An_uploaded_photo_has_no_exif_or_gps_after_storage_and_the_original_bytes_are_not_kept`; server-generated names: `PhotoUploadTests.The_stored_name_is_ours_and_never_the_name_the_client_sent`. |
| V12.3 File execution (no path traversal, no user-named paths) | Pass | Server-chosen file names; media routes never redirect or follow stored paths out of the folder: `Security/NoRedirectTests` (locked). |
| V12.4 File storage (private, per library) | Pass | `PhotoAcceptanceTests.A_stored_photo_is_readable_only_by_members_of_its_library`, `Catalog/CoverEndpointTests`. |
| V12.5 File download (headers, `nosniff`) | Pass | Media answers carry `nosniff`: `Catalog/CoverEndpointTests`; export download link is bound to one export and one user and expires (`Exports/ExportEndpoints.cs:78`). |
| V12.6 SSRF protection | Pass | `Hardening/OutboundFetchTests` (allow list, private-address refusal, redirect refusal). |

## V13 API and web service

| Section | Status | Evidence |
|---|---|---|
| V13.1 Generic web service security (same auth, schema-validated, fails closed) | Pass, with G5 | `Security/MatrixCompletenessTests`; ZAP API scan from the OpenAPI document as an editor ends with no FAIL and no unexplained WARN. |
| V13.2 RESTful web service (method allow list, content type, CSRF) | Pass | Wrong methods are 405 and wrong content types 415 (ZAP API scan saw 142 and 22); `Security/CsrfEveryWriteRouteTests`. |
| V13.3 SOAP | N/A | Not used. |
| V13.4 GraphQL | N/A | Not used. |

## V14 Configuration

| Section | Status | Evidence |
|---|---|---|
| V14.1 Build and deploy | Pass | `scripts/check.ps1` (locked restore, warnings as errors), `deploy/docker/Dockerfile.api`. |
| V14.2 Dependency (known-vulnerable components, updates) | Gap G2, G3 | `scripts/security/Run-DependencyAudit.ps1`: shipping .NET projects clean; npm HIGH advisories are build tooling (accepted with reasons); test-only NuGet advisories (pin proposed and checked: both clear with 10.0.13). Dependabot weekly. |
| V14.3 Unintended security disclosure (no stack traces, no debug, no version banners) | Pass | `Security/ProductionErrorTests.The_developer_pages_do_not_exist_in_production`; ZAP "Server Leaks Version" and "X-Powered-By" rules pass in both scans. |
| V14.4 HTTP security headers | Pass, with G4 | `Hardening/SecurityHeadersTests` (enforced CSP, `nosniff`, `Referrer-Policy`, `Permissions-Policy`, COOP, frame ancestors); ZAP baseline has no unexplained WARN. |
| V14.5 HTTP request header validation (host header, methods) | Pass | `Hardening/SecurityHeadersTests.A_cross_origin_preflight_gets_no_cors_headers`, `Auth/SessionCookieTests.No_cors_origin_is_allowed`; unknown methods are 405. |

## How to refresh this file

1. `pwsh scripts/security/Run-ZapBaseline.ps1 -WebDist app/dist` and `Run-ZapApi.ps1 -WebDist app/dist` (build the web export first: `npm run generate --prefix app`, then `npm run build:web --prefix app -- --output-dir dist`).
2. `pwsh scripts/security/Run-ImageScan.ps1` and `Run-DependencyAudit.ps1`.
3. `pwsh scripts/security/Stop-Security.ps1` removes anything left (only containers labelled `cm.agent=qa-1c`).
4. Update the counts in the PR and any row whose evidence changed.
