# 08 — Security and privacy

Target: **OWASP ASVS Level 2** for the API and web app, **OWASP MASVS-L1** for the iOS app. Authentication and authorization are covered in depth in [07](07-authentication-and-authorization.md). This doc covers everything else.

## What we're protecting

| Asset | Why it matters |
|---|---|
| Your collection data | Personal. Combined with photos it reveals what's in your home and roughly what it's worth. |
| Shelf photos | Pictures of the inside of your home. Phone photos carry **GPS coordinates** in EXIF. |
| Accounts and sessions | Takeover means data loss or exposure |
| AI and provider budget | Abuse = a surprise bill (Claude) or a banned API key (IGDB, UPCitemdb) |
| Signing keys | Whoever holds them can mint tokens for any user |

## Threat model (STRIDE, condensed)

| Threat | Example | Mitigations |
|---|---|---|
| **S**poofing | Stolen refresh token; forged Apple id_token | Rotating refresh tokens with reuse detection; Keychain storage; full id_token validation (sig/iss/aud/exp/nonce); passkeys |
| **T**ampering | Mass assignment (`ownerLibraryId` in a PATCH body); edits to another user's item | Explicit request DTOs only; library comes from the route or the stored resource; ETag concurrency |
| **R**epudiation | "I didn't delete those 200 items" | `audit_events` for destructive and security actions; soft delete with restore |
| **I**nformation disclosure | IDOR on `/items/{id}`; photo URLs guessable; EXIF GPS leak | Resource authorization + query filters + IDOR tests; private blob containers, served only through authorized endpoints or short-lived SAS; EXIF stripped by re-encoding |
| **D**enial of service / cost | Flooding photo scans to burn Claude budget; huge uploads | Rate limits, upload caps, AI budget circuit breaker, per-provider quotas |
| **E**levation of privilege | Viewer calls an editor endpoint; normal user hits `/api/admin` | Role metadata on every endpoint + matrix tests; admin route group with its own policy |

## OWASP API Security Top 10 (2023) → controls

| # | Risk | Our controls |
|---|---|---|
| API1 | Broken object-level authorization | Resource-based policies, a 404 for non-members, EF global query filters, IDOR test suite |
| API2 | Broken authentication | OpenIddict standard flows, PKCE, short-lived access tokens, refresh rotation + reuse detection, passwordless only |
| API3 | Broken object-property-level authorization | No entity binding; read DTOs never include internal fields (`provider_payloads`, `trace` only for admins) |
| API4 | Unrestricted resource consumption | Rate limiter (below), 15 MB upload cap, pixel caps, max 50 codes per batch, pagination limits, request timeouts, AI budget breaker |
| API5 | Broken function-level authorization | Admin endpoints in a separate group + policy; reflection test that every endpoint declares its policy |
| API6 | Unrestricted access to sensitive business flows | Photo scan quotas per user/hour; export throttled to 1 per 10 min; account deletion requires re-auth |
| API7 | Server-side request forgery | We never fetch user-supplied URLs. Cover images are fetched only from an **allow-list of provider hosts** over HTTPS, with a redirect limit and a size cap. |
| API8 | Security misconfiguration | Hardened headers (below), Scalar/OpenAPI UI in development only, `ValidateOnStart` config, no detailed errors in production, chiseled non-root container |
| API9 | Improper inventory management | Single versioned API; OpenAPI snapshot + `oasdiff` breaking-change gate in CI |
| API10 | Unsafe consumption of APIs | Provider and LLM responses validated against schemas, string lengths capped, never rendered as HTML, circuit breakers + timeouts |

## AI-specific risks

| Risk | Mitigation |
|---|---|
| **Prompt injection via the image** (a spine or sticker reading "ignore your instructions…") | The model has **no tools** and no access to data. Its only output is a JSON schema of item guesses. The system prompt states that text in the image is data. Output is used only as **search terms** and is shown to the user for confirmation, so a successful injection can at worst produce a wrong title in your own review list. |
| Output used unsafely | Enum fields validated; free-text fields length-capped (title ≤ 300 chars) and rendered as plain text, never as markup |
| Cost abuse | On-device auto-snap gate (steady, sharp, title text, new view; barcodes win); misfire back-off; 60 pictures/user/hour; global monthly budget breaker (default $25); `ai_usage` accounting; alert at 50% and 80% of budget |
| Privacy of photos sent to the model | Photos are re-encoded (EXIF and GPS removed) **before** being sent. Under its API terms Anthropic doesn't train on API data. This is disclosed in the privacy policy and the first time the scanner takes a picture. |
| Visual index privacy | Embeddings of your confirmed photos are private to your library and are used only for your own matching. Provider artwork embeddings are global. |
| Embedding model supply chain | The ONNX model comes from the publisher's official release, is pinned by SHA-256 in the build and verified at start-up. It runs in-process with no network access of its own. |
| Tie-break requests | Candidate artwork is fetched by **our** server from allow-listed provider hosts and sent to Claude as image bytes. No URLs from user input or model output are ever fetched. |

## Rate limits (ASP.NET Core rate limiter, partitioned by user ID, or by IP when anonymous)

| Group | Limit |
|---|---|
| `/account/*`, `/connect/token` | 10/min per IP, 30/hour per IP (sliding window) |
| Photo scans | 60/hour per user; 10 concurrent in-flight per user |
| Barcode batches | 120/min per user |
| Catalogue search | 60/min per user |
| Everything else | 600/min per user (token bucket) |
| Export | 1 per 10 min per user |

