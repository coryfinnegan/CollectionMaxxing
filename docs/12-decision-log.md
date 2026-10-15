# 12 — Decision log (ADRs)

Short Architecture Decision Records. Status is **Proposed** until you approve the plan; after that, changes get a new ADR that supersedes the old one.

---

## ADR-001: .NET 10 modular monolith

- **Status:** Proposed
- **Context:** One developer and one household of users. You trust .NET above anything else for the backend.
- **Decision:** A single ASP.NET Core 10 (LTS) service with Domain / Application / Infrastructure / Api projects, enforced by architecture tests.
- **Alternatives:**
  - Microservices: operational cost with no benefit at this scale.
  - Azure Functions: cold starts, awkward for OIDC + SSE + long-running background jobs.
  - .NET 11 (STS): shorter support window. Upgrade to .NET 12 LTS in late 2027.
- **Consequences:** Simple to run and debug. If the AI worker ever needs to scale independently, it can be split into its own Container App because it only talks to the job table and the ports.

## ADR-002: PostgreSQL over SQL Server

- **Status:** Proposed
- **Context:** We need fuzzy title matching, JSON payload storage and low cost.
- **Decision:** PostgreSQL 17 (in Docker on your PC, per ADR-018) with `pg_trgm` and `jsonb`, through EF Core + Npgsql.
- **Alternatives:**
  - Azure SQL Database serverless free offer: free, but auto-pause means cold resumes of up to a minute, and its fuzzy matching is weaker.
  - Cosmos DB: wrong shape for relational ownership data.
- **Consequences:** Excellent fuzzy search; Entra/managed-identity auth supported; portable to any host. About $16/month for the smallest always-on tier.

## ADR-003: Expo (React Native) for iOS and web

- **Status:** **Accepted** (D1, 2026-10-12: keep Expo "only if it lets me do iOS without a Mac and without paying Apple"). It does: GitHub's free macOS runners build it and AltStore installs it with a free Apple ID (ADR-019, replacing EAS). It's also the only option where everyday changes skip the cloud Mac: JavaScript reloads live from your PC, so the free macOS minutes go only to native changes. .NET MAUI's Mac-free route, Hot Restart, needs the paid membership and was removed in Visual Studio 2026. SwiftUI and Flutter need a cloud build for every change.
- **Context:**
  - iOS is required and web is required.
  - The best possible camera/barcode scanning matters most.
  - Development happens on **Windows**.
  - You don't mind what the front end is written in.
- **Decision:** Expo SDK 57 + TypeScript + Expo Router, one codebase for iOS and web; EAS cloud builds for iOS.
- **Alternatives:**
  - **.NET MAUI + Blazor Hybrid/Blazor Web:** C# everywhere, but iOS builds need a paired Mac, the multi-barcode camera ecosystem is thinner, and the web and mobile UIs diverge anyway.
  - **SwiftUI + a separate React/Blazor web app:** the best possible iOS (VisionKit `DataScannerViewController`), but it needs a Mac, means two codebases, and doubles the feature work.
  - **Flutter:** viable, but less web-friendly for table-heavy screens and a smaller ecosystem for our needs.
- **Consequences:**
  - No Mac needed.
  - Strong scanning through VisionCamera v5 + Apple Vision and Foundation Models via a small Swift module (ADR-017).
  - The front end isn't C#, so the generated OpenAPI client and CI keep the TypeScript side honest.
  - Android becomes cheap later.

## ADR-004: Self-hosted passwordless identity (OpenIddict + ASP.NET Core Identity)

- **Status:** Resolved (D2): OpenIddict + Identity kept; **sign-in methods amended by ADR-019**: setup code + passkeys on the web, QR pairing for the iPhone; no Sign in with Apple and no Google
- **Context:**
  - The App Store requires Sign in with Apple when social login is offered.
  - We want the best iOS sign-in UX and no per-user cost.
  - The user base may grow beyond the household.
- **Decision:** OpenIddict 7 OIDC server inside the API, with ASP.NET Core Identity for users. Sign-in with Apple, passkeys and Google (v1.1). No passwords.
- **Alternatives:**
  - **Microsoft Entra External ID:** managed, free tier, supports Apple/Google. Less control over the UX, and more configuration surface.
  - **Auth0 / Clerk:** good SDKs, but a third-party dependency and per-MAU pricing at scale.
  - **ASP.NET Identity bearer tokens (`MapIdentityApi`):** simple, but non-standard opaque tokens with weaker revocation.
