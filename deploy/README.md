# Deploying CollectionMaxxing on the PC

The PC **pulls**: a small deployer checks GitHub for a new `main` image every 2 minutes, proves this repo built it, and swaps it in. Nothing pushes to the PC and there is no self-hosted runner (`docs/10-hosting-and-devops.md`, ADR-022).

Everything here was written and tested **without touching the real machine**. Every script is a dry run unless you pass `-Apply`, and prints the exact commands it would run. **Nobody has run the real install yet: it is an owner checkpoint.** Follow the checklist below on the PC, in order.

| Folder | What is in it |
|---|---|
| `docker-compose.yml`, `.env.example` | The production stack, bound to `127.0.0.1` only |
| `deployer/` | `Deploy-CollectionMaxxing.ps1` (the deployer), `Install-Deployer.ps1` / `Uninstall-Deployer.ps1` (the scheduled task), shared `Cm.*.ps1` |
| `apple/` | `New-SigningRequest.ps1` and `Publish-SigningSecrets.ps1`: the TestFlight signing setup and its yearly renewal (ADR-024, [runbooks/iphone-app-testflight.md](runbooks/iphone-app-testflight.md)) |
| `tailscale/` | `Set-TailscaleServe.ps1` |
| `backup/` | The backup image: `Dockerfile.backup`, `backup.sh`, `backup-lib.sh` (dump, files archive, manifest, retention, heartbeat) |
| `runbooks/`, `drills/` | Short "what do I do when..." runbooks (start at `runbooks/README.md`) and the restore drill log template |
| `RESTORE.md` | The runbook for the restore drill (twice a year) and for a real restore. The drill itself is `scripts/restore-drill.ps1` / `scripts/restore-drill.sh` |
| `docker/` | `Dockerfile.api`, `Dockerfile.migrate` |
| `proposed/release.yml` | The CI workflow that builds and signs the images (the lead copies it to `.github/workflows/`) |
| `tests/` | `pwsh -NoProfile -File deploy/tests/Run-Tests.ps1` runs everything and prints one summary line |

Ports: the API is `127.0.0.1:5480` and the Aspire dashboard `127.0.0.1:18888`. Ports 8080 (SABnzbd), 8409 (ErsatzTV), 32400 (Plex) and 8096 (Jellyfin) belong to other apps; the deployer refuses to start a stack that uses them or that binds anything but `127.0.0.1`. Postgres is not published at all.

## From merge to running (the delivery path)

What happens after a merge to `main`, in order, and what each step needs from the world:

1. **CI builds and signs** (`.github/workflows/release.yml`, "Release images", about 4-5 minutes). Three images land on GHCR tagged with the full commit SHA, each with a build attestation; then the moving `:main` tag jumps onto all three — but only after every leg AND its attestation succeeded. Needs: GitHub Actions running. When the run fails, `:main` stays where it was and the deployer never sees the commit.
2. **The deployer polls** (the `CollectionMaxxingDeployer` scheduled task, every 2 minutes). It pulls `api:main`, reads the commit SHA from the image's `org.opencontainers.image.revision` label, and stops when that is the commit already running. Needs: the PC on, you signed in, Docker Desktop running. The task does not run while you are signed out, and nothing queues up for it — the first poll after you sign in just picks up whatever `:main` is then.
3. **The deployer proves and swaps** ("How the deployer decides" below): ancestry (the new commit must descend from the running one), one attestation per image (the certificate must say this repo built it from `main` at that commit), the expand-only migration, `up -d`, then the smoke test, the service check and the running-version check (the api container's revision label must equal the deployed commit). A refusal or a failure is an `ALERT` line in `logs\deployer.log` — and, only when `CM_HEARTBEAT_URL` is set in `.env`, a failure email. **That URL is not set today, so a stuck deployer is silent outside this log file.**
4. **Delivery is verified against the running server**, not the registry: `GET http://127.0.0.1:5480/health/version` reports the commit the api is running (`CM_BUILD_SHA`, set from the deployed tag). A server-side fix is delivered when that endpoint reports the merge commit (or a newer one) — the rule proposal is `docs/proposed-server-delivery-rule-491.md`.

Manual steps, honestly listed: the one-time install checklist below; keeping the PC on, signed in and Docker running (steps 2-3 only happen then); setting `CM_HEARTBEAT_URL` if you want failure emails. There is no manual pull, ever: at the start of every poll the deployer fast-forwards its own clone to `origin/main` when the clone is clean and fast-forwardable (a dirty, diverged or off-`main` clone is left alone with a `WARN` line until it is fixed), so a deployer or compose fix self-delivers within one poll cycle and the next run loads it. Last-good stays solely in `state\deployer-state.json` (the rollback anchor), never in the clone position.

## Before you start (nothing here changes anything)

- [ ] **Docker Desktop** is updated (24.0.7 is from 2023) and set to start when you sign in. Windows: Settings > Accounts > Sign-in options > "Use my sign-in info to automatically finish setting up after an update".
- [ ] **GitHub CLI** is version 2.49 or newer (the first with `gh attestation verify`). Check with `gh --version`; update with `winget upgrade GitHub.cli` (needs admin), or, without admin, unzip the official `gh_<version>_windows_amd64.zip` from [cli/cli releases](https://github.com/cli/cli/releases) to `C:\CollectionMaxxing\tools\gh`. The deployer puts `tools\gh\bin` first on its own PATH when it is there.
- [ ] **`gh auth status`** says you are logged in (the attestation check needs a token; read-only is enough). If not, run `gh auth login` yourself.
- [ ] **PowerShell 7**: `pwsh --version` prints 7.x.
- [ ] **Tailscale** is signed in: `tailscale status`.
- [ ] **The lead has merged** `deploy/proposed/release.yml` as `.github/workflows/release.yml`, a push to `main` has run it, and the three packages (`collectionmaxxing-api`, `-migrate`, `-backup`) are public on GHCR. Until then there is nothing to deploy.
- [ ] **WSL2 memory cap** (so Docker never squeezes Plex or ErsatzTV): put this in `%UserProfile%\.wslconfig`, then restart Docker Desktop. This is your edit; no script touches it.
  ```
  [wsl2]
  memory=4GB
  ```

## Owner install checklist

Every step says what to run, what you should see, and how to undo it. If anything looks different, stop and ask.

### 1. Get the scripts

```powershell
New-Item -ItemType Directory C:\CollectionMaxxing
git clone https://github.com/coryfinnegan/CollectionMaxxing C:\CollectionMaxxing\repo
```
Expect: `Cloning into ...` and no errors. **Undo:** delete `C:\CollectionMaxxing`.
The deploy scripts follow `main` by themselves: at the start of every poll the deployer runs `git -C C:\CollectionMaxxing\repo fetch origin main` plus `git merge --ff-only origin/main`, so the next run loads the scripts and compose file of the latest `main`. It never resets, checks out or forces anything: if the clone has uncommitted changes to tracked files, is not on `main`, or cannot fast-forward, it leaves it alone and logs a `WARN` line (no alert); fix the clone and the next poll catches up. There is no manual pull, ever. The *app* updates through images, and last-good stays solely in `state\deployer-state.json`.

### 2. Create `.env`

```powershell
Copy-Item C:\CollectionMaxxing\repo\deploy\.env.example C:\CollectionMaxxing\.env
# a database password: copy this output into POSTGRES_PASSWORD
[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
notepad C:\CollectionMaxxing\.env
```
Fill in `POSTGRES_PASSWORD`, `CM_PUBLIC_ORIGIN` (the address people browse to, for example `https://pc.tailnet.ts.net`), `CM_SECRETS_DIR`, `CM_BACKUP_DIR` (a folder on **F:**, not C:), `CM_TZ` and, if you want email alerts, `CM_HEARTBEAT_URL` (step 9). `CM_GOOGLE_BOOKS_API_KEY` is optional: paste a Google Books API key to add a second source for book lookups (leave it empty to use Open Library alone). The Windows environment variable `Providers__GoogleBooks__ApiKey` does **not** reach the containers; only this `.env` line does. After changing it, recreate the API: `$env:CM_TAG='<sha>'; docker compose -p collectionmaxxing -f C:\CollectionMaxxingepo\deploy\docker-compose.yml --env-file C:\CollectionMaxxing\.env up -d api` (`<sha>` is `current.sha` in `C:\CollectionMaxxing\state\deployer-state.json`). Leave the ports alone. `.env` is never committed. **Undo:** delete the file.

### 3. Set up Auth0 sign-in

Sign-in is Auth0's (docs/07, docs/14). There are no token keys to make. Follow \"What you do\" in `docs/14-auth0-migration.md`, then run `deploy/auth0/Apply-Auth0.ps1` (a dry run first, then `-Apply`). It writes `C:\CollectionMaxxing\secrets\auth0\api.env`. Copy its `CM_AUTH0_*` lines into `.env`: all six Auth0 values are required, and the stack won't start without them. Make the secrets folder readable only by your account (folder Properties > Security) and make sure BitLocker covers that drive. **Undo:** remove the lines from `.env`.

### 4. Dry run of the deployer (read-only)

```powershell
pwsh -NoProfile -File C:\CollectionMaxxing\repo\deploy\deployer\Deploy-CollectionMaxxing.ps1
```
Expect: `DRY RUN: nothing will be changed`, no `config problem:` lines, then the list of commands a real run makes (`docker pull`, `gh attestation verify`, `compose run --rm migrate`, `compose up -d api backup aspire-dashboard`, a `curl` of `/health/ready`, and a `compose ps` check that every started service is up). If you see `config problem:` read it: a forbidden port, a non-loopback bind, or a missing `.env` value.

### 5. First deploy, by hand

```powershell
pwsh -NoProfile -File C:\CollectionMaxxing\repo\deploy\deployer\Deploy-CollectionMaxxing.ps1 -Apply
```
Expect, in `C:\CollectionMaxxing\logs\deployer.log` and on screen: `deploying <12 hex> (attestations verified)`, then `deployed <12 hex>; smoke test passed`. Exit code 0 (`$LASTEXITCODE`). Check:

```powershell
Invoke-RestMethod http://127.0.0.1:5480/health/ready      # Healthy
docker compose -p collectionmaxxing ps                     # postgres, api, aspire-dashboard, backup running; migrate exited 0
```
Open `http://127.0.0.1:18888` for logs, traces and metrics. There is no setup code: the first sign-in creates an account, and people create or join libraries in the app. Support commands (recovery code, developer-tools role) are in [docs/07](../docs/07-authentication-and-authorization.md).

If a step fails, the log says why: no `gh` login or an image that is not attested = nothing deployed; a failed migration = the old api keeps running; a failed smoke test or service check = the previous good version is pinned and restarted.
**Undo:** `docker compose -p collectionmaxxing -f C:\CollectionMaxxing\repo\deploy\docker-compose.yml --env-file C:\CollectionMaxxing\.env stop` (stops without deleting data; **never** use `down -v`, it deletes the database volume).

### 6. Tailscale Serve (HTTPS from your phone)

```powershell
tailscale serve status                                                   # expect: "No serve config"
pwsh -NoProfile -File C:\CollectionMaxxing\repo\deploy\tailscale\Set-TailscaleServe.ps1          # dry run
pwsh -NoProfile -File C:\CollectionMaxxing\repo\deploy\tailscale\Set-TailscaleServe.ps1 -Apply
```
Expect the dry run to print `tailscale serve --bg --https=443 http://127.0.0.1:5480`. The script refuses (exit 4) if Serve already has a configuration that is not exactly ours, or has Funnel on, so it cannot break anything you already publish. Then open `https://<your-pc>.<tailnet>.ts.net/health/ready` from the iPhone with Tailscale on.
**Undo:** `tailscale serve --https=443 off`.

### 7. Start the deployer every 2 minutes

```powershell
pwsh -NoProfile -File C:\CollectionMaxxing\repo\deploy\deployer\Install-Deployer.ps1            # dry run
pwsh -NoProfile -File C:\CollectionMaxxing\repo\deploy\deployer\Install-Deployer.ps1 -Apply
schtasks /Query /TN CollectionMaxxingDeployer
```
The only thing it registers is `schtasks /Create /TN CollectionMaxxingDeployer /SC MINUTE /MO 2 /TR "conhost.exe --headless pwsh ... Deploy-CollectionMaxxing.ps1 -Root C:\CollectionMaxxing -Apply" /RL LIMITED`: a user-level task, no admin, no SYSTEM account. It runs while you are signed in, like Docker Desktop. `conhost --headless` keeps a console window from flashing up every 2 minutes. Runs never overlap (a second one exits at once; if the lock has been held for over 30 minutes the run instead exits 1 and alerts, since the holder looks stuck — the lock itself is never broken). It refuses to replace an existing task of that name.
**Undo:** `pwsh -NoProfile -File C:\CollectionMaxxing\repo\deploy\deployer\Uninstall-Deployer.ps1 -Apply` (or `schtasks /Delete /TN CollectionMaxxingDeployer /F`).

### 8. Check the first backup

```powershell
docker compose -p collectionmaxxing -f C:\CollectionMaxxing\repo\deploy\docker-compose.yml --env-file C:\CollectionMaxxing\.env exec backup /opt/cm/backup.sh once
Get-ChildItem F:\Backups\CollectionMaxxing -Recurse
```
(`exec` here needs `CM_TAG` set; take it from `C:\CollectionMaxxing\state\deployer-state.json` > `current.sha`, and run `$env:CM_TAG='<sha>'` first.) Expect `database dumped to db/collectionmaxxing-<time>.dump` and `files archived to files/files-<time>.tar.gz`, and those two files on F:. Nightly runs happen at `CM_BACKUP_SCHEDULE` (default 03:00 in `CM_TZ`). Retention is 14 daily, 8 weekly and 12 monthly; a failed night never deletes anything.

Each dump has a **manifest** next to it, `db/collectionmaxxing-<time>.manifest.json`: the row count of every table (read just before and just after the dump), the newest migration, the sha256 of the dump and of the files archive, and a sha256 list of every file in the archive. Counts, hashes, file names and times only: no row data, no file content. The restore drill compares a restored backup against it. A run that cannot write the manifest counts as failed (and pings `/fail`); the manifest is pruned together with its dump.
**Undo:** delete the files in the backup folder.

### 9. Heartbeat (an email when a night is missed)

Create a free check at healthchecks.io with a 1-day period and a grace of a few hours, put its ping URL in `CM_HEARTBEAT_URL` in `.env`, then recreate the backup container: `$env:CM_TAG='<sha>'; docker compose -p collectionmaxxing -f ... --env-file ... up -d backup`. A good night pings the URL, a failed dump pings `<url>/fail`, an empty URL pings nothing.

### 10. The iPhone app (TestFlight)

The iPhone app is no longer served from this PC: it comes from TestFlight (ADR-024, set up with [runbooks/iphone-app-testflight.md](runbooks/iphone-app-testflight.md)). The deployer used to copy each release's `.ipa` into the files volume; on a server that still has one, the next run (with or without a deploy) removes `ipa/` from the files volume with a throwaway `docker run --rm` of the backup image, deletes `state\ipa-stage`, and drops `ipa` / `ipaAttempt` from `state\deployer-state.json`. A failure is a `WARN`, never an alert, and the next run tries again.
**Undo:** nothing to undo: the file is no longer used by anything. The signing helpers for TestFlight are in `deploy/apple/`.

### 11. Optional: off-site copy (off by default)

Nothing here is set up. When you want it (docs/10): create a free Backblaze B2 bucket or use OneDrive, then `rclone config` (an `crypt` remote on top), and schedule `rclone copy F:\Backups\CollectionMaxxing <crypt-remote>:` weekly with a normal Windows scheduled task you create yourself. Keep the rclone password somewhere safe: without it the copy cannot be read.

## Everything the real install runs

| Step | Commands that change the machine |
|---|---|
| 1 | `git clone` into `C:\CollectionMaxxing\repo` |
| 2 | Copies `.env.example` to `.env` (you edit it) |
| 3 | `Apply-Auth0.ps1 -Apply`: changes your Auth0 tenant, and writes the files in `secretsuth0\` |
| 5, and every 2 minutes after | `git -C ...\repo fetch origin main` plus `git merge --ff-only origin/main` at poll start (after read-only `git status` / `git symbolic-ref`; skipped with a `WARN` when the clone is dirty, off `main` or diverged) and, only when a new image appeared, a second fetch plus read-only `git cat-file` / `git merge-base --is-ancestor`; `docker pull` of the three images; `gh attestation verify` (every local digest, until one verifies); a second `docker pull` of `api:main` plus `image inspect` (the revision must not have moved); `docker compose run --rm migrate`; `docker compose up -d api backup aspire-dashboard`; `curl` of `/health/ready`; `docker compose ps` (every started service must be up); `docker compose ps -q api` plus `docker inspect` (the running api must report the deployed commit); writes `state\deployer-state.json` and `logs\deployer.log*`; once, on a server that still has the retired `.ipa`, `docker run --rm ... sh -c "rm -rf /data/files/ipa"` |
| 6 | `tailscale serve --bg --https=443 http://127.0.0.1:5480` |
| 7 | `schtasks /Create ...` (one task) |
| 8 | `backup.sh once` inside the backup container |

It never runs `docker system prune`, `docker volume prune`, `docker rm` on anything else, `docker compose down -v`, anything that edits the registry, PATH, system or WSL settings, or anything that binds a port other than `127.0.0.1:5480` and `127.0.0.1:18888`.

## How the deployer decides (for review)

1. Every poll starts by fast-forwarding the deployer's own clone to `origin/main` when it is clean and fast-forwardable (otherwise a `WARN` line and the poll continues), so deployer and compose fixes self-deliver with no manual pull, ever. Then `docker pull` of `...collectionmaxxing-api:main`; its `org.opencontainers.image.revision` label is the commit SHA.
2. Same SHA as the running one: stop. A SHA that already failed here: skip until a newer one appears. Then the **ancestry guard**: `git -C C:\CollectionMaxxing\repo fetch origin main`, then `git merge-base --is-ancestor <running sha> <new sha>`. The new commit must be a strict descendant of the running one. An older commit, a diverged branch, a commit the clone has never seen, a running commit the clone does not know (or a state file that cannot be read), or a failed fetch all **refuse the deploy** (exit 4). Build times and run numbers play no part. The first ever deploy has nothing to compare with.
3. Pull `api`, `migrate` and `backup` at that SHA. For each, `gh attestation verify oci://...@sha256:<digest> --repo coryfinnegan/CollectionMaxxing --signer-workflow coryfinnegan/CollectionMaxxing/.github/workflows/release.yml`, **and** the certificate must say `refs/heads/main` (and the same commit). A pulled image can carry more than one local digest (the `:main` index digest sorts next to the `:sha` manifest digest, and only the manifest one is attested), so every digest is tried and the certificate decides; an unattested envelope never blocks an attested build (#491). Any failure: log it, do nothing. Then `:main` is pulled once more: when its revision moved while the images were checked, the stale commit is left for the next run.
4. `docker compose run --rm migrate` (expand-only migrations, so the running api stays compatible). Failure: the api is not touched.
5. `docker compose up -d api backup aspire-dashboard` with `CM_TAG=<sha>`, then up to 12 smoke tests of `http://127.0.0.1:5480/health/ready` 5 seconds apart, then `docker compose ps --format json` until every started service (`api`, `backup`, `aspire-dashboard`) is running (and healthy where it has a healthcheck): a crash-looping service fails the deploy like a failed smoke test. Then the running-version check: the api container's `org.opencontainers.image.revision` label must equal the deployed commit (read with `docker compose ps -q api` plus `docker inspect`), or the previous good version is pinned again like any other failed deploy.
6. Success: record the SHA as last good in `state\deployer-state.json` (and, once, remove the retired `.ipa`). Failure: remember the SHA as bad, `up -d` with the previous good tag, and check the previous version is healthy again. The clone already tracks `origin/main` from step 1; it is never the rollback anchor.

State lives in `C:\CollectionMaxxing\state`, logs in `C:\CollectionMaxxing\logs` (1 MB each, 5 kept). The log never contains the database password or the heartbeat URL.

**If the state file is lost** (deleted, or its path is wrong) while the stack is running, or `.env` / the compose file pins a `CM_TAG`, the deployer refuses (exit 4, ALERT) instead of treating it as a first deploy, because it could not tell an older commit from a newer one. Seed the state by hand with the commit the running api was built from:

```powershell
$env:CM_TAG = 'x'   # any value; compose needs it set to read the stack
$sha = docker inspect (docker compose -p collectionmaxxing -f C:\CollectionMaxxing\repo\deploy\docker-compose.yml --env-file C:\CollectionMaxxing\.env ps -q api) --format '{{index .Config.Labels "org.opencontainers.image.revision"}}'
New-Item -ItemType Directory -Force C:\CollectionMaxxing\state | Out-Null
@{ current = @{ sha = $sha.Trim() } } | ConvertTo-Json | Set-Content C:\CollectionMaxxing\state\deployer-state.json
```
The sha must be 40 lowercase hex characters. A true first deploy (no state, nothing running, no pinned tag) is unaffected.

**Exit codes:** 0 fine or nothing to do (also "already up to date", "known bad, waiting for a newer one", and a dry run), 1 deploy failed or rolled back (migration failed, smoke test or service check failed, stale lock, crashed), 2 config refused, 3 Docker or GHCR not reachable, 4 refused (not a descendant, not attested, unreadable state). (5 was the retired `.ipa` sync.)

**Refusals are loud.** Every non-zero result of a real (`-Apply`) run writes a line starting `ALERT deploy <outcome>` to `deployer.log` and pings `<CM_HEARTBEAT_URL>/fail` with the reason as the body: the same free healthchecks.io check the nightly backup uses, so you get the same email. (A deploy alert also marks that check "down" until the next good backup ping; that is the price of using one channel.) With no heartbeat URL the log line is the only alert. A failed ping never changes the exit code. The poller repeats the ping every 2 minutes while the cause persists; healthchecks only emails on the change.

**Rolling back on purpose** is the one way to go to an older commit, and it is separate from the poller:

```powershell
pwsh -NoProfile -File C:\CollectionMaxxing\repo\deploy\deployer\Deploy-CollectionMaxxing.ps1 -RollbackTo <40-char sha>          # dry run: prints the plan
pwsh -NoProfile -File C:\CollectionMaxxing\repo\deploy\deployer\Deploy-CollectionMaxxing.ps1 -RollbackTo <40-char sha> -Apply
```
The target must be an **older commit of the running one** (a newer one is a normal deploy; a diverged or unknown one is refused). Its three images get the same attestation proof as any deploy. No migration runs (they are expand-only). If the older version fails its smoke test, the version that was running is pinned again and the run exits 1. On success the commit you rolled back from is remembered as bad, so the poller does not put it straight back; a newer commit on main deploys normally.

## Changing the code

Run `pwsh -NoProfile -File deploy/tests/Run-Tests.ps1` (about a minute and a half; needs Git for Windows' bash, or Linux). Most of it needs no Docker daemon; the restore-drill suite starts real throwaway Postgres containers (`cm-ops1-test-*`, no network, no ports, no volumes, removed afterwards) and needs Docker running and the `postgres:17` image, and `-SkipDocker` leaves it out. `-DockerBuild` also builds the three Dockerfiles as `cm-ops1-test-*` images and removes only those tags. Tests never touch the real Docker, GitHub, Tailscale or Task Scheduler: every external command goes through `Invoke-Cm`, which the tests replace with a recorder.