## Upload pipeline

1. Kestrel limits the request size to 16 MB, and the endpoint limits the image part to 15 MB.
2. **Magic-byte check** (JPEG, PNG or WebP). The client's `Content-Type` and filename are ignored.
3. Decode with ImageSharp, with a **pixel limit** of 40 MP to guard against decompression bombs.
4. Auto-orient → resize → **re-encode to JPEG** (drops all metadata and any polyglot payload).
5. Store under a server-generated name in a private container. The original upload is never stored.
6. Serve only through an authorized endpoint or a 5-minute read-only SAS scoped to that one blob.

## HTTP hardening

- HTTPS only; HSTS (`max-age=31536000; includeSubDomains`) on the custom domain; TLS 1.2+ (managed by Container Apps ingress).
- `Content-Security-Policy` for the web app: `default-src 'self'; img-src 'self' blob: data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self' https://appleid.apple.com https://accounts.google.com`. Inline script is avoided; if the Expo web build needs any, nonces are used.
- `X-Content-Type-Options: nosniff`, `Referrer-Policy: strict-origin-when-cross-origin`, `Permissions-Policy: camera=(self), geolocation=()`, `Cross-Origin-Opener-Policy: same-origin`.
- **No CORS origins allowed** (same-origin web; iOS isn't subject to CORS).
- Cookies: `__Host-` prefix, `HttpOnly`, `Secure`, `SameSite=Strict`.

## Infrastructure security

- **Managed identity everywhere:** the API reaches Postgres (Entra auth, no password), Blob and Key Vault with its user-assigned identity. The only stored secrets are third-party API keys, and those live in Key Vault.
- **Network:** a VNet-integrated Container Apps environment. Postgres uses **private access** (no public endpoint). Storage and Key Vault use private endpoints in prod, or firewall rules if cost matters.
- **Container:** `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled` (no shell, no package manager, non-root), read-only root filesystem, image scanned by Trivy in CI.
- **CI/CD:** GitHub Actions authenticates to Azure with **OIDC federated credentials** (no stored cloud secrets). The `main` branch is protected; deploys need passing checks.
- **Least privilege:** the API's identity gets *Storage Blob Data Contributor* on one account, *Key Vault Secrets User* + *Certificate User*, and a Postgres role with DML on its schema only (DDL belongs to the migration identity).

## Supply chain

- Dependabot for NuGet, npm, GitHub Actions and the Docker base image. Weekly, grouped PRs.
- `dotnet list package --vulnerable --include-transitive` and `npm audit --audit-level=high` fail CI.
- CodeQL (C# + JavaScript/TypeScript) on every PR.
- GitHub secret scanning + push protection.
- Lock files are committed (`packages.lock.json` with `RestoreLockedMode` in CI, `package-lock.json`).
- Actions are pinned by commit SHA.

## Logging and monitoring

- **Never logged:** tokens, cookies, auth codes, email addresses, photo bytes, provider API keys. A redaction enricher plus unit tests on the logger configuration enforce this.
- Security events go to `audit_events` **and** App Insights: sign-in, sign-in failure, refresh-token reuse detected, device revoked, account deletion, membership change, export, admin actions.
- Alerts:
  - Refresh-token reuse detected (any)
  - More than 20 failed sign-ins per 10 min
  - AI spend over 50% / 80% of budget
  - 5xx rate over 2% for 5 min
  - A provider circuit breaker open for more than 15 min

## iOS app (MASVS-L1)

- Tokens are stored in the Keychain only (`expo-secure-store`, this-device-only accessibility); nothing sensitive goes in MMKV or AsyncStorage.
- No secrets in the bundle. All third-party calls are proxied by our API.
- App Transport Security stays at its defaults (HTTPS only). We do **not** pin certificates, because the operational risk outweighs the benefit for this threat model.
- A blur overlay hides the app-switcher snapshot of the review screen (optional, in settings).
- Camera permission string explains the purpose. Photos are kept in memory or a temp directory and deleted after upload. Nothing is written to the camera roll.

## Privacy

| Topic | Commitment |
|---|---|
| Data minimisation | No location, contacts, tracking or ads. The email is stored only if the IdP provides one. |
| Photos | EXIF/GPS stripped; deleted after 30 days, or right after commit if you choose that |
| Third parties | Anthropic (photo identification); metadata providers receive **only** barcodes/titles, never user identifiers |
| Export | Full CSV + JSON export at any time |
| Deletion | In-app; purge within 30 days; Apple token revocation |
| App Store | Privacy policy URL, privacy nutrition labels ("Data not linked to you: none collected for tracking"), `PrivacyInfo.xcprivacy` manifest via Expo config |
| Attribution | TMDB and IGDB attribution on the About screen, as their terms require |

## Security testing

| When | What |
|---|---|
| Every PR | Authorization matrix, IDOR tests, upload-validation tests, CodeQL, dependency audit, secret scan |
| Nightly | OWASP ZAP baseline scan against the staging revision; Trivy image scan |
| Before v1.0 launch | ASVS L2 checklist walkthrough (recorded in `docs/security/asvs-l2-checklist.md`), manual test of the auth flows (state/nonce/PKCE tampering, refresh replay, redirect URI manipulation), restore-from-backup drill |
| Yearly | Key rotation drill; dependency major-version review |
