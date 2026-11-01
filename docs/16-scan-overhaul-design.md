# Core scan overhaul — technical design

For: [#334](https://github.com/coryfinnegan/CollectionMaxxing/issues/334).
Status: **committed**. Implementation is tracked in #336–#348 (see "Implementation
issues" below); implement those in dependency order, not this doc directly.

Grounded in: branch `muse/issue-334` at `origin/main` (HEAD `30acbe1`).
Code refs below are repo-relative paths on that tree.

## 0. Principles (non-negotiable)

1. **Shelf photos are drafts; barcode/cover matches are proof.** A spine read can
   propose; only an identifier or a confirmed match can assert an edition.
2. **Capture never poisons the record.** Every auto-commit is undoable; every
   unreadable spine is counted and findable, never silently dropped, never invented.
3. **Successes are never reviewed one by one.** Human eyes see exceptions only.
4. **No metric gaming.** Denominator = every captured segment. Weakening a check or
   dropping a scan to hit a number is a defect, not an optimization (§17–§18).

## 1. What exists today (the design is a delta, not a rewrite)

The current pipeline already matches the research direction in several places:

| Piece | State | Where |
|---|---|---|
| One scanner, no mode picker | EXISTS | `app/src/features/scan/scanner/ScannerRoute.tsx`; docs/02 §1; ADR-014 |
| Live multi-code barcodes + stability filter | EXISTS | `app/modules/cm-vision`, `tray/stability.ts` (2-of-4), 5 s tray cooldown |
| On-device read, text-only `identify` | EXISTS | `EngineSelector.ts` (Foundation Models → Vision fallback); `POST …/identify` refuses images (415) |
| Server grounding + pure scorer | EXISTS | `Application/Scanning/Identify/`, `Domain/Catalog/Matching/MatchScorer.cs` (Matched ≥ 0.90 + lead ≥ 0.08; Ambiguous ≥ 0.60) |
| Review screen with Ready/Pick/Not-found/Owned groups | EXISTS | `app/src/features/review/ReviewScreen.tsx`, `grouping.ts`, `plan.ts` |
| Commit + Undo, barcode learning loop | EXISTS | `CommitScanSession.cs`, `UndoScanCommit.cs`, `BarcodeLearner` |
| Background jobs + SSE + polling fallback | EXISTS | `Application/Jobs/`, `Application/Scanning/Events/ScanEventFeed.cs`, `changeSeq` cursor |
| Offline tray outbox + owned-index + crops on phone | EXISTS | MMKV stores, `snapStore.ts` (`cm-snaps/`, 7-day/50-snap), `offline/ownedCache.ts` |
| Session defaults on server | EXISTS (unwired in UI) | `ScanSession.DefaultLocationId/DefaultStatus` |
| Eval scorer + M3 gate + regression rule | EXISTS (baseline set 2026-10-31) | `Application/Evals/*`, `evals/baseline.json` (the clean 32-photo run of 2026-10-31, #608) |

Gaps the design must close (§2–§12 each name theirs): camera-first entry, session
context UI, auto-add tiers, Move action, barcode entry in review, cover snap,
segment counters, provisionals, edition upgrade, barcode-primary games/movies
under quota, honest baseline + review-burden metric.

## 2. Component diagram

```text
iPhone (Expo + VisionCamera v5, Apple-only, ADR-017)
│ ScanTab→Camera  SessionSheet  ReceiptBar  Review  EvalsRunner
│ VisionCameraSource ─┬─ live barcodes (AVFoundation metadata)
│                     └─ stills → ShotClassifier → AppleModelEngine ⇄ VisionPipelineEngine
│ Stores (MMKV): tray / pipeline / snapStore(crops) / owned-index / fingerprints
│ Transport: batched POST + Idempotency-Key, SSE w/ Last-Event-ID, ?since= fallback
▼
API (.NET 10, /api/v1/scan-sessions/*)
│ ScanningEndpoints (+ Identify/Artwork/Events subfolders, CandidatePhotoEndpoint)
│   → Create/Get/Barcodes/Identify/Art/Photo/Decide/Search/Commit/Undo/Abandon
│   (+ NEW: provisionals, crops, auto-commit)
│ IdentifyGrounding → MatchScorer → CandidateResolver (+OwnershipQuery)
│ JobQueue (in-proc, pg) → scan.resolve_candidate / scan.identify_resolve / safety jobs
│ ScanEventFeed (changeSeq SSE) → candidate/segment/session events
▼
Providers: OpenLibrary │ GoogleBooks(key) │ UPCitemdb(100/day) │ TMDB(key) │ IGDB(key)
Learned layer: ReleaseIdentifier rows (barcode→release, session-tagged, undoable)
```

(No server vision/OCR/AI anywhere — verified zero hits. That stays: ADR-017.)

## 3. One scanner, no modes + shot classification from framing

No mode picker exists today and none is added. New: the phone classifies each shot
from framing and records it, so engine choice, guidance, and evals can condition on it.

`ShotKind` (new enum, phone + server mirror): `Barcode | SingleCover | ShelfSegment
| LooseItem | Unknown`.

Classification signals (all on-device, cheap first):

| ShotKind | Signals |
|---|---|
| `Barcode` | ≥1 stable AVFoundation object (existing stability filter) — wins immediately |
| `SingleCover` | one dominant rectangle ≥ 45% frame area, aspect ≈ media cover/cart, or user tapped-to-focus a cover |
| `ShelfSegment` | ≥2 text rows or ≥2 spine-like rectangles, or auto-snap gate fired (existing gate: steady 0.7 s, sharp, text/rect ≥ 20% frame) |
| `LooseItem` | `SingleCover` geometry + session media hint is Game/Movie with no barcode after N frames |
| `Unknown` | none of the above → shutter still captures; server treats as `ShelfSegment` |

Rules: barcode-in-view always wins (current behavior, keep). Auto-snap fires only
when no barcode is in view (current gate, keep). `ShotKind` travels in the snap
payload and is stored on `ScanSnap`/`ScanCandidate`; eval truth gains an optional
`shotKind` for slicing (truth format stays backward compatible — unknown key ignored).

## 4. Scan opens to the camera; session context set once (CLZ Pre-fill)

**Change:** the Scan tab renders the camera immediately. Today's landing
(`ScanTab.tsx`: "Open scanner" / "Add it by hand" buttons) becomes a first-run
coach overlay + a compact session bar, not a gate.

Session context (new `SessionSheet`, one tap from the session bar, set once per
session, inherited by every capture):

- `libraryId` (from active library; switcher inline, existing `useActiveLibraryId`)
- `locationId` — default placement; remembers last-used per library (new MMKV key,
  e.g. `session-context-v1:{libraryId}`); "Not placed" allowed but explicit
- `status` — Owned (default) / Wishlist; maps to existing `DefaultStatus`
- `mediaHint` — Books / Games / Movies / Mixed (default Mixed); biases engine
  prompts, provider order, and fallback (c) copy
- `channel` — Home / In-store: Home inherits location; In-store sets a visible
  "decide location later" banner and Review defaults `addTo` to last home location

Server: `POST …/scan-sessions` already takes defaults (keep shape); add optional
`mediaHint` + `channel`. No new session entity — `ScanSession` already carries
`DefaultLocationId/DefaultStatus`.

Typed search ≤ 2 taps: search icon in the scanner chrome → `GET /api/v1/catalog/search`
scoped `libraryFirst` (existing endpoint; add optional `scope=library` bias so owned
answers render first). Offline: searches the owned-index + fingerprint matches.

## 5. Background processing, live counters, segment thumbnail strip

Captures queue and resolve while the user keeps shooting. Mostly composition of
existing machinery (tray outbox, pipeline store + 5 s resend, jobs, SSE), plus:

**Live counters** (new `SessionCounters` bar above the receipt; works offline from
local queue + last snapshot):

- `captured` — snaps + barcode lines in this session (local truth)
- `added` — auto-committed + committed items (server `CommittedItemIds` count + session auto-commit count, new)
- `toReview` — candidates in Ambiguous/NotFound/Error or unactioned provisionals
- `owned` — exact-owned hits this session (informational; duplicates protection, not a failure)

**Segment strip** (not in the app: it was built but never placed on a screen, and was
removed as unused; the live counters above are what shipped. The design is kept for
reference.) (new horizontal strip in shoot order; each cell = snap thumbnail
from `snapStore` + state dot):

```text
segment: Captured → Reading → Identifying → Resolved | NeedsEye → Reviewed
                                    └→ OfflineQueued (flush on reconnect)
dot:     grey      blue      blue          green      amber     hollow-green
```

Tap cell → snap detail pager (existing `PhotoViewer` "N of M" pager, extended with
per-item state + guidance). Strip is phone-local (thumbnails never upload except via
the provisional crop path in §10, which needs an ADR exception — see §14).

Retention interplay: `snapStore` 7-day/50-snap eviction must **pin** snaps with
unresolved segments/provisionals until resolved, committed, or explicitly discarded.

**Queue semantics:** flush order per session is barcodes first (fast, exact), then
identify payloads in shoot order; same `Idempotency-Key` retry as today; SSE
`session.counters` event carries the four counters + `changeSeq` so background tabs
and web stay consistent.

## 6. Confidence-tiered commit (incl. auto-add policy)

Tiers, evaluated server-side per candidate at resolve time (single place:
`CandidateResolver`, so jobs and inline resolve agree):

| Tier | Condition | Behavior |
|---|---|---|
| T0 barcode-proof | normalized identifier → exactly one release (learned `ReleaseIdentifier`, then providers), not exact-owned (or session says "copies") | **auto-add**: beep/haptic, receipt row, Undo 10 s; SSE `candidate.auto_committed` |
| T1 high-confidence spine/cover | `MatchScorer` Matched (≥ 0.90 + lead ≥ 0.08 — existing constants, unchanged), single-work agreement across signals, series-volume unambiguous (see §12) | **auto-add flagged `editionUnverified`** (new `Item.EditionVerified=false`; visible badge, filterable) |
| T2 review | multiple candidates, series-volume ambiguity, low OCR confidence, no match, unreadable segment, quota-pending | exceptions-only review queue; successes never listed one by one |

Auto-add writes real `Item` rows in the session transaction path (reuse commit
machinery; auto-committed ids append to `ScanSession.CommittedItemIds` so existing
`UndoScanCommit` semantics — "removes exactly these" — cover them). Undo surfaces
per event (snackbar) and per session (session screen "Undo last auto-add").

**Required decision (lead): amend ADR-014.** It currently says "nothing added
without Review → Add". Proposed amendment: T0/T1 auto-add is allowed because (a)
every auto-add is undoable in one tap, (b) T1 carries a persistent unverified-edition
flag, (c) auto-add precision is gated by eval (§17: `auto_add_precision`, FAIL line
below 99%). If the lead rejects the amendment, T0/T1 degrade to pre-checked review
rows — the rest of the design is unchanged.

Series-volume rule: ≥2 same-series candidates within the Ambiguous band, or OCR text
matching a series title with no volume numeral → T2 always (research: three series
volumes all matched vol. 1). No threshold tuning may override this; it is a veto,
pinned by a locked test.

## 7. Capture-time duplicate / owned answers (home + in-store)

Single definition stays: `OwnershipQuery` (exact copies Owned/Lent + soft same-work
different-shape). New presentation + actions on the owned card:

```text
"Already own — Hardcover, Living room shelf B"   [Add another copy] [Move it here] [Skip]
```

- **Add another copy**: existing semantics (copies+1 / new `Item` row per copy
  convention — keep whichever the commit path uses; do not invent a third).
- **Move it here**: NEW — sets the owned item's `LocationId` to the session location
  (reuse the item-update endpoint; one tap, undoable via the same snackbar).
- **Skip**: existing reject-and-drop; counts as `owned` in counters, never as a failure.
- Soft matches ("same book, different format?") render as informational line, default
  action Add, per docs/02 §7.

In-store = same scanner, `channel: InStore`: location inherited as "decide later",
owned answers work offline from the owned-index (`useOwnedLookup` server-first →
local exists; extend to answer fully offline with a "last synced …" line). The
duplicate re-scan test (DoD: zero silent second copies) is: re-scan an owned shelf;
PASS = every owned hit answered on-card, zero new `Item` rows without explicit
Add-copy.

## 8. Ordered fallbacks (a–e)

Evaluated in order per unresolved segment/item; the first applicable fires and is
recorded (`Candidate.GuidanceCode`) so evals can audit fallback usage.

**(a) Specific reshoot guidance, never a generic failure.** The classifier + engine
`FallbackReason` (existing `ScanSnap` enum — extend) maps to copy:

| Signal | Guidance |
|---|---|
| spine text height < ~24 px | "Move closer — spines too small" |
| ≥ N items but frame edges cut text | "Split the segment — shoot left/right halves" |
| blur/low-light meter | "Hold steady / add light" (existing sharpness gate) |
| glare band over text rows | "Tilt to kill the glare" |

**(b) Cover mode with candidate tap-to-pick.** `ShotKind.SingleCover` (or user taps
"shoot the cover" from a T2 row) → identify with `kind: cover` → server returns
top-3 with artwork → tap-to-pick binds the candidate (existing alternatives UI in
Review `pick` group; extend to scanner cards).

**(c) Per-item barcode queue inside review.** NEW: every T2 row gains "Scan barcode".
It opens the camera bound to that candidate (`candidateId`); a stable read posts to
`POST …/barcodes` with `{candidateId}` (endpoint extension); on resolve the row
upgrades in place (T0 path). This is also the games/movies primary path (§12).

**(d) Type-ahead title search, pre-filled and neighbour-constrained.** NEW: T2 row →
search sheet pre-filled with the OCR guess; query biased by segment neighbours
(same snap's agreed authors/series sent as `contextWorkIds[]` to a session-scoped
search — extend per-candidate `:search`, keep global `/api/v1/catalog/search` untouched).
Neighbour constraint is a *ranking bias*, never a filter (a filter could hide the
right answer and poison precision).

**(e) Provisional record from the crop itself.** NEW, last resort, always available:
"Keep as unverified" creates a `ProvisionalRecord` (§10): crop as cover, OCR-guess
title (marked guess), status Unverified, session location. **Invariant:
provisional-or-counted** — an unreadable spine ends as a provisional or stays a
visible T2 row; it is never silently dropped and its title is never invented
(`invented-titles` eval metric, §17, guards this).

## 9. Offline capture + deferred lookup and sync

Exists today: tray outbox with idempotency keys (barcode batches), pipeline
`phase/sent` persistence (snaps), owned-index delta sync, fingerprint local-match.
The design hardens, not rebuilds:

1. **Segment queue survives app kill** — already MMKV-backed; add a startup
   reconciliation pass (orphaned `reading` snaps → re-queue, never silently complete).
2. **Pin crops for unresolved work** — eviction skips snaps with segments not in
   `Resolved|Reviewed` (§5).
3. **Deferred lookup order** — on reconnect: barcode batches → identify payloads
   (shoot order) → art-scores → crop uploads. Quota-pending candidates stay Pending
   and are retried by existing `DrainQuotaBacklog` (daily) + session-reopen drain.
4. **Commit works from a mixed queue** — commit accepts server-resolved rows;
   still-offline rows stay queued with per-row state (existing Review per-row errors).
5. **Counters offline** — `captured`/`owned` from local data; `added`/`toReview`
   from last snapshot + local pending overlay, badged "offline".

No new sync protocol: SSE resume (`Last-Event-ID`) + `?since=` polling already cover
reconnect; tombstone deltas already cover ownership.

## 10. Segment crops linked to provisional records

Crops exist phone-side (`deviceCrops`, `snapStore` `{snapId}-{i}.jpg`). New:

- **Upload path (narrow):** `POST …/candidates/{cid}/crop` (multipart JPEG ≤ 1 MB,
  EXIF-stripped via existing `JpegSanitizer`) storing an `Asset` (reuse
  `AssetKind.ScanPhoto`) linked from the candidate/provisional. This is the only
  recognition-adjacent image upload besides "Use my photo".
- **Required decision (lead): ADR-015/017 exception.** "Photos stay on the phone"
  vs review-is-visual ("this crop → this entry: correct?"). Proposed: crops may
  upload **to the owner's self-hosted PC only**, only for provisionals/T2 rows the
  user keeps, never for auto-resolved successes, deleted with the provisional.
  If rejected: crops stay phone-only and provisionals are phone-visible-only until
  promoted — state that cost explicitly (no web review, lost on reinstall).
- **Review is visual:** every T2/provisional row shows its crop; confirm/skip acts
  on the pair, recorded in `match_feedback`.

## 11. Provisional records + edition-upgrade path

**ProvisionalRecord (new table `provisional_records`):** `Id, LibraryId, SessionId,
SnapId, CropAssetId?, TitleGuess(with IsGuess=true always), MediaHint, LocationId,
Status=Unverified, CandidateId?, CreatedAt, ResolvedAt?, ResolveAction`.
Findable: library filter "Needs attention"; counted in `toReview` and in eval
review burden (§17). Lifecycle: promote (→ `Item` via commit path, crop optionally
kept as user cover) / resolve-to-existing (link + delete) / delete. **Not** subject
to the 7-day session cleanup — provisionals persist until the user acts.

**Edition upgrade in place** (title-level → edition-accurate, no re-entry):

1. **Barcode pull-out (primary):** from item detail or T1/provisional row, "Scan
   barcode to verify edition" → (c)-style bound capture → identifier resolves to a
   release of the same work → swap release in place (extend `RematchItem` with
   `UpgradeFromBarcode`, preserving copies/location/notes/lending), set
   `EditionVerified=true`, teach `BarcodeLearner`. Different work → refuse with
   "barcode belongs to X" (never silently re-point).
2. **Cover + copyright page (no-barcode books):** new identify `kind:
   edition_proof` bound to the item; matched edition fields upgrade the release link
   the same way; mismatch → T2 row, never an overwrite.

## 12. Games / movies: barcode-primary, recognition only for no-barcode items

Current resolver already routes non-book barcodes UPCitemdb → `NameCleaner` →
IGDB/TMDB search → score. Design keeps that order and fixes its weak links:

- **Learned-first:** check `ReleaseIdentifier` (user's own learned layer) and the
  30-day provider cache before spending UPCitemdb quota — learned hits are free,
  exact, and edition-precise.
- **Quota-aware continuous queue:** the existing `ProviderQuota`/`TokenBucket`
  gates UPCitemdb (100/day trial). On exhaustion: keep capturing; codes resolve to
  barcode-only provisionals ("UPC … — lookup pending", still counted, still
  duplicate-checked by digits) and drain via `DrainQuotaBacklog`. The counters show
  a "quota paused — N pending" line, never a dead end. (No paid tier: repo rule —
  only the Apple Developer Program is paid. Quota pressure is relieved by the
  learned layer + `VerifyRelease` curation below, not by buying quota.)
- **Recognition reserved for no-barcode items** (loose carts, VHS, pre-ISBN books,
  bare discs — the existing `EvalKinds` cover these): `ShotKind.LooseItem` +
  mandatory confirmation (always T2, never T1 — no auto-add without an identifier
  for these kinds).
- **Curated UPC layer (long-term):** implement the stubbed `VerifyRelease`
  (promote a user-confirmed release to global) so the household's confirmations
  compound into the curated layer the research says disc/game ID needs. Until then,
  messy retailer strings are cleaned by `NameCleaner` + confirmation UI, never
  auto-committed as titles (T0 requires identifier→single-release, not string parse).

Series/volume veto (§6) applies to game editions and box sets too: "Game of the Year
vs base", "omnibus vs vol. 1" ambiguity → T2.

## 13. API changes (all under `/api/v1`; existing shapes keep working)

| Endpoint | Change |
|---|---|
| `POST /libraries/{lib}/scan-sessions` | += optional `mediaHint`, `channel`, `autoCommitTiers` (default `["T0"]`; T1 opt-in per session until §17 gate passes) |
| `GET …/scan-sessions/{id}[?since=]` | += `counters{captured,added,toReview,owned}`, `segments[]` (snap states), provisionals |
| SSE `…/events` | += `session.counters`, `candidate.auto_committed`, `provisional.created/updated` |
| `POST …/barcodes` | += optional `candidateId` binding (fallback (c), §11 upgrade) |
| `POST …/identify` | += `shotKind`, `segmentId`, `kind` (`shelf\|cover\|edition_proof`), `engineReading` version |
| `POST …/candidates/{cid}:search` | += optional `contextWorkIds[]` ranking bias (fallback (d)) |
| `POST …/candidates/{cid}/crop` | NEW — crop upload ≤ 1 MB (§10) |
| `POST …/provisionals` / `…/provisionals/{pid}:promote\|:resolve\|:delete` | NEW — provisional lifecycle (§11) |
| `PATCH /items/{id}` (existing update) | reused for Move-it-here (location-only patch, undoable) |
| `POST …/items/{id}:rematch` | += `upgradeFromBarcode` mode (§11) |
| `GET /api/v1/catalog/search` | += optional `scope=library` bias (scanner search ≤ 2 taps) |
| `POST /api/admin/evals/runs` etc. | += new metrics in report (§17); shapes additive |

Auth: scan routes keep existing owner-404 + role filters; crop/provisional routes
are Editor (same as decide/commit); eval routes stay Admin.

## 14. Data-model changes

| Table/entity | Change |
|---|---|
| `ScanSession` | += `MediaHint`, `Channel`, `AutoCommitTiers[]`, `AutoCommittedItemIds[]` (or flag on `CommittedItemIds` entries) |
| `ScanCandidate` | += `ShotKind`, `SegmentId`, `GuidanceCode`, `Tier` (T0/T1/T2), `ProvisionalId?` |
| `ScanSnap` | += `ShotKind`; extend `FallbackReason` with (a) codes |
| `provisional_records` | NEW entity (§11); excluded from session 7-day cleanup |
| `Item` | += `EditionVerified bool` (default true; backfill true; T1 sets false) |
| `Asset` | reuse `ScanPhoto` for crops; crop rows cascade-delete with provisional |
| `ReleaseIdentifier` | unchanged (learned layer already session-tagged + undoable) |

Migration notes: all additive except none; `EditionVerified` backfill is a data
migration in the same PR as T1 (contract-change rule: deliberate + same-PR).

## 15. State machines

**Candidate lifecycle** (server is the authority; phone mirrors for UI):

```text
Pending ─┬─ resolve ─┬─ T0 ── auto-add ──► Committed(auto) ──undo──► Open*
         │           ├─ T1 ── auto-add ──► Committed(unverified) ──undo──► Open*
         │           └─ T2 ──► NeedsEye ─┬─ accept/pick ──► Committed
         │                              ├─ provisional ──► Provisional ──► Committed | Deleted
         │                              └─ skip ──► Skipped
         └─ quota/error ──► Pending(quota) ── drain ──► (resolve again; never Error-swallowed)
* undo returns the candidate to NeedsEye with its evidence intact, not to Pending.
```

**Rules:** a person's explicit decision always wins (existing rule, keep); artwork
re-score never overrides a decision (existing, keep); auto-add never fires on
exact-owned unless the session opted into copies (T0 condition, §6).

## 16. Thresholds (concrete, pinned)

| Knob | Value | Notes |
|---|---|---|
| Barcode stability | 2-of-4 frames, 5 s tray cooldown | existing, unchanged |
| `MatchScorer` Matched / lead / Ambiguous | ≥ 0.90 / ≥ 0.08 / ≥ 0.60 | existing constants, unchanged |
| T1 auto-add | Matched + single-work + volume-unambiguous + not exact-owned | §6 |
| Series-volume veto | → T2 always | veto, locked test, §6 |
| LooseItem/no-barcode kinds | → T2 always (never T1) | §12 |
| Auto-snap gate | steady 0.7 s, sharp, text/rect ≥ 20% frame, no barcode in view | existing, unchanged |
| `autoCommitTiers` default | `["T0"]`; T1 requires session opt-in until the §17 gate passes on real data | rollout guard |

Any change to this table is a deliberate contract change: same-PR baseline note +
re-run of the home acceptance scenario (§17).

## 17. Eval harness: make the baseline real + home acceptance as DoD

**Baseline (set 2026-10-31, #608):** `evals/baseline.json` holds the metrics and
threshold hash of the owner's clean 32-photo run of 2026-10-31, so
`EvalRegression.Check()` now compares every run against it and the noise-aware
regression rule engages: a rate (a percentage) may move the wrong way by 2 points,
and on the rows built on the on-device model (`apple_model`, `fm_text`), which answer
differently every run, by two standard errors of the number of items it is measured
over if that is more (a baseline of exactly 0 or 100 uses a smoothed boundary,
`1/(n+2)` or `(n+1)/(n+2)`); the Vision row and the combined row rerun to the same
numbers and keep 2 points; a time is a regression only past 2 x the baseline; raw
counts (tier counts, provisionals, unreconciled, excess unreadable) are not judged;
`duplicate_clean` keeps the 2 points. Two clean runs of the same build had differed by
more than a flat 2 points on 23 of 255 metrics, all noise. Before the baseline it was `"metrics": {}`
and every run reported `no_baseline`. **Still open from the original gap:** no review-burden metric exists. The
real-golden rescore test (`RescoreLatestRunTests`, `RUN_EVALS=1`) never runs in CI,
and the manually-triggered eval workflow anticipated by docs/09 §"AI evals" does
not exist. Golden photos/truth live only on the owner's PC (`Evals:GoldenPath`).

**To make it real (all required before the overhaul can claim PASS):**

1. **First real baseline run.** On the owner's PC: run the golden set through the
   phone runner (`app/src/app/evals.tsx`), submit via `POST /api/admin/evals/runs`,
   verify the dated aggregate + `latest-run.json`, then copy its `metrics` object
   into `evals/baseline.json` per the file's own note. From that commit on,
   `no_baseline` must never reappear silently — a run reporting it fails CI loudly.
2. **New metrics** (extend `EvalScorer`/`SliceMetrics`/`EvalSummaryModels`,
   additive; regression names via `EvalRegression.Flatten`):
   - `review_rate` — fraction of ground-truth items needing any human touch
     (T2 rows + provisionals + corrections of auto-adds). THE review-burden metric.
   - `auto_add_precision` — precision over T0+T1 auto-commits only. Guards "zero
     silent false positives". Gate bar: ≥ 99% (FAIL below; this is stricter than the
     overall 95% because no human saw these).
   - `duplicate_clean` — duplicate re-scan test result: re-scan owned shelf →
     `true` iff zero new `Item` rows without explicit Add-copy AND every owned hit
     answered on-card.
   - `time_to_first_results`, `total_time_to_trustworthy` — wall-clock from first
     capture to first/last trustworthy record (audited, not self-reported: measured
     by the harness run timestamps).
   - Per-tier counts (`t0_auto`, `t1_auto`, `t2_review`, `provisional`) for slice
     analysis; `shotKind` slices once §3 lands.
3. **Anti-gaming rules (locked tests):**
   - Denominator = every captured segment/snap in the run. Unreadable counts
     (`ShelfTruth.unreadable` exists — keep and require it to reconcile: reported
     unreadable + resolved + provisional == captured).
   - `invented_pct` stays a lower-is-better gate (< 1%, existing M3 bar); a
     provisional with a guessed title must carry `IsGuess` (else it scores as
     invented).
   - Partial runs can never pass or become baselines (existing `PartialRunTests`
     behavior — extend to the new metrics).
   - Threshold changes (§16) require a same-PR baseline note + acceptance re-run;
     the regression check treats an un-annotated threshold move as a failure
     (implement as: thresholds hashed into the run manifest; hash change without
     `baselineNote` → `incomplete`).
4. **Manual eval workflow (lead-owned `.github/`):** add the dispatch workflow
   docs/09 anticipates — runs scoring/gate/regression tests + (with `RUN_EVALS=1`
   on a self-hosted runner or documented PC procedure) the rescore; required check
   for prompt/model/scorer/threshold PRs.

**Home acceptance scenario (DoD for the overhaul, from #334):** one uncatalogued
real shelf; ground-truth list written FIRST (`<id>.truth.json` in the golden set);
N = 25–40 including a ≥3-volume series, an omnibus/box set, a worn spine.
Measure: recall, precision, time-to-first-results, total-time-to-trustworthy vs the
barcode pace for the same shelf (~8–15 s/item), review burden (`review_rate`), and
the duplicate re-scan test. Lines:

- PASS: recall ≥ 90%, precision ≥ 95%, review burden ≤ 25%, faster than barcode, duplicates clean.
- FAIL: recall < 75%, precision < 90%, or any silent false positive
  (`auto_add_precision` < 100% on the acceptance run fails even if overall precision passes).

## 18. Rollout + open questions for the lead

Rollout: ship T0 auto-add first (behind nothing — barcode + unique hit is already
exact), T1 behind per-session opt-in until the §17 gate passes on real data, then
default-on. `EditionVerified` backfill rides the T1 PR. Crop upload rides the ADR
decision below.

Decisions needed (lead; each blocks the marked issue):

1. **Amend ADR-014** to allow T0/T1 auto-add with Undo (§6)? (Blocks auto-add.)
2. **ADR-015/017 narrow exception** for provisional/T2 crop upload to the
   self-hosted PC (§10)? (Blocks visual review of provisionals.)
3. `VerifyRelease` scope: minimal promote-flow now, or defer curation? (§12)
4. Provisional retention: persist-until-acted (proposed) vs TTL?
5. Confirm no web recognition parity work (ADR-017 iPhone-only stands).

Out of scope (per #334): visual/design-system work, pricing/valuation, and anything
that weakens a check or drops a scan to hit a metric.

## Implementation issues

Filed from this design, in dependency order (each links back here and to #334).
"Blocked on" = needs a lead decision from §18 before work starts.

| # | Issue | Design | Depends on |
|---|---|---|---|
| #336 | Session context — CLZ Pre-fill | §4 | none (first) |
| #337 | Scan tab opens to the camera | §4 | #336 |
| #338 | Shot classification (`ShotKind`) | §3 | none (parallel with #336–#337) |
| #339 | Barcode-primary capture + per-item barcode queue | §12, §8(c) | #336 |
| #340 | Owned/duplicate answers, home + in-store | §7 | #336 |
| #341 | T0 barcode auto-add + Undo | §6, §15, §16 | #339, #340; blocked on decision 1 (ADR-014) |
| #342 | Live counters + segment strip + offline hardening | §5, §9 | #338 |
| #343 | T1 high-confidence auto-add, flagged edition-unverified | §6, §14, §16 | #341, #342; blocked on decision 1 (ADR-014) |
| #344 | Exceptions-only review + fallbacks (a)(b)(d) | §8 | #338, #342 |
| #345 | Provisional records + segment-crop linkage | §10, §11, §8(e) | #342, #344; blocked on decision 2 (ADR-015/017) |
| #346 | Edition-upgrade path | §11 | #339, #343, #345 |
| #347 | Honest eval harness + real baseline | §17 | #341, #343, #345 |
| #348 | Home acceptance run — DoD sign-off | §17 | all of #336–#347 |

## Appendix: grounding index

App: `app/src/app/(tabs)/scan.tsx`, `app/src/features/scan/ScanTab.tsx`,
`app/src/app/scanner.tsx`, `scanner/ScannerRoute.tsx`, `CameraPreview.tsx`,
`app/modules/cm-vision/*`, `tray/stability.ts`, `tray.ts`, `autosnap/*`,
`recognition/EngineSelector.ts`, `pipeline/SnapPipeline.ts`, `pipelineApi.ts`,
`sessionApi.ts`, `pipelineStore.ts`, `snapStore.ts`, `pictureFiles.ts`,
`offline/ownedCache.ts`, `offline/sync.ts`, `fingerprints/*`, `cards/*`,
`app/src/app/review.tsx`, `features/review/*`, `app/src/app/result/[id].tsx`,
`detail/*`, `app/src/app/evals.tsx`, `app/src/features/evals/*`.

API/Application: `src/CollectionMaxxing.Api/Scanning/ScanningEndpoints.cs`
(+ `Identify/`/`Artwork/`/`Events/` subfolders, `CandidatePhotoEndpoint.cs`;
`ScanSessionRoutes.cs` is only the route group + owner/access filters)
(Create/Get/Events/Barcodes/Identify/Art/Photo/Decide/Search/Commit/Undo/Abandon),
`Api/Recognition/RecognitionEndpoints.cs`, `Api/Evals/EvalsEndpoints.cs`,
`Application/Scanning/Identify/*` (`IdentifyGrounding`, `MatchScorer` use),
`Application/Scanning/*Resolve*` (`BarcodeResolver`, `CandidateResolver`),
`Application/Ownership/OwnershipQuery.cs`, `Application/Jobs/*`,
`Application/Items/RematchItem.cs`, `Application/Catalog/Providers/*`
(`ICatalogProvider`, `BookLookup`), `Application/Evals/*` (`EvalScorer`,
`M3Gate`, `EvalRegression`, `EvalTruth`, `EvalKinds`), `Application/Exports/*`.

Infrastructure: `Infrastructure/Providers/*` (OpenLibrary, GoogleBooks,
UpcItemDb+`NameCleaner`, Tmdb, Igdb, `HttpCatalogProvider`, `ProviderQuotaStore`,
`TokenBucketRateLimiter`), `Infrastructure/Jobs/*` (`JobWorker`,
`RecurringJobScheduler`, `ScanFollowUpServices`),
`Infrastructure/Evals/FileEvalResults.cs`,
`Infrastructure/Recognition/RecognitionConfigStore.cs`,
`Infrastructure/Catalog/LocalCatalogSearch.cs`.

Domain: `Domain/Catalog/{Work,Release,ReleaseIdentifier,Asset,Platform}.cs`,
`Domain/Identifiers/*`, `Domain/Items/{Item,ItemFeaturePrint,ItemTombstone,Tag}.cs`,
`Domain/Locations/*`, `Domain/Libraries/*`, `Domain/Scanning/{ScanSession,
ScanCandidate,ScanSnap}.cs`, `Domain/Catalog/Matching/*` (`MatchScorer`,
`ScoringProfiles`, `TitleNormalizer`, `PlatformNames`).

Tests/evals: `tests/CollectionMaxxing.Recognition.Evals/*`
(`ScoringMathsTests`, `GateAndRegressionTests`, `DraftTruthTests`,
`PartialRunTests`, `TimeoutScoringTests`, `RescoreLatestRunTests`,
`ResultsFolderIgnoreTests`), `tests/CollectionMaxxing.Api.Tests/Scanning/*`,
`tests/CollectionMaxxing.Api.Tests.Library/Evals/*`, `tests/…/Domain.Tests/Matching/*`,
`evals/baseline.json`, `scripts/eval-truth-stub.ps1`.

Docs/ADRs: `docs/02-scanning-and-recognition.md` (§1–§8, §10),
`docs/03-data-model.md`, `docs/06-ux-and-wireframes.md` (M4–M6, copy rules),
`docs/09-testing-and-tdd.md` (AI evals), ADR-006/009/010/014/015/016/017/018/024/026/027.

