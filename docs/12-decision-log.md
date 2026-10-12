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
- **Decision:** PostgreSQL 17 (Azure Flexible Server) with `pg_trgm` and `jsonb`, through EF Core + Npgsql.
- **Alternatives:**
  - Azure SQL Database serverless free offer: free, but auto-pause means cold resumes of up to a minute, and its fuzzy matching is weaker.
  - Cosmos DB: wrong shape for relational ownership data.
- **Consequences:** Excellent fuzzy search; Entra/managed-identity auth supported; portable to any host. About $16/month for the smallest always-on tier.

## ADR-003: Expo (React Native) for iOS and web

- **Status:** Proposed (**Decision needed: D1**)
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
  - Strong scanning through VisionCamera v5 + ML Kit.
  - The front end isn't C#, so the generated OpenAPI client and CI keep the TypeScript side honest.
  - Android becomes cheap later.

## ADR-004: Self-hosted passwordless identity (OpenIddict + ASP.NET Core Identity)

- **Status:** Proposed (**Decision needed: D2**)
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

- **Status:** Proposed
- **Context:** Scanning has to feel instant and work in basements without signal.
- **Decision:** VisionCamera v5 + ML Kit on device (multi-code per frame). The server only resolves codes to catalogue entries. Server-side ZXing.Net is used only for codes inside uploaded stills.
- **Alternatives:** Upload frames for server decoding (latency, bandwidth); Expo's built-in camera scanner (weaker multi-code support).
- **Consequences:** Offline queueing works naturally. Requires an EAS development build (not Expo Go), which is fine.

## ADR-007: Claude for photo identification

- **Status:** Proposed (**Decision needed: D4**, about sending photos)
- **Context:** Book spines, VHS and loose cartridges often have no usable barcode. We need to read many titles from a shelf photo, identify single items from a close-up, and return structured data. For single items Claude also acts as the visual tie-breaker between candidate artworks (see ADR-013).
- **Decision:** Anthropic API, `claude-opus-5-5`, structured outputs, official C# SDK. Effort and model are tuned through the eval suite, and any move to a cheaper model is your call, backed by eval data.
- **Alternatives:**
  - Azure AI Vision OCR + heuristics: reads text, but can't tell a spine from a sticker and doesn't know what a title is.
  - On-device Apple Vision OCR only: same problem, but a good future pre-pass.
  - Claude through Microsoft Foundry: Azure billing, but some API features (such as server-side fallbacks) aren't available there. We can revisit if Azure-only billing matters to you.
- **Consequences:** About $0.05–0.12 per photo; budget breaker and quotas required; photos leave our boundary (EXIF stripped first, disclosed in the privacy policy).

## ADR-008: Azure Container Apps hosting

- **Status:** Proposed (**Decision needed: D3**, budget)
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

- **Status:** Proposed (**Decision needed: D11**, which platforms to seed)
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
  - The API container grows to 1 vCPU / 2 GiB (~+$10/month). The model file goes in the image.
  - Embeddings get a model-version column and a re-index job.
  - The eval suite gains a cover set with per-kind top-1/top-3 targets.
  - Accuracy improves the more you use it, because confirmed photos join the index.

## ADR-014: One scanner, no modes

- **Status:** Accepted (your direction, 2026-10-12)
- **Context:** An earlier draft had Barcodes / Cover / Shelf / Check modes. You want to open the camera and scan, and have the app work out what it's looking at.
- **Decision:** A single scanner screen.
  - Barcodes are read continuously on the device.
  - When no barcode is in view, an **auto-snap gate** (steady, sharp, title text or an item-shaped rectangle, a view not snapped before) takes a picture.
  - The **server** decides from Claude's per-item `view` + box output whether that picture is one item (single-item route with the visual index) or many (per-item route).
  - Every result carries ownership, which replaces Check mode. Nothing is added without Review → Add.
  - A shutter button remains as a manual override.
- **Alternatives:** explicit modes (rejected: extra decisions for the user, and the wrong mode gives bad results); always-on continuous AI on video frames (too expensive and slow); shutter-only pictures (one more thing to remember; auto-snap with a shutter fallback gives the best of both).
- **Consequences:**
  - The auto-snap gate is new, client-side logic and must be well tested; misfires cost about $0.03 each, mitigated by the gate and a misfire back-off.
  - The API loses its `mode` and `kind` parameters, so it gets simpler.
  - A viewer role can scan (to check ownership) but not commit.
