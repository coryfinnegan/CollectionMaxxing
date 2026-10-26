# First real baseline + eval workflow spec (issue #347, docs/16 §17)

The lead owns `.github/` and `docs/`; this file carries the procedure and the
workflow spec so the lead can wire them up. Suggested final home for the
procedure: docs/09-testing-and-tdd.md, "AI evals".

## First real baseline (on the owner's PC)

Running the golden set once on real photos turns the regression check on: from
that commit on, `no_baseline` is a loud CI failure, not a silent skip.

1. On the PC, enable the eval server: `Evals:Enabled=true`,
   `Evals:GoldenPath` at the golden folder (`shelf/`, `single/`, truth files),
   `Evals:ResultsPath` at a writable results folder, `Evals:BaselinePath` at
   this repo's `evals/baseline.json`.
2. On the iPhone (dev build), open the hidden eval runner
   (`app/src/app/evals.tsx`, **Run evals**). It runs every golden photo through
   both engines and uploads per-photo results to
   `POST /api/admin/evals/runs`.
3. Verify the dated aggregate in the results folder:
   `evals/results/<date>-<sha>.json`, plus `latest.json`, `latest-run.json`
   (grounded run, titles — PC only) and `report.md` (PC only).
4. Copy the dated file's `metrics` object **and** its `thresholdHash` into
   `evals/baseline.json`. Aggregate numbers only — no titles, no photo names.
5. Commit. The next rescore compares against the baseline; anything but `ok`
   fails loudly.

Re-baseline (same steps) whenever the golden set changes, and whenever a
threshold move lands: same PR carries the baseline note plus the acceptance
re-run, whose upload sets `baselineNote`.

Until the phone runner sends the honest fields (per-item tier, provisional /
`IsGuess`, per-photo `capturedCount` / `capturedAt`, `duplicateRescan`), runs
report the new metrics as no-data (`review_rate` reads 100%: unmeasured work
fails closed as burden). Sending those fields is follow-up work, needed before
the home acceptance run (issue #348).

## Manual eval workflow spec (for the lead to file under `.github/`)

What docs/09 anticipates: a manually-triggered workflow that scores,
gates and regression-checks, plus the real-golden rescore.

- Name: `evals.yml`. Trigger: `workflow_dispatch` (inputs: none required).
  Required check for PRs touching prompts, models, scorers or thresholds
  (suggested paths: `src/**/Matching/**`, `src/**/Scanning/**`,
  `src/**/Evals/**`, `app/src/features/evals/**`, `evals/baseline.json`).
- Job 1 — scoring, gate, regression maths (GitHub-hosted, no secrets):
  `dotnet test tests/CollectionMaxxing.Recognition.Evals`
  (excludes `Category=Eval` by default) plus the eval API tests. Fails on any
  red test: the pinned maths, the anti-gaming locked tests and the gate bars.
- Job 2 — real-golden rescore (self-hosted runner on the PC, or the PC
  procedure above run by hand and its result attached): with `RUN_EVALS=1`,
  `Evals__GoldenPath`, `Evals__ResultsPath` and `Evals__BaselinePath` set,
  `dotnet test tests/CollectionMaxxing.Recognition.Evals --filter Category=Eval`.
  The rescore asserts the regression status is exactly `ok`: `no_baseline`,
  `no_overlap`, `partial_run`, `regressed` and threshold-`incomplete` all fail
  loudly. The PC's photos, truth files and reports never leave the PC; only
  the pass/fail (and, on re-baseline, the aggregate `metrics` +
  `thresholdHash`) comes back.
- Secrets: none. Cost: $0 (phone + PC).