- **Consequences:** We own token issuance, so key rotation, tests and auditing are on us (all covered in [07](07-authentication-and-authorization.md)). Moving to Entra later changes only the token authority the API validates against.

## ADR-005: Web app on the same origin with cookie auth (BFF pattern)

- **Status:** Proposed
- **Context:** Tokens held in browser JavaScript are exposed to any XSS.
- **Decision:** The API serves the Expo web build. The browser authenticates with an HttpOnly `__Host-` cookie, plus `SameSite=Strict` and an `X-CSRF` header. No CORS.
- **Alternatives:**
  - An SPA with OAuth code + PKCE in the browser: tokens in JS, plus CORS.
  - A separate static host (Static Web Apps) plus a proxy: more moving parts.
- **Consequences:** The simplest secure option. Web static assets are served by Kestrel, which is fine at this scale; a CDN can be added later.

## ADR-006: On-device barcode decoding

- **Status:** **Amended by ADR-017**: Apple Vision instead of ML Kit; no server-side ZXing
- **Context:** Scanning has to feel instant and work in basements without signal.
- **Decision:** VisionCamera v5 + ML Kit on device (multi-code per frame). The server only resolves codes to catalogue entries. Server-side ZXing.Net is used only for codes inside uploaded stills.
- **Alternatives:** Upload frames for server decoding (latency, bandwidth); Expo's built-in camera scanner (weaker multi-code support).
- **Consequences:** Offline queueing works naturally. Requires a development build (not Expo Go), which is fine; it's built on a GitHub macOS runner (ADR-019).

## ADR-007: Claude for photo identification

- **Status:** **Superseded by ADR-015** (2026-10-12). Claude is now an optional tier 4, off by default.
- **Context:** Book spines, VHS and loose cartridges often have no usable barcode. We need to read many titles from a shelf photo, identify single items from a close-up, and return structured data. For single items Claude also acts as the visual tie-breaker between candidate artworks (see ADR-013).
- **Decision:** Anthropic API, `claude-opus-5-5`, structured outputs, official C# SDK. Effort and model are tuned through the eval suite, and any move to a cheaper model is your call, backed by eval data.
- **Alternatives:**
  - Azure AI Vision OCR + heuristics: reads text, but can't tell a spine from a sticker and doesn't know what a title is.
  - On-device Apple Vision OCR only: same problem, but a good future pre-pass.
  - Claude through Microsoft Foundry: Azure billing, but some API features (such as server-side fallbacks) aren't available there. We can revisit if Azure-only billing matters to you.
- **Consequences:** About $0.05–0.12 per photo; budget breaker and quotas required; photos leave our boundary (EXIF stripped first, disclosed in the privacy policy).

## ADR-008: Azure Container Apps hosting

- **Status:** **Superseded by ADR-018** (2026-10-12): self-hosted on your PC.
- **Context:** .NET backend, managed identity, low cost, room to grow.
- **Decision:** Container Apps (Consumption) + Postgres Flexible B1ms + Blob + Key Vault + ACR, in one region, VNet-integrated; Bicep via azd; GitHub Actions with OIDC.
- **Alternatives:**
  - **App Service B1:** simpler, but no scale-to-zero, and revisions/traffic splitting are weaker.
  - **AKS:** overkill.
  - **Fly.io / Railway / a VPS:** cheaper or simpler, but no managed identity, and more do-it-yourself security work.
  - **Home server:** free, but exposes the home network and lacks backups/uptime.
- **Consequences:** About $30–45/month. Revision-based zero-downtime deploys with instant rollback.

## ADR-009: Provider abstraction and a learned local catalogue

- **Status:** Proposed
- **Context:** Free metadata sources each cover a slice, have quotas, and change terms. UPC → title is the weakest link.
- **Decision:** An `ICatalogProvider` port with a chain per media kind. Responses are cached. Confirmed barcode → release mappings persist locally and are tried first.
- **Consequences:** The catalogue gets better the more it's used. Providers can be swapped without touching the domain.

## ADR-010: Background jobs in-process on a Postgres job table

- **Status:** Proposed
- **Context:** Photo identification and pending lookups need durable async processing.
- **Decision:** A `BackgroundService` polling a `jobs` table with `FOR UPDATE SKIP LOCKED`, retries with backoff, and a dead-letter status.
- **Alternatives:**
  - Azure Service Bus or Storage Queues: an extra resource plus messaging semantics we don't need yet.
  - Hangfire: fine, but another dependency and dashboard to secure.
