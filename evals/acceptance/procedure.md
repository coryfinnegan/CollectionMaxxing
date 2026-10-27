# Home acceptance run: procedures (issue #348, prep)

Design: `docs/16-scan-overhaul-design.md` §17. The run itself needs the owner's
shelf and stays `needs:cory`; this file prepares everything around it.

## 0. Ground truth first (proof built in)

Real titles never enter the repo. Truth files live on the PC beside it
(docs/09: `C:\CollectionMaxxing\evals\golden\shelf\`, `CM_EVALS_DIR` moves it).

1. Pick one uncatalogued shelf: N = 25–40 items, including a ≥3-volume series,
   an omnibus/box set, and a worn spine.
2. Shoot the shelf photo(s): close, well-lit, steady, one segment per photo.
   Name them `accept-shelf-01.jpg`, `accept-shelf-02.jpg`, …
3. Copy `evals/acceptance/shelf-truth.template.json` to
   `<photo-id>.truth.json` next to each photo and fill every slot from the
   spines — before the phone runs anything.
4. Seal it: `pwsh scripts/Write-EvalTruthManifest.ps1 -ShelfFolder
   <golden>\shelf -PhotoId accept-shelf-01[, …] -Out
   evals/acceptance/<date>-truth-manifest.json`. The script refuses
   placeholders, enforces N = 25–40 and the composition, and writes photo ids
   with SHA-256 hashes (no titles — safe to commit).
5. Commit the manifest. Its commit predating the run's dated aggregate is the
   proof ground truth came first; file mtimes are the backup.
6. After the run, re-run the script with `-Verify` against the manifest: it
   fails if any truth file changed since sealing.

## 1. Shelf-pass procedure

1. On the PC: `Evals:Enabled=true`, `Evals:GoldenPath` at the golden folder,
   `Evals:ResultsPath` writable, `Evals:BaselinePath` at
   `evals/baseline.json` (docs/09 "First real baseline").
2. Confirm the manifest commit from §0 is pushed.
3. On the iPhone (dev build): open the hidden eval runner
   (`app/src/app/evals.tsx`, **Run evals**). It reads every golden photo
   through both engines and uploads to `POST /api/admin/evals/runs`.
   Leave **Baseline note** empty unless a threshold moved (§16 table).
4. The runner sends per-item tier/provisional/`IsGuess`/`segmentId` and
   per-photo `capturedCount`/`capturedAt` (#397); it never touches the
   library, so it sends no duplicate evidence — that is §3.
5. Verify the dated aggregate in the results folder:
   `<date>-<sha>.json` plus `latest.json`, `latest-run.json` (titles, PC
   only) and `report.md` (PC only).

## 2. Barcode pace baseline

The same shelf, timed with a stopwatch, before or after §1 (not during):

1. Open the real scanner and scan each shelf item's barcode once, in shelf
   order, at a natural pace. Reference pace: ~8–15 s/item.
2. Record started/finished wall-clock, N, and total seconds in
   `<results>\acceptance-barcode-baseline.json`:
   `{"date":…, "photoIds":[…], "items": N, "totalSeconds": S,
   "secondsPerItem": S/N, "method": "stopwatch, one barcode pass"}`.
   No titles — the aggregate may be quoted in the report.
3. The PASS comparison is manual: the harness reports
   `total_time_to_trustworthy` (seconds) in the dated aggregate; PASS needs
   it under the barcode total for the same shelf.

## 3. Duplicate re-scan test

Needs the shelf committed to the library first (the §1 shelf pass, reviewed
and committed through the real scanner):

1. Re-scan the same shelf with the real scanner.
2. Count: owned hits, how many were answered on-card
   ("Already own — …" with Add another copy / Move it here / Skip), and new
   `Item` rows that appeared without an explicit Add-copy.
3. PASS needs zero silent second copies: every owned hit answered on-card,
   zero unsolicited adds.
4. Evidence upload (the phone runner cannot send this): from the PC, POST one
   evidence-only run to `POST /api/admin/evals/runs` (owner admin token,
   `Content-Type: application/json`), one result per acceptance photo —
   strict shape, no undeclared members:

```json
{
  "device": {"model": "iPhone", "osVersion": "26", "appVersion": "dev"},
  "configVersion": "<same configVersion the shelf-pass run used>",
  "results": [
    {
      "photoId": "accept-shelf-01",
      "engine": "apple_model",
      "outcome": "ok",
      "thermalState": "nominal",
      "latencyMs": 0,
      "duplicateRescan": {"ownedHits": 30, "answeredOnCard": 30, "unsolicitedAdds": 0}
    }
  ]
}
```

   Read `duplicate_clean` from that run's dated aggregate (100 = clean);
   disregard its other metrics (empty reads score nothing by design).

## 4. Lines (read from harness output, never hand-computed)

From the shelf-pass aggregate (`apple_model.shelf.*`) plus the §3 aggregate:

- PASS: recall ≥ 90%, precision ≥ 95%, review burden (`review_rate`) ≤ 25%,
  faster than the §2 barcode total, duplicates clean.
- FAIL: recall < 75%, precision < 90%, or any silent false positive
  (`auto_add_precision` < 100% fails even if overall precision passes).
- PASS → update `evals/baseline.json` with the dated aggregate's `metrics`
  and `thresholdHash`, close #348 with the report linked. FAIL → file one
  follow-up per failing dimension with owner-visible repro steps.

## 5. Where the harness output goes

- PC results folder: dated aggregate `<date>-<sha>.json`, `latest.json`,
  `latest-run.json`, `report.md` (docs/09 "How it runs").
- Repo: only the dated aggregate may be copied to `evals/results/`
  (`.gitignore` + `ResultsFolderIgnoreTests` enforce this); the manifest from
  §0 lives at `evals/acceptance/<date>-truth-manifest.json`.
- Re-score any time without the phone:
  `RUN_EVALS=1 dotnet test tests/CollectionMaxxing.Recognition.Evals
  --filter Category=Eval` with `Evals__GoldenPath` / `Evals__ResultsPath` set
  (`RescoreLatestRunTests`).
