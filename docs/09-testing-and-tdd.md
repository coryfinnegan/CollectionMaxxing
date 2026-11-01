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
| Integration | `Infrastructure.Tests` | ~15% | seconds | EF mappings + queries on real Postgres, migrations, provider clients against WireMock fixtures, the photo and file stores |
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
| Mutation testing | The sampler (`scripts/mutate.ps1`) on Domain (nightly, report only). *Stryker still can't drive xUnit v3 here (`tests/mutation/README.md`), so verifiers hand-mutate key rules until it can.* | — |
| Coverage | coverlet + ReportGenerator | Jest `--coverage` |
| Components | — | React Native Testing Library |
| E2E | — | **Maestro** (iOS simulator on a GitHub macOS runner, release tags only, because it's slow), **Playwright** (web) |

## Quality gates

| Gate | Threshold | Where |
|---|---|---|
| Build | 0 warnings (`TreatWarningsAsErrors`), `dotnet format --verify-no-changes`, eslint + prettier, `tsc --noEmit` | every PR |
| Unit + integration + acceptance | all green | every PR |
| Line coverage | Not gated: nothing measures coverage in CI or the check script. Domain ≥ 95% and Application ≥ 90% stay the aspiration. | — |
| **Mutation score** | Not gated: the nightly sweep (`mutation.yml`) samples Domain mutants and reports the survivors; it never fails the build. | nightly, report only |
| Authorization matrix + every-endpoint-has-policy | all green | every PR |
| OpenAPI | snapshot approved; `oasdiff breaking` clean | every PR |
| Generated TS client | committed and up to date | every PR |
| Web E2E (Playwright) | green against the ephemeral CI stack | every PR to `main` |
| iOS E2E (Maestro) | green | release tags (GitHub macOS runner) |
| AI eval | no rate falls more than 2 points vs. baseline (more on the on-device model rows, by their sample size), no time passes 2 x the baseline, raw counts not judged | before any prompt/model/scorer change merges (manually triggered workflow) |

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
- Each photo gets a `truth.json`: an ordered list of `{ title, media_type, platform?, year? }`, plus how many items are visible but unreadable. A truth names an item as it reads on the spine or box ("The Example Cookbook"), but a provider often answers with the full title and subtitle. An item may list those answers in `alsoTitled` (up to 10 strings; `also_titled` works too), and a release titled with any of them counts as the item. An unlisted subtitle is still a miss, so a look-alike product that shares the main title is never accepted by accident. A single-item truth takes the same field.
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

**On-phone eval runner (no Mac needed):** the Apple model and Apple Vision only run on Apple hardware, and development happens on Windows. So dev builds include a hidden **Run evals** screen. It runs the golden set through the three engines on your iPhone and sends each photo's results (engine, items, latency, refusals, thermal state) to the API as soon as that photo is read, where the eval report is built. This is how the M3 decision gate is measured, and how we re-check after each iOS release.

**How it runs:** tap **Run evals** on your iPhone (dev build), then **Start the run**. The run lives on the PC, not on the phone, so you can switch apps, lock the phone or let iOS end the app, and pick up where you left off:

1. **Opening.** The phone opens a run (`POST /api/admin/evals/runs/open`: device, config version, optional baseline note). The PC answers with a run id and the golden photos in reading order. There is at most one open run. When the screen opens it asks the PC for it (`GET /api/admin/evals/runs/open`); if one is open it shows "N of M photos read" with **Continue** and **Start over** (which asks once, then discards the run: `DELETE /api/admin/evals/runs/open`).
2. **One photo at a time.** For each photo the PC does not have yet, the phone downloads it, reads it with the three engines, and sends that photo's results in one small request (`PUT /api/admin/evals/runs/open/photos/{photoId}`, JSON, no picture). The PC answers as soon as the results are stored; it grounds nothing inside the request. Sending a photo again replaces it, so a retry after a lost answer is harmless. The same per-photo checks as the one-shot route apply, and a photo that fails them is refused alone: the run goes on without it and the score lists it as skipped.
3. **If a send fails** the photo's results stay on the phone (MMKV) and are sent again before the next photo is read, with a wait that grows from one second to thirty. A PC that does not answer, or answers 502, 503 or 504 as it does while the API redeploys, is waited for with no limit (the screen says so); a photo's download is waited for the same way, and a photo is skipped only when the PC says it is not there (404). Only a refusal about the photo itself (400, or 413 for one too big) leaves a photo out, so one bad photo never holds the run up. If the PC cannot take anything right now (signed out, evals off, results folder not writable, five plain 500s in a row) the run stops with every reading still kept on the phone and says so; **Check again**, then **Continue**, sends them first and reads nothing twice.
4. **Leaving the app** pauses the run. A photo that was being read when the app went to the background is read again from the start when it comes back, and what that read produced is dropped: a read cut short by the background is never recorded as an engine error or timeout. Coming back resumes by itself unless you had paused. If iOS ended the app, opening the screen finds the open run on the PC and offers **Continue**; nothing already on the PC is read again. The screen is held awake only while it is actively reading in the foreground.
5. **Grounding in the background.** The PC grounds each stored photo as it arrives, with the same code as `/identify` and one grounding memo per run, acting as the person who opened the run with the libraries they belong to when the photo is grounded (as every job does), so a release a library made for itself is seen as it is by the one-shot route and the scanner. The queue is the run folder itself (`open-run/` under `Evals:ResultsPath`: a photo stored without a grounded file is waiting), served by a small hosted service rather than the database job queue, because the evals keep no database rows. A photo stored before an API restart is grounded after it. The folder holds real titles: owner only, never in the repo.
6. **Finishing.** After the last photo the phone asks the PC to finish (`POST /api/admin/evals/runs/open/finish`) and returns at once. **Stop and score the N read so far** finishes early and marks the run partial, as cancelling always did (a run finished with photos missing, or with a photo the PC gave up grounding, is partial too and never becomes a baseline). The PC scores once every stored photo is grounded, exactly as the one-shot route does (`EvalReport.Evaluate`, same result files), as one run with one result file. The screen says the PC is scoring and that you can leave it; the gate summary shows when it is ready (`GET /runs/open` says `done`, and `GET /runs/latest` still reads the latest summary).
7. **Re-scoring after a deploy, with no phone.** Most recognition fixes change only the PC's half of a run (the combination, kinds, matching). So when a run is scored, by either route, the PC also keeps what the phone read (the accepted results of every photo, the checks' notes, and the run's device, config version, note, partial flag and opener) as `latest-inputs.json` beside `latest-run.json`: real titles, PC only, replaced by the next run. When the API starts as another server version than the one that made `latest.json` (`buildSha`), and no run is open, the worker grounds those readings again with this version's code (a fresh memo, acting as the run's opener) and scores them exactly as a phone run is scored. The result is a new run with the current sha and the usual files (dated file, `latest.json`, `latest-run.json`, `report.md`), no draft truth files, and `regroundedFrom` (the original run id and when it was read) in its summary; `report.md` opens with a line saying so. The regression check against `evals/baseline.json` runs as usual, so a PC-side fix is measured minutes after it deploys. It decides once per start, a failure is logged by the error's type and not retried until the next start, a run opened meanwhile stops it and goes ahead, and `Evals:RegroundOnStart=false` turns the automatic step off. Dropping an empty `reground.request` file in `Evals:ResultsPath` asks for one now, even with the version unchanged (the worker deletes the file when it starts; it waits while a run is open). A phone-side fix (prompts, the on-device engines) still needs a phone run.

`POST /api/admin/evals/runs` (the whole run in one request) still works unchanged for app builds that predate the open run. `dotnet test tests/CollectionMaxxing.Recognition.Evals --filter Category=Eval` with `RUN_EVALS=1` re-scores the latest run against the truth files and writes `evals/results/<date>-<git-sha>.json` for trend tracking. A run costs **$0**: everything runs on your phone and your PC.

**Where the golden set lives:** `C:\CollectionMaxxing\evals\golden\shelf\` and `...\golden\single\` on the PC (`CM_EVALS_DIR` in `.env` moves it), beside the repo clone and never inside it. Photos can be added from the iPhone too: the eval runner's **Add test photos** card sends each original at full size to `POST /api/admin/evals/golden/{set}/{id}` (platform admin only), which saves it straight into `shelf/` or `single/`, with no detour through email, which shrinks photos. Each photo has an `<id>.truth.json`; `scripts/eval-truth-stub.ps1 <folder>` makes empty skeletons, and a run writes `<id>.truth.draft.json` drafts to correct and rename. Results and `report.md` land in `C:\CollectionMaxxing\evals\results\`. Only the dated aggregate file may be copied into the repo's `evals/results/` (`.gitignore` blocks the rest, because they hold real titles).

**First real baseline (on the owner's PC):** running the golden set once on real photos turns the regression check on: from that commit on, `no_baseline` is a loud CI failure, not a silent skip.

1. On the PC, enable the eval server: `Evals:Enabled=true`, `Evals:GoldenPath` at the golden folder (`shelf/`, `single/`, truth files), `Evals:ResultsPath` at a writable results folder, `Evals:BaselinePath` at this repo's `evals/baseline.json`.
2. On the iPhone (dev build), open the hidden eval runner (`app/src/app/evals.tsx`, **Run evals**). It runs every golden photo through the three engines and sends each photo's results to the open run on the PC (`PUT /api/admin/evals/runs/open/photos/{photoId}`), which scores once when the phone finishes.
3. Verify the dated aggregate in the results folder: `evals/results/<date>-<sha>.json`, plus `latest.json`, `latest-run.json` (grounded run, titles — PC only) and `report.md` (PC only).
4. Copy the dated file's `metrics` object **and** its `thresholdHash` into `evals/baseline.json`. Aggregate numbers only — no titles, no photo names.
5. Commit. The next rescore compares against the baseline; anything but `ok` fails loudly.

Re-baseline (same steps) whenever the golden set changes, and whenever a threshold move lands: same PR carries the baseline note plus the acceptance re-run, whose upload sets `baselineNote`.

Until the phone runner sends the honest fields (per-item tier, provisional / `IsGuess`, per-photo `capturedCount` / `capturedAt`, `duplicateRescan`), runs report the new metrics as no-data (`review_rate` reads 100%: unmeasured work fails closed as burden). Sending those fields is follow-up work, needed before the home acceptance run (issue #348).

**Eval workflow:** `.github/workflows/evals.yml` (manual trigger). Job 1 scores the maths on GitHub-hosted runners; Job 2 re-scores the real goldens on the self-hosted PC runner when dispatched with `rescore: true` (until that runner exists, run the PC procedure above by hand). Suggested required check for PRs touching prompts, models, scorers or thresholds (`src/**/Matching/**`, `src/**/Scanning/**`, `src/**/Evals/**`, `app/src/features/evals/**`, `evals/baseline.json`) — wiring that into branch protection is an owner repo-settings step.

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
