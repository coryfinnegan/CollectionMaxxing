# 01 — Architecture

## Principles

1. **Boring, trusted core.** .NET 10 LTS, PostgreSQL, standard OAuth/OIDC. No novel infrastructure where a table and a background service will do.
2. **One deployable backend.** A modular monolith: one container serves the REST API, the OIDC endpoints, the web app's static files and background jobs. Split only when there's a measured reason.
3. **The scanner never waits on the network.** Barcodes decode on the device. Identification happens server-side and streams back.
4. **External data is untrusted.** Provider APIs and the LLM are adapters behind interfaces. Their output is validated, cached and never trusted for authorization or rendering as HTML.
5. **Testability is a design input.** Every I/O boundary is an interface with a fake. Time is injected (`TimeProvider`). See [09](09-testing-and-tdd.md).
6. **Passwordless, and no secrets in git.** Users have no passwords. Server secrets live in a locked-down folder on your PC (`.env` + Docker secrets), never in the repo.

## System context

```mermaid
flowchart LR
  subgraph Clients
    ios["iOS app<br/>Expo / React Native<br/>all recognition on-device:<br/>Apple Foundation Models + Vision"]
    web["Web app<br/>Expo web build<br/>browse + manage only"]
  end
  subgraph tailnet["Your Tailscale network (private, WireGuard)"]
    subgraph pc["Your PC: Windows 11, Docker Compose"]
      ts["Tailscale Serve<br/>HTTPS pc.tailnet.ts.net"]
      api["CollectionMaxxing.Api<br/>ASP.NET Core 10<br/>REST API, OIDC server,<br/>static web host, background jobs"]
      mig[["migrate<br/>EF Core bundle"]]
      pg[("PostgreSQL 17<br/>pg_trgm")]
      files[("files volume<br/>covers, exports")]
      dash["Aspire Dashboard<br/>logs, traces, metrics"]
    end
  end
  subgraph External["External services"]
    meta["Open Library, Google Books,<br/>TMDB, IGDB, UPCitemdb"]
  end
  ios -- "Tailscale, bearer token" --> ts
  web -- "Tailscale, same-origin cookie" --> ts
  ts --> api
  api --> pg
  api --> files
  api -.-> dash
  mig --> pg
  api --> meta
```

The web app is served **from the same origin as the API**. That lets the browser use an HttpOnly session cookie, so no tokens live in JavaScript, and it removes CORS entirely. The iOS app uses standard OAuth 2.0 Authorization Code + PKCE and gets bearer tokens. See [07](07-authentication-and-authorization.md).

## Technology stack

### Backend

