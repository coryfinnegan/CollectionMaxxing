# Load check (k6)

Evidence for the M5 line "20 concurrent scanners for 10 minutes, p95 under 500 ms, while a game is running". Scripts live in `scripts/perf/`.

## What it does

`Run-Perf.ps1` builds (once) the repo's own API and migration images, then starts a **throwaway stack on a private Docker network**: Postgres on tmpfs, the migrations, the API (Production environment, throwaway keys) and a `grafana/k6` container. Nothing is published to the host, the network is `--internal` (no way out, so no provider is ever called), and the script has no URL parameter, so it cannot be pointed at the live stack. Every container is named `cm-perf-*` and labelled `cm.agent=qa-2a`. Limits: API 2 CPU / 1 GB, database 1 CPU / 512 MB, k6 2 CPU. (`deploy/docker-compose.yml` sets no limits; these are the ones the PC needs while a game runs.)

The seed step signs people up through the API's own flows (a fake Auth0 that issues test tokens, first-call account creation, library creation, invites), so no test code is in the product. Each virtual scanner is one signed-in account.

One scanner: opens a scan session; every 2 to 4 s sends a barcode batch (1 to 5 codes) or an identify (1 to 3 items); polls `GET /scan-sessions/{id}?since=` for results (k6 cannot hold an SSE stream; polling is the app's fallback); an ownership snapshot every 20 s; a commit about once a minute (then a new session); library browsing and catalogue search at human speed. The mix stays inside the doc 08 rate limits (`tests.ps1` proves the arithmetic against the limits read from `SecurityOptions.cs`). A 429 is counted separately, not as an error, and is a finding.

Two settings of the stack differ from production, both only for the seed and the provider stubs: the sign-in limits (`Auth__RateLimit__PermitLimit`, `Security__Limits__AuthHourly__Permits`) are widened because every account is created from one address, and the providers' host names point at the container itself so their calls are refused at once (with a short retry and no client-side provider rate limit). The limits the scanners live under (default, search, barcodes, identify) are the shipped ones.

## Profiles

| Profile | Scanners | Accounts | Length | Hard cap |
|---|---|---|---|---|
| `smoke` (default) | 2 | 2 | 30 s | 6 min |
| `gaming` | 5 | 2 | 3 min | 10 min |
| `full` | 20 | 5 | 10 min (1 min ramp) | 20 min |

Thresholds: p95 of API calls under 500 ms, errors under 1%. Abort rules (the run stops itself): p95 over 2 s for 30 s, or errors over 10%. `-MaxMinutes` overrides the hard cap.

## Running it

```powershell
# from the repo root, in PowerShell 7
pwsh -NoProfile -File scripts/perf/Run-Perf.ps1 -BuildOnly                       # once, before the game: builds the images
pwsh -NoProfile -File scripts/perf/Run-Perf.ps1 -DryRun -Profile full -ConfirmFull   # prints the plan, starts nothing
pwsh -NoProfile -File scripts/perf/Run-Perf.ps1 -Profile gaming                  # 3 minutes
pwsh -NoProfile -File scripts/perf/Run-Perf.ps1 -Profile full -ConfirmFull       # the Checkpoint run: 10 minutes
```

`full` never starts without both `-Profile full` and `-ConfirmFull`. The first build downloads base images and takes minutes; do it before starting the game (`-BuildOnly`).

### While a game is on

1. Run `-BuildOnly` ahead of time. Start the game and get into a typical scene.
2. Start the `full` command in a terminal (alt-tab, or a second monitor).
3. Watch the game. If it stutters badly, stop the run (below) and write that down: that is also a result.
4. At the end it prints the summary and `stopped`.

### Stopping

- **Ctrl+C** in the runner window, or
- from any other window: `pwsh -NoProfile -File scripts/perf/Stop-Perf.ps1`

Both remove exactly the containers and networks labelled `cm.agent=qa-2a` and print `stopped`. Anything else (Plex, Jellyfin, the live stack) is never touched. `Stop-Perf.ps1 -RemoveImages` also removes the images the runner built.

## Output

A short summary is printed and written as JSON to `%TEMP%\collectionmaxxing-perf\<run>\summary.json` (with `k6-summary.json` and the API's last log lines `api.log`): scanners, requests, p50/p95/p99 per call type, errors, 429s, and CPU and memory samples of the API, database and k6 from `docker stats`. Nothing from a run goes into the repo. Exit codes: 0 passed, 1 refused or failed to start, 2 thresholds failed, 3 aborted by an abort rule, 4 stopped or k6 failed.

## What to write down (owner, at the Checkpoint)

| Date | Profile | Game | p95 (ms) | Errors | 429s | Aborted? | Did the game stutter? | Notes |
|---|---|---|---|---|---|---|---|---|
| | full | | | | | | | |

## Tests

`pwsh -NoProfile -File scripts/perf/tests.ps1` (needs Docker for one test; `-SkipDocker` leaves it out).