- **Consequences:** No new infrastructure; transactional enqueue (job + session update commit together). If throughput ever demands it, swap the job store behind the `IJobQueue` port.

## ADR-011: Real Postgres in tests

- **Status:** Proposed
- **Context:** The EF Core in-memory provider doesn't behave like SQL (constraints, `pg_trgm`, `jsonb`, concurrency).
- **Decision:** Testcontainers Postgres for Application, Infrastructure and Api tests, with one shared container per run and a Respawn reset per test.
- **Consequences:** Docker is required for `dotnet test`. Tests catch real SQL bugs. Slightly slower than pure unit tests, mitigated by keeping logic in the pure Domain.

## ADR-012: No MediatR, AutoMapper or FluentAssertions

- **Status:** Proposed
- **Context:** All three moved to commercial licences in 2025, and none of them is necessary.
- **Decision:** Plain handler classes injected into endpoints; explicit mapping (a Mapperly source generator if it ever gets tedious); Shouldly for assertions.
- **Consequences:** Fewer dependencies, no licence exposure, and the code is easier to step through.

## ADR-013: Cover recognition = Claude + a self-hosted visual index

- **Status:** **Superseded by ADR-017** (2026-10-12). No visual index and no Claude: Apple's model reads items, and Apple feature prints compare artwork on the phone.
- **Context:**
  - The user wants items identified from a picture of the item itself: loose cartridges, VHS sleeves and tapes, boxes, discs.
  - Text reading alone can't tell editions and regional variants apart, and gets weaker on worn labels.
  - Every snap costing an LLM call, even for something seen before, is wasteful.
- **Decision:** Two independent signals, then a rerank:
  1. Claude vision reads the item: title, platform from the shell shape, completeness, region, edition marks.
  2. A **visual index**: an image-embedding model (SigLIP 2 or CLIP, int8 ONNX) runs in-process with ONNX Runtime, and vectors are stored in **pgvector** (HNSW). It covers provider artwork, an optional per-platform seed, and every photo you confirm.

  A Claude **tie-break** call compares your photo with the candidates' artwork only when the two signals are close or disagree. Repeats of confirmed photos take a free fast path.
- **Alternatives:**
  - **Claude only:** simpler, but weaker on editions/variants, no free fast path, and every snap pays.
  - **Google Cloud Vision web detection:** good at naming famous covers, but sends photos to another third party, has a per-call cost, and is weak on loose carts. Could be added later as one more candidate source.
  - **Azure AI Vision multimodal embeddings / other managed embedding APIs:** managed, but region-limited, an extra service and key, and per-call cost.
  - **A trained classifier:** needs a labelled dataset we don't have. The confirmed-photo index gives most of the benefit with no training.
- **Consequences:**
  - ~~The API container grows to 1 vCPU / 2 GiB~~ No longer needed: queries embed on the phone, and a scale-to-zero job builds the index (ADR-015).
  - Embeddings get a model-version column and a re-index job.
  - The eval suite gains a cover set with per-kind top-1/top-3 targets.
  - Accuracy improves the more you use it, because confirmed photos join the index.

## ADR-014: One scanner, no modes

- **Status:** Accepted (your direction, 2026-10-12)
- **Context:** An earlier draft had Barcodes / Cover / Shelf / Check modes. You want to open the camera and scan, and have the app work out what it's looking at.
- **Decision:** A single scanner screen.
  - Barcodes are read continuously on the device.
  - When no barcode is in view, an **auto-snap gate** (steady, sharp, title text or an item-shaped rectangle, a view not snapped before) takes a picture.
  - The **phone** splits each picture into items (text columns, rectangles) and identifies them on-device (ADR-015).
  - Every result carries ownership, which replaces Check mode. Nothing is added without Review → Add.
  - A shutter button remains as a manual override.
- **Alternatives:** explicit modes (rejected: extra decisions for the user, and the wrong mode gives bad results); always-on continuous AI on video frames (too expensive and slow); shutter-only pictures (one more thing to remember; auto-snap with a shutter fallback gives the best of both).
- **Consequences:**
  - The auto-snap gate is new, client-side logic and must be well tested; since recognition moved on-device (ADR-015), misfires cost nothing.
  - The API loses its `mode` and `kind` parameters, so it gets simpler.
  - A viewer role can scan (to check ownership) but not commit.

