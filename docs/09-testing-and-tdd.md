# 09 — Testing and TDD

## Working agreement

1. **No production code without a failing test first.** The exception is throwaway spikes in `spikes/` (never merged into `src/`).
2. **Red → green → refactor.** Commit on green. Keep refactors in their own commits when they're more than trivial.
3. **Outside-in for features.** Start with an **acceptance test** at the API boundary (WebApplicationFactory) that describes the behaviour in user terms. Drop to unit tests to drive out the domain logic it needs. The acceptance test going green means the feature is done.
4. **Bugs start as a failing test** reproducing them.
5. **Tests are deterministic.** Time comes from `FakeTimeProvider`, IDs from an injectable generator, and there are no real network calls (WireMock). The database is **real Postgres** (Testcontainers), never the EF in-memory provider, which hides real SQL behaviour.
6. **Test names state behaviour.** `Upc_a_with_bad_check_digit_is_rejected`, not `TestGtin3`.
7. **A test that's hard to write is design feedback.** Fix the design rather than mocking deeper.
8. **Agents follow the same rules, plus two more:**
   - in risky areas, the tests come from a separate agent and are locked;
   - a verifier agent checks every PR, including a mutation run on the changed code.

   See [13](13-agent-orchestration.md#the-verifier).

## Test pyramid

| Layer | Project | Share of tests | Speed | What it covers |
|---|---|---|---|---|
| Domain unit | `Domain.Tests` | ~55% | < 1 ms each | GTIN/ISBN parsing, classification, title normalisation, scoring, invariants (e.g. you can't lend a sold item) |
| Application | `Application.Tests` | ~20% | fast | Handlers with fakes for external ports (providers, blob) and a shared real Postgres: resolution chain ordering, duplicate detection, commit rules, quota behaviour |
| Integration | `Infrastructure.Tests` | ~15% | seconds | EF mappings + queries on real Postgres, migrations, provider clients against WireMock fixtures, ImageSharp on fixture images |
| API acceptance | `Api.Tests` | ~8% | seconds | Full HTTP pipeline: auth, authorization matrix, validation, Problem Details, idempotency, ETags, SSE |
| End-to-end | Maestro (iOS), Playwright (web) | ~2% | minutes | Critical journeys only |
| AI evals | `Recognition.Evals` + on-phone runner | separate | minutes, free | The golden photos run on your iPhone through both engines (Apple model, Vision text). Results are uploaded and scored by the .NET eval project. |

## Tooling

| Purpose | .NET | Front end |
|---|---|---|
| Runner | **xUnit v3** | Jest (via `jest-expo`) |
| Assertions | **Shouldly** (FluentAssertions v8 is commercial) | Jest `expect` |
| Fakes / mocks | Hand-written fakes for ports (preferred); **NSubstitute** where a fake is overkill | MSW for HTTP |
| Test data | Builders + **Bogus** | factory helpers |
| Database | **Testcontainers.PostgreSql** + Respawn (reset between tests) | — |
| HTTP stubs | **WireMock.Net** with recorded JSON fixtures | MSW |
| Snapshots | **Verify** (OpenAPI doc, prompt templates, provider mappings, CSV export format) | Jest snapshots (sparingly) |
| Time | `Microsoft.Extensions.TimeProvider.Testing` | Jest fake timers |
| Architecture rules | **ArchUnitNET** | eslint import boundaries |
| Mutation testing | **Stryker.NET** on Domain + Application (nightly). *As of 2026-10, Stryker can't drive xUnit v3 here, so verifiers hand-mutate key rules until it can.* | — |
| Coverage | coverlet + ReportGenerator | Jest `--coverage` |
| Components | — | React Native Testing Library |
| E2E | — | **Maestro** (iOS simulator on a GitHub macOS runner, release tags only, because it's slow), **Playwright** (web) |

## Quality gates

| Gate | Threshold | Where |
|---|---|---|
| Build | 0 warnings (`TreatWarningsAsErrors`), `dotnet format --verify-no-changes`, eslint + prettier, `tsc --noEmit` | every PR |
| Unit + integration + acceptance | all green | every PR |
| Line coverage | Domain ≥ 95%, Application ≥ 90%. Not enforced on Api/Infrastructure, where acceptance tests matter more than the number. | every PR |
| **Mutation score** | Domain ≥ 80% | nightly; a drop fails the next PR |
| Authorization matrix + every-endpoint-has-policy | all green | every PR |
| OpenAPI | snapshot approved; `oasdiff breaking` clean | every PR |
| Generated TS client | committed and up to date | every PR |
| Web E2E (Playwright) | green against the ephemeral CI stack | every PR to `main` |
| iOS E2E (Maestro) | green | release tags (GitHub macOS runner) |
| AI eval | no metric regresses more than 2 points vs. baseline | before any prompt/model/scorer change merges (manually triggered workflow) |

## How each layer handles its dependencies

| Dependency | Unit tests | Integration tests | E2E |
|---|---|---|---|
| Postgres | Domain: n/a (pure). Application handlers: **real Postgres**, using one shared Testcontainers instance per test run with a Respawn reset per test (~10–50 ms each). These are "sociable" unit tests, and that beats faking EF. | real (Testcontainers) | real (container) |
| Providers (Open Library, TMDB…) | fake `ICatalogProvider` | WireMock with recorded fixtures | `FakeCatalogProvider` seeded with ~50 known items |
| Apple Vision + Foundation Models (`cm-vision`) | `FakeVisionEngine` replaying barcode/OCR/model JSON captured from your iPhone | n/a | real on your iPhone (manual smoke checklist + the on-phone eval runner) |
| File storage (covers, your photos) | in-memory `IBlobStore` | file-system `IBlobStore` in a temp folder | file-system `IBlobStore` |
| Camera | n/a | n/a | `FakeScannerSource` (injects codes) |
| Clock | `FakeTimeProvider` | `FakeTimeProvider` | real |

The E2E environment (`ASPNETCORE_ENVIRONMENT=E2E`) swaps the fakes in through DI. An architecture test makes sure the fake registrations can't be reached when the environment is `Production`.

## Starter test list (what M1–M2 TDD begins with)

Kent Beck-style test list. Each line becomes one red test.

### Domain: identifiers

- [ ] Valid UPC-A normalises to GTIN-13 with a leading zero
- [ ] UPC-A with a bad check digit is rejected
- [ ] UPC-E expands to the correct UPC-A (one case per number-system rule)
- [ ] EAN-13 with prefix 978 is classified as an ISBN-13 book
- [ ] EAN-13 with prefix 979-1…979-9 is classified as an ISBN-13 book
- [ ] EAN-13 with prefix 9790 is classified as ISMN (not a book)
- [ ] ISBN-10 with an `X` check digit converts to the right ISBN-13
- [ ] 5-digit EAN add-ons are ignored by the classifier
- [ ] Leading and trailing whitespace and hyphens in typed ISBNs are tolerated

### Domain: title normalisation and scoring

- [ ] "The Matrix" and "Matrix, The" normalise equal
- [ ] "Final Fantasy VII" and "Final Fantasy 7" normalise equal
- [ ] "Ratchet & Clank" and "Ratchet and Clank" normalise equal
- [ ] Exact identifier match scores 1.0 regardless of title
- [ ] *Alien* (1979) outranks *Aliens* (1986) for the query "Alien 1979 DVD"
- [ ] *Metroid Prime* vs *Metroid Prime 2: Echoes* is Ambiguous, not Matched, when the subtitle is missing
- [ ] A platform mismatch (N64 vs GameCube) caps the score below the Matched threshold
- [ ] A lead of less than 0.08 over the runner-up yields Ambiguous
- [ ] The cover profile uses the visual term; the shelf-spine profile ignores it
- [ ] Loose-cartridge profile: a platform mismatch outweighs a strong visual match
- [ ] Visual similarity is rescaled to 0–1 and clamped (negative cosine → 0)
- [ ] The art term is ignored (weights redistributed) when the phone sent no distances

### Domain: inference from a picture

- [ ] A loose cartridge with no box visible → completeness *Cart only*, cover choice *my photo*
- [ ] A box with a manual visible → completeness suggestion *Complete* (needs confirmation)
- [ ] A disc only → completeness *Disc only*
- [ ] Region hint missing → the library's home region

### Domain: items

- [ ] An item can't be lent while its status is Sold
- [ ] Lending sets the status to Lent and records the borrower and date (from the clock)
- [ ] Rematching keeps condition, location, notes and tags

### Application: resolution and commit

- [ ] A barcode known in the local catalogue resolves without calling any provider
- [ ] An ISBN calls Open Library first and falls back to Google Books on not-found
- [ ] A product GTIN goes UPCitemdb → normaliser → IGDB when the category is video games
- [ ] When UPCitemdb's daily quota is exhausted, the candidate is Pending and a retry job is queued
- [ ] A provider timeout falls through to the next provider and is recorded in the trace
- [ ] Duplicate detection flags an exact release already owned, with its locations
- [ ] Owning the same work in another format is flagged soft, not as a duplicate
- [ ] Commit creates items only for accepted candidates, in one transaction
- [ ] Committing twice with the same Idempotency-Key creates items once
- [ ] Confirming a candidate persists barcode → release for future lookups
- [ ] A user-created release is invisible to other libraries

### Picture recognition (TypeScript on the phone + Application on the server)

- [ ] Spine grouping: OCR lines from a rotated-shelf fixture group into the expected columns (pure TS)
- [ ] Rectangle splitting: a floor-spread fixture yields one item per cover, each with its inner text
- [ ] Engine selection: Apple model unavailable, declined, timed out or out of context → that snap goes through the Vision text engine (fixture-driven fakes)
- [ ] Apple model items are matched to Vision text boxes by visible text for highlights
- [ ] A model title with no catalogue match never becomes Matched (grounding rule)
- [ ] Local match: a feature print close to one of your confirmed items matches on the phone with no server call (offline)
- [ ] `/identify` rejects any image payload and items > 16 KB
- [ ] A barcode found in a picture takes the barcode path; no artwork search runs for it
- [ ] Spine profile: "DUNE FRANK HERBERT" picks the novel over the film because the creator matches
- [ ] Cartridge profile: a platform mismatch outweighs a strong artwork match
- [ ] Another library's feature-print backups are never returned
- [ ] Confirming a picture candidate stores its feature print on the phone and backs it up (opaque) to the API
- [ ] `:art` distances re-score a close call (original vs *Player's Choice*) correctly

### Api: acceptance and security

- [ ] `POST /scan-sessions/{id}/barcodes` returns matched candidates for known codes (happy path)
- [ ] Anonymous → 401 on every non-allow-listed endpoint (matrix)
- [ ] Another user's item → 404; a viewer posting an item → 403 (matrix)
- [ ] Every endpoint declares an authorization requirement (reflection)
- [ ] Upload over 15 MB → 413; a PDF renamed `.jpg` → 415; a 50k × 50k PNG bomb → 422
- [ ] Uploaded JPEG with GPS EXIF → the stored blob has no EXIF
- [ ] A viewer can scan (barcodes and pictures) and sees ownership on each candidate, but commit returns 403
- [ ] The photo-scan limit returns 429 with `Retry-After`
- [ ] PATCH with a stale `If-Match` → 412 (two family members editing the same item: the second gets "Sam changed this. Reload?")
- [ ] An invite works once; an expired or revoked invite → 410; the member gets exactly the invited role
- [ ] Removing a member → their refresh tokens and cookie session fail immediately; their items keep `created_by`
- [ ] The SSE stream delivers `candidate.updated` after async resolution, and resumes from `Last-Event-ID`

### Front end

- [ ] Tray: a code seen in only 1 of 4 frames isn't added (stability filter)
- [ ] Tray: the same code scanned twice in a session adds one line
- [ ] Tray: "+1 copy" increments copies, not lines
- [ ] Tray: offline lines persist across an app restart and drain on reconnect with the same keys
- [ ] Review: the button count equals the checked rows; ambiguous rows are unchecked until picked
- [ ] Auth: concurrent 401s trigger exactly one refresh
- [ ] Auto-snap: steady + sharp + text with no barcode → exactly one snap
- [ ] Auto-snap: any barcode in view suppresses snapping
- [ ] Auto-snap: no second snap until the perceptual hash moves past the threshold
- [ ] Auto-snap: motion or blur below threshold → no snap
- [ ] Auto-snap: 3 "nothing found" results in a session raise the thresholds
- [ ] Closing the scanner with lines on the receipt offers Review / Keep for later / Discard; Discard adds nothing
- [ ] "Nice scan!" feedback fires on snap with no network (offline too)
- [ ] Result cards insert newest-first from SSE events; a card's + Add commits only that candidate
- [ ] Import from Photos queues each picked image with `trigger: import`

## AI evals

**Why:** prompt and model changes can silently trade recall for precision. Evals make AI quality a number we track, the same way tests make correctness binary.

**Golden set (built in M3):**

- 40–60 photos of **your real shelves**, in deliberately varied conditions: book spines (vertical, horizontal, stacked), DVD/Blu-ray spines, VHS clamshells, game cases (N64 boxes, PS2 cases, cartridges), mixed piles, a floor spread of covers, glare, low light, an angled shot.
- Each photo gets a `truth.json`: an ordered list of `{ title, media_type, platform?, year? }`, plus how many items are visible but unreadable.
- **Single-item set (60–100 photos):**
  - loose cartridges for each platform you collect, including at least one worn/faded label each
  - VHS sleeves, clamshells and bare tapes
  - game boxes, including a *Player's Choice*/*Greatest Hits* re-release beside its original
  - DVD/Blu-ray covers, discs only, a few book covers
  - some shot badly on purpose: glare, angle, low light

  Truth is `{ title, platform?, format, completeness, edition? }`. Metrics: **top-1 and top-3 accuracy per item kind**, platform accuracy, completeness accuracy, **which engine/step resolved each item** (Apple model, Vision text, artwork comparison, Fix it), and the on-phone hit rate on repeat snaps.
- The photos and truth files are stored **only on your PC** (and in its backups), never in the repo, which is public. Only aggregate scores are committed.

**Metrics:**

| Metric | Definition |
|---|---|
| Recall | matched truth items ÷ readable truth items |
| Precision | correct predicted items ÷ predicted items |
| Title accuracy | normalised title exact match (after the final catalogue match) |
| Type / platform accuracy | per-field |
| Latency p50 / p95 | wall clock |

**On-phone eval runner (no Mac needed):** the Apple model and Apple Vision only run on Apple hardware, and development happens on Windows. So dev builds include a hidden **Run evals** screen. It runs the golden set through both engines on your iPhone and uploads per-photo results (engine, items, latency, refusals, thermal state) to the API, where the eval report is built. This is how the M3 decision gate is measured, and how we re-check after each iOS release.

**How it runs:** tap **Run evals** on your iPhone (dev build). The phone uploads its results, and `dotnet test tests/CollectionMaxxing.Recognition.Evals --filter Category=Eval` with `RUN_EVALS=1` scores them against the truth files and writes `evals/results/<date>-<git-sha>.json` for trend tracking. A run costs **$0**: everything runs on your phone and your PC.

## CI pipeline (PR)

```
restore (locked) ─► build ─► format/lint ─┬─► unit (Domain, Application)
                                           ├─► integration (Testcontainers: Postgres; WireMock)
                                           ├─► api acceptance + authz matrix
                                           ├─► arch tests
                                           ├─► openapi snapshot + oasdiff + generated-client check
                                           ├─► app: tsc, eslint, jest
                                           └─► web e2e (Playwright vs API in E2E mode + web build)
                     ─► CodeQL, dependency audit, secret scan (parallel)
```

Target: under 12 minutes on GitHub-hosted Ubuntu runners.