| Concern | Choice | Notes |
|---|---|---|
| Runtime | **.NET 10 (LTS)** | Supported to Nov 2028. .NET 11 (Nov 2026) is STS, so skip it. |
| Web | ASP.NET Core Minimal APIs, route groups per feature | Built-in .NET 10 validation (`AddValidation`), `TypedResults`, SSE results |
| Data | **PostgreSQL 17**, EF Core 10 + Npgsql | `pg_trgm` for fuzzy title search/matching; `jsonb` for raw provider payloads |
| Identity | ASP.NET Core Identity + **OpenIddict 7** | Passkeys (new in .NET 10 Identity); Apple/Google through OpenIddict's web providers |
| Background work | `BackgroundService` + a Postgres job table (`FOR UPDATE SKIP LOCKED`) | Durable, multi-replica safe, no extra infrastructure |
| HTTP resilience | `Microsoft.Extensions.Http.Resilience` | Retry with jitter that honours `Retry-After`, timeouts, circuit breakers, per-provider rate limiters |
| Caching | `HybridCache` (memory) + `provider_cache` table (durable) | Provider responses are cached to respect quotas |
| Recognition | **None on the server.** All recognition runs on the iPhone with Apple frameworks (Vision, Foundation Models); the API only matches text against the catalogue | See [02](02-scanning-and-recognition.md#apple-frameworks-only-on-the-phone-0-per-scan) |
| Images | SixLabors.ImageSharp | Validate, auto-orient, re-encode (strips EXIF/GPS), thumbnails. Its Split License is free at our scale; revisit if this ever goes commercial. |
| API docs | `Microsoft.AspNetCore.OpenApi` (OpenAPI 3.1) + Scalar UI (development only) | The TypeScript client is generated from this document |
| Observability | OpenTelemetry → standalone **Aspire Dashboard** container on the PC | Traces, metrics, logs; one trace across app → API → providers |
| Local orchestration | **Aspire** AppHost | One F5 starts Postgres, the API and the Expo web dev server |
| Packages | Central Package Management, `TreatWarningsAsErrors`, nullable enabled, `.editorconfig` | |

We deliberately avoid **MediatR and AutoMapper**. Both moved to commercial licensing in 2025, and we don't need them: handlers are plain classes injected into endpoints, and mapping is explicit. The same goes for **FluentAssertions** (v8 is commercial); we use Shouldly instead.

### Front end (details in [05](05-frontend.md))

Expo SDK 57 (React Native 0.86, React 19.2), TypeScript, Expo Router, TanStack Query, Zustand, VisionCamera v5 with a small Swift module for Apple Vision and Foundation Models (iOS only), a client generated with orval from OpenAPI. iOS builds come from GitHub's free macOS runners, signed and uploaded to TestFlight (ADR-024, which replaced AltStore from ADR-019).

## Solution structure

```
src/
  CollectionMaxxing.AppHost/          Aspire orchestration (dev only)
  CollectionMaxxing.ServiceDefaults/  OTel, health checks, resilience defaults
  CollectionMaxxing.Domain/           Entities, value objects, domain services. No dependencies.
  CollectionMaxxing.Application/      Use-case handlers, ports (interfaces), DTOs
  CollectionMaxxing.Infrastructure/   EF Core, provider clients, blob, image (re-encode your-photo covers)
  CollectionMaxxing.Api/              Composition root: endpoints, auth, OpenIddict, login pages, SPA host
tests/
  CollectionMaxxing.Domain.Tests/
  CollectionMaxxing.Application.Tests/
  CollectionMaxxing.Infrastructure.Tests/   Testcontainers Postgres, WireMock providers
  CollectionMaxxing.Api.Tests/              WebApplicationFactory: acceptance + authorization matrix
  CollectionMaxxing.TestSupport/            Shared fixtures: Testcontainers Postgres, test auth scheme, library builders
  CollectionMaxxing.Architecture.Tests/     Layering rules
  CollectionMaxxing.Recognition.Evals/      Scores eval results uploaded by the on-phone eval runner
app/                                        Expo app (iOS + web)
deploy/                                     docker-compose.yml, Tailscale Serve config, backup jobs, runbooks
```

### Dependency rules (enforced by `Architecture.Tests`)

```
Api ──► Application ──► Domain
 │            ▲
 └──► Infrastructure ──┘   (Infrastructure implements Application's ports)
```

- **Domain** references nothing except the BCL: no EF, no ASP.NET, no HTTP.
- **Application** references Domain, plus EF Core's abstractions so `IAppDbContext` can expose `DbSet<T>`. There's no Npgsql and no ASP.NET, and an architecture test enforces that. It defines ports such as `ICatalogProvider`, `IVisionIdentifier`, `IBlobStore`, `IAppDbContext` and `IClock`.
- **Infrastructure** implements the ports. It is the only project that touches the network, the database driver or the file system.
- **Api** wires everything, owns the HTTP and auth concerns, and contains no business rules.
- Endpoint classes may not reference `DbContext` directly; they call application handlers.

### Feature slices inside Application

Code is organised by feature (vertical slice), not by technical layer:

```
Application/
  Library/      AddItem, UpdateItem, DeleteItem, ListItems, GetItem, MoveItems
  Scanning/     StartSession, SubmitBarcodes, SubmitPhoto, ResolveCandidate, CommitSession
  Catalog/      SearchCatalog, LookupIdentifier, RefreshRelease
  Ownership/    CheckOwnership
  Locations/    …
  Accounts/     DeleteAccount, ExportLibrary
```

Each use case is one handler class with one public method, a request record and a response record. For example: `SubmitBarcodes.Handler.HandleAsync(SubmitBarcodes.Command, CancellationToken)`.

### Built for parallel agents

Several agents build this at once ([13](13-agent-orchestration.md)), so the structure keeps them out of each other's files:

- **Each feature registers itself** (`builder.AddCatalog()`, `app.MapCatalogEndpoints()`). `Program.cs` is written once and rarely changes.
- **Entity configurations live in each feature's folder** and are picked up with `ApplyConfigurationsFromAssembly`, so nobody edits `AppDbContext` to add a table.
- **Wave 0 creates the whole v1 schema** (from [03](03-data-model.md)) **and the whole v1 API contract** (from [04](04-api.md)). Every endpoint is stubbed behind its real authorization policy, so later work fills in behaviour instead of adding shared structure.
- **The app mirrors this:** a folder per feature under `app/features/`, routes created up front, and mock API handlers generated from the contract.

## Key runtime flows

### Live barcode batch

```mermaid
sequenceDiagram
  actor U as You
  participant App as iOS app
  participant API as API
  participant DB as Catalog (Postgres)
  participant P as Providers
  U->>App: Hold phone over 6 game cases
  App->>App: Apple Vision decodes 6 barcodes on device
  App->>App: Haptic tick, 6 lines on the receipt tray
  App->>API: POST /scan-sessions/{id}/barcodes (Idempotency-Key)
  API->>DB: Look up by GTIN
  DB-->>API: 4 known releases
  API->>P: UPCitemdb, then IGDB, for the 2 unknown
  API-->>App: 6 candidates (5 matched, 1 pending)
  API--)App: SSE candidate.updated (pending resolved)
  U->>App: Review, then "Add 6 items"
  App->>API: POST /scan-sessions/{id}/commit
  API->>DB: Insert items, remember barcode → release
```

### Shelf picture (all recognition on the phone)

```mermaid
sequenceDiagram
  actor U as You
  participant App as iPhone (Foundation Models + Vision)
  participant API as API
  participant P as Providers
  U->>App: Hold steady on a shelf
  App->>App: Auto-snap → on-device model streams 17 items (title, creator, kind)
  App->>App: Vision text boxes → each item's exact box on the photo
  App->>API: POST /scan-sessions/{id}/identify (text only, no photo)
  loop each item
    API->>API: Catalogue trigram search
    API->>P: Open Library / IGDB / TMDB search (cached)
    API--)App: SSE candidate.updated → card
  end
  U->>App: Tap a card → photo with that spine boxed (drawn on the phone)
```

### Picture of one item (no barcode in view)

```mermaid
sequenceDiagram
  actor U as You
  participant App as iPhone (Foundation Models + Vision)
  participant API as API
  participant P as Providers
  U->>App: Hold up a loose N64 cart
  App->>App: Auto-snap → model reads "Super Mario 64", platform N64 (cart shape), loose cart
  App->>App: Feature print vs your confirmed items (no match yet)
  App->>API: POST /identify { title, platform: N64, completeness: cartOnly }
  API->>P: IGDB "Super Mario 64" (cached)
  API->>API: Score (cartridge profile) → Matched
  API--)App: SSE → "SUPER MARIO 64 · N64 · READY"
  U->>App: Add → its feature print is saved, so the next one matches instantly
```

## Cross-cutting concerns

| Concern | Approach |
|---|---|
| Errors | RFC 9457 Problem Details everywhere; no stack traces outside development |
| Validation | Request DTOs are validated at the edge. Domain invariants are enforced in constructors and methods, and are never bypassed by EF. |
| Time | `TimeProvider` injected; tests use `FakeTimeProvider` |
| IDs | UUIDv7 (`Guid.CreateVersion7()`): time-ordered for index locality |
| Concurrency | Optimistic, using Postgres `xmin` as the row version and exposed as an `ETag` |
| Idempotency | `Idempotency-Key` on create/commit endpoints. Responses are stored for 24 h, so mobile retries are safe. |
| Configuration | Options pattern with validation at startup (`ValidateOnStart`); secrets come only from the PC's `.env`/Docker secrets (prod) or user-secrets (dev) |
| Feature flags | `Microsoft.FeatureManagement` backed by config. Used for AI scanning and new providers. |
| Logging | Structured logs with no PII or tokens. A user ID is fine; an email is not. |
| Health | `/health/live`, `/health/ready` (DB + blob); a minimal readiness check for the external providers |

## Why not microservices, serverless functions or a separate worker?

One person builds this, and the traffic is a single household's. A second deployable doubles CI/CD, auth wiring and failure modes for no benefit. The scan worker is a `BackgroundService` in the same process. The job table makes it safe to run several replicas. If AI scanning ever needs independent scaling, the worker project can move into its own container in the compose file without code changes, because it talks only to the job table and the ports.