## ADR-015: On-device recognition first; LLMs optional

- **Status:** **Amended by ADR-017** (SigLIP/ONNX, pgvector and Claude removed; Apple frameworks only). Originally accepted (your direction, 2026-10-12: "as low cost as possible, without using an LLM if possible")
- **Context:**
  - Ingestion happens almost entirely on an iPhone, which has a Neural Engine, Apple Vision (text recognition, rectangles, feature prints) and, on iOS 27 Apple Intelligence devices, an on-device foundation model.
  - Sending every picture to a cloud LLM costs money per scan and sends photos of your home off the device.
- **Decision:**
  - Recognition runs on the phone, cheapest signal first:
    - barcodes
    - Apple Vision text
    - SigLIP 2 artwork embedding (Apache 2.0, ONNX Runtime + Core ML)
    - zero-shot kind/platform
    - local nearest neighbours
  - The server only matches **text and vectors** (trigram search, free provider APIs, pgvector). Photos never leave the phone in the default path.
  - Optional tiers for leftovers: tier 3, Apple's on-device model (free, iOS 27 + Apple Intelligence, confirmed in the M3 spike); tier 4, Claude (paid, **off by default**, capped).
- **Alternatives:**
  - **Cloud LLM first (the old ADR-007):** best on odd cases, but costs money per scan and photos leave the phone.
  - **Apple MobileCLIP / MobileCLIP2:** smaller and made for iPhone, but the weights are under a research-only licence, so excluded.
  - **Apple's built-in feature print for catalogue matching:** free, but the server can't compute it for reference artwork. Used only on-device for de-duplication.
  - **VisionKit Visual Look Up:** doesn't expose results to apps.
  - **A custom-trained detector or classifier:** needs labelled data we don't have; zero-shot + nearest neighbours gets most of the value.
- **Consequences:**
  - $0 AI per scan, and better privacy.
  - Faster results (no LLM round trip).
  - Offline matching for your own items and downloaded platforms.
  - A ~90 MB model download on first use, plus per-platform slices.
  - A small Swift module, compiled by EAS since development is on Windows.
  - The API container shrinks back to 0.5 vCPU / 1 GiB; a nightly job builds the index.
  - Accuracy on odd items is lower than with an LLM; the evals measure it and the optional tiers cover it.

## ADR-016: Apple's on-device model as the primary reader, where it qualifies

- **Status:** Proposed, and expected to pass: your scanning phone is an **iPhone 17 Pro** (D12), which qualifies. The M3 gate on that phone makes the final call. **Amended by ADR-019:** the Private Cloud Compute tier is dropped (it needs App Store distribution), and the on-device model must work in a free-signed app, checked in an M0 spike. **Spike answer (PR #5, 2026-10-14): yes.** In a free-signed AltStore install on iOS 27.2, the model reported `available` with `.vision` and `.guidedGeneration`, and read a book cover from an image into a `@Generable` struct in 2.64 s. Vision read the EAN-13 in 0.06 s and the text in 0.19 s.
- **Context:**
  - iOS 27's Foundation Models framework accepts images on the on-device model, with guided generation (`@Generable`), streaming and image-aware tool calling (WWDC26 sessions 241 and 237).
  - It's free, private and on-device, and it can do in one call what the Vision pipeline does with hand-written heuristics: split a shelf into items, read sideways spines, infer platform, edition and completeness.
  - Its limits:
    - Apple Intelligence devices only
    - small context (4–8K tokens)
    - small-model world knowledge, so it can invent titles
    - safety guardrails that may decline some covers
    - behaviour that can change with each iOS release
- **Decision:**
  - Two on-device engines behind one `RecognitionEngine` interface. The **Apple model engine** is primary on qualifying devices *if* it passes the M3 decision gate (accuracy +5 points over the Vision pipeline, p50 ≤ 2.5 s, < 1% invented titles surviving grounding, < 2% refusals, thermals OK).
  - The **Vision pipeline engine** is always built and is the per-snap fallback.
  - Both share the SigLIP artwork step and server grounding, and **the model never decides a match alone**.
  - Apple's **Private Cloud Compute** model becomes tier 3 for hard cases (free under 2M first-time downloads, if image input is confirmed). This makes Claude unnecessary for most users; it stays optional and off.
- **Alternatives:**
  - **Apple model only** (no Vision pipeline): simpler, but leaves older iPhones, the web and refusals with no reader.
  - **Vision pipeline only** (ADR-015 as written): works everywhere, but the hand-written heuristics will be weaker on messy shelves.
  - **Wait for Callstack's React Native binding:** it doesn't support images yet; our Swift bridge is one function and can be swapped later.
- **Consequences:**
  - Two engines to maintain, mitigated by one shared interface, contract and eval set.
  - A slightly larger Swift module, compiled by EAS.
  - An on-phone eval runner, since there's no Mac.
  - $0 per scan either way.

## ADR-017: iPhone-only recognition with Apple frameworks only

- **Status:** Accepted; PCC hard-case tier removed and builds moved off EAS by **ADR-019**. Originally accepted (your direction, 2026-10-12: "no vision work in the web app… only the Apple vision libraries on the mobile app… for simplicity and cost")
- **Context:** The plan had grown a cross-platform recognition stack:
  - ML Kit barcodes
  - SigLIP 2 via ONNX on phone and server
  - a pgvector artwork index with a nightly builder job
  - web photo scanning with server-side ZXing
  - an optional Claude tier

  With an iPhone 17 Pro and iOS 27's Foundation Models image input, Apple's frameworks cover the jobs that matter.
- **Decision:**
  - **Web app:** no camera, scanning or recognition. Browse, manage, typed search, CSV import.
  - **iPhone app:** Apple frameworks only.
    - Vision for barcodes, text boxes, rectangles and feature prints
    - Foundation Models (on-device) to read pictures
    - Foundation Models (Private Cloud Compute) for hard cases
  - **Server:** text matching only (trigram + free provider APIs). It stores opaque feature-print backups and never computes or compares them.
  - **Removed:**
    - ML Kit
    - SigLIP 2 / ONNX on phone and server
    - pgvector and the index-builder job
    - server-side ZXing and picture uploads for recognition
    - Claude and Anthropic entirely, along with `ai_usage` and the budget breaker
    - web scanning screens
- **Alternatives:** keep SigLIP + a vector index (catalogue-wide image search, at the cost of a model download, an index job and pgvector); keep Claude as an optional tier (better on odd cases, but a third-party dependency and a bill). Both can be re-added later behind the same interfaces if the evals show a real gap.
- **Consequences:**
  - $0 AI cost.
  - Fewer moving parts: no model files, no vector DB, no index job, no AI keys.
  - Photos never reach our servers for recognition.
  - The web app is simpler.
  - There's no image search across the whole catalogue; an item whose text can't be read and that you've never scanned goes to Fix it.
  - Recognition requires the iPhone app: iOS 27 with Apple Intelligence for the model engine, or the Vision text fallback otherwise.

## ADR-018: Self-hosted on your gaming PC, reached through Tailscale

- **Status:** Accepted; distribution and auth amended by **ADR-019** (no paid Apple membership). D13 settled: the PC stays on 24/7, as it already does for Plex, ErsatzTV and Jellyfin. Originally accepted (your direction, 2026-10-12: "This is going to be a self hosted app")
- **Context:**
  - The app is personal and self-hosted.
  - The PC (Windows 11 Pro, i9-13900KF, 32 GB RAM, RTX 4070 Ti, plenty of disk) already runs Docker and Tailscale.
  - The iPhone needs to reach the server from anywhere, including thrift stores over cellular.
- **Decision:**
  - **Docker Compose** on the PC: `api` (.NET 10, serves the API and web app), a one-shot `migrate`, `postgres:17`, a `files` volume, the standalone **Aspire Dashboard**, and a nightly `backup` job.
  - **Tailscale** for access: `tailscale serve` provides HTTPS on `<pc>.<tailnet>.ts.net`. Nothing is exposed to the internet, and the API binds to `127.0.0.1`.
  - **CI** on GitHub-hosted runners → GHCR. A **self-hosted runner limited to the deploy job** on `main` pulls, migrates, restarts and smoke-tests. *(Replaced by a pull-based deployer in ADR-022.)*
  - **iPhone distribution:** TestFlight internal testing (or ad hoc). No App Store listing.
  - **Auth:**
    - native Sign in with Apple + token exchange on the iPhone, with no redirects;
    - web sign-in by a pairing code from the phone, then a passkey;
    - no Google sign-in;
    - the first account is the owner, and others join by invite.
  - **Offline "do I own this?"** moves into v1, so the PC being off doesn't break thrift-store checks.
  - **Backups:** nightly to a second drive, weekly encrypted off-site, restore drills, BitLocker.
- **Alternatives:**
  - **Azure (ADR-008):** managed and always on, but $30–40/month and more moving parts.
  - **Port forwarding + a domain + Let's Encrypt:** public exposure of a home server, with more attack surface.
  - **Cloudflare Tunnel:** good if public access is ever needed (share links, family without Tailscale); kept as a later option.
  - **Native Windows services instead of Docker:** they survive logouts, but lose parity with CI and dev.
- **Consequences:**
  - $0 hosting, and your data stays at home.
  - Availability depends on the PC being on (D13); the phone works offline for scanning and ownership checks.
  - You become the ops team: updates, backups, disk health. The backup heartbeat and restore drills keep that honest.
  - Azure-specific pieces are gone from the plan: managed identity, Key Vault, Container Apps revisions, Bicep, App Insights.
  - The GPU is unused for now, and is a free upgrade path if the evals ever argue for server-side models.

## ADR-019: $0 total: free Apple ID, sideloading, and pairing-based sign-in

- **Status:** Accepted (your direction, 2026-10-12: "I'm trying to spend 0 money here")
- **Context:** The only remaining spend was the Apple Developer Program ($99/yr), needed for EAS device builds, TestFlight, Sign in with Apple, push notifications and Apple's Private Cloud Compute model.
- **Decision:**
  - **Build** unsigned `.ipa` files on GitHub Actions macOS runners (free; unlimited since the repo went public, ADR-022).
  - **Install** with **AltStore**: AltServer runs on your PC, signs with your free Apple ID, and re-signs weekly in the background over home Wi-Fi. Sideloadly is the manual alternative.
  - **Auth without Apple sign-in:**
    - a one-time setup code creates the owner with a **passkey** (web);
    - iPhones are **paired by QR code** (a single-use, 2-minute token → a device-bound, rotating refresh token);
    - an optional **Face ID app lock** (LocalAuthentication);
    - recovery is a CLI command on the PC.
  - **Dropped:** Sign in with Apple, push notifications (results wait in the app; backup alerts by email), Apple Private Cloud Compute (hard cases go to Fix it), EAS Build and TestFlight.
  - **Off-site backups** go to Backblaze B2's free 10 GB or a free OneDrive.
  - **Day-one M0 spike:** prove that the on-device Foundation Model + Vision work in a free-signed app on your iPhone 17 Pro.
- **Alternatives:** pay $99/yr (TestFlight, Sign in with Apple, push, PCC); make the repo public (unlimited macOS minutes); SideStore (on-device re-signing, more setup).
- **Consequences:**
  - **$0 total.**
  - Weekly re-signing, handled automatically at home.
  - Limited to 3 sideloaded apps.
  - Developer Mode stays on.
  - Build budget of ~8–12 native builds a month.
  - If Apple tightens free provisioning, the $99 membership is a drop-in fallback with no code changes.

## ADR-020: Household sharing in v1

- **Status:** Accepted (your direction, 2026-10-12: "Family sharing should be in v1")
- **Context:** The data model, roles and authorization-matrix test were already multi-user. Only the invite flow, the members UI and onboarding were deferred to v1.1.
- **Decision:**
  - One library per household, owned by you. Family members join it by **invite** (link + QR code, single use, 7 days, revocable), as **Editor** or **Viewer**.
  - Each member creates their own passkey through the invite and pairs their own iPhone. No shared accounts.
  - Members are managed on the web (owner only). Items show who added them.
  - Removing a member revokes their devices and sessions at once.
  - Family reach the PC through Tailscale: the free plan allows 6 users, and an ACL limits them to the app's HTTPS port.
  - Family install the app with AltStore and their own free Apple ID, or use only the web app.
  - It lands in **M4**, which grows from ~2 to ~3 weeks.
- **Alternatives:**
  - One shared account: no attribution, and you can't revoke one person.
  - Separate libraries with sharing between them: needs a library switcher and more UI, with no benefit for one household.
- **Consequences:**
  - v1 is about 15 part-time weeks instead of 14.
  - Concurrent edits are real now, so the ETag/412 path gets a friendly prompt.
  - Each family member's phone needs your home Wi-Fi weekly for re-signing, or SideStore.

## ADR-021: Delivery by parallel agents in waves

- **Status:** Accepted (your direction, 2026-10-12: the estimates were "insanely long for something that will be entirely vibe coded")
- **Context:** The 15-week estimate assumed one person writing code with an assistant. Agents write code quickly, so the real constraints are:
  - merge conflicts in shared files;
  - verifying code nobody reads line by line;
  - your time on the iPhone;
  - the iOS build budget;
  - your Claude plan's usage limits.
- **Decision:**
  - **Five waves.** Wave 0 is a serial foundation that front-loads everything shared: the whole schema, the whole API contract with stubbed endpoints, wiring, app routes, mocks and fakes. Waves 1–4 run 4–6 builder agents at a time, each in its own git worktree.
  - **A lead agent** writes briefs as GitHub issues, integrates and merges one PR at a time, and owns the hot files.
  - **Every PR gets a verifier agent**, including a mutation-testing check.
  - **Locked tests** cover auth, identifiers/scoring and migrations.
  - **iOS builds are lead-only, at most daily.** Recognition prompts and thresholds are runtime-tunable, so tuning needs no build.
  - **You approve wave plans and test at checkpoints.** You don't review code.
- **Alternatives:**
  - One agent working serially: simpler, but slow.
  - Fully autonomous back-to-back waves: possible later (D16).
  - Cloud agents: they can't reach your Docker or tailnet.
- **Consequences:**
  - ~2–3 weeks to v1 instead of ~15.
  - Wave 0 is heavier and has to land before anything runs in parallel.
  - Quality rests on automated gates rather than human code review, so those gates are themselves tested early: the authorization matrix and architecture tests exist from the first commit.

## ADR-022: Public repository

- **Status:** Accepted (your direction, 2026-10-13: "Make the repo public")
- **Context:** A private repo gets ~200 free macOS minutes a month, about 8–12 iOS builds. With parallel agents (ADR-021), that was the tightest budget. On public repos, GitHub's standard runners are free with no minute limits.
- **Decision:** the repo is public, with these guards:
  - **No self-hosted runner** (a fork's PR could run code on it). A **pull-based deployer** on your PC deploys only images whose build attestation shows they were built by this repo's workflow from `main`. This replaces the runner in ADR-018.
  - **Fork PRs** need your approval before workflows run. They run with read-only tokens and no secrets.
  - **No secrets, photos, eval set, backups or real library data** in the repo. Secret scanning and push protection are on.
  - **Agents treat other people's issues, PRs and comments as untrusted data.** Briefs come only from you or the lead.
  - **GHCR images are public too.** They contain no secrets or data.
- **Alternatives:** stay private with a budget of ~8–12 builds a month; a paid plan (ruled out by $0).
- **Consequences:**
  - CI and iOS builds are unlimited and free.
  - Anyone can read the code and the plan.
  - Deploys lag a merge by up to ~2 minutes (the deployer's poll interval).

## ADR-023: Spend tokens only where judgement matters

- **Status:** Accepted (your direction, 2026-10-13: "4 agents is good but I want to do everything I can to minimize token usage")
- **Context:** With 4 agents at once, your Claude plan's usage limits are the binding budget.
- **Decision:**
  - **Model tiers per brief:** Haiku (mechanical, low effort), Sonnet (the default, medium effort), Sonnet at high effort for lead planning and the locked areas. **No Opus** (your call, 2026-10-13: "keeping it to Claude with haiku and sonnet").
  - **No tokens on boilerplate:** generators and formatters write it.
  - **A Wave 0 reference slice** that briefs say to copy.
  - **Quiet test output** through `scripts/check`.
  - **Small contexts:** a short root `CLAUDE.md`, per-folder rules, deny-read on build output and generated code, a fresh lead session per wave, at most 2 fix rounds.
  - **Cheaper verification:** CI first; the verifier sees the diff only; no verifier for docs- and config-only PRs.
  - **Local models** (Ollama on your GPU) are trialled once, in Wave 1, for chores. GitHub Copilot Free is your own autocomplete. ChatGPT/Codex isn't used in the build.
- **Alternatives:**
  - Opus everywhere: best quality, but it exhausts the limits fastest.
  - Local models as builders: $0, but too unreliable for multi-file agentic TDD on a 12 GB GPU.
  - A mix of vendors: more free quota, but two sets of conventions to maintain.
- **Consequences:**
  - The same plan uses much less of your limits.
  - The lead owns tier choices and adjusts them after each wave, based on first-time pass rates.
