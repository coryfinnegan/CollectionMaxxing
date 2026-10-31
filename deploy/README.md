# Deploying CollectionMaxxing on the PC

The PC **pulls**: a small deployer checks GitHub for a new `main` image every 2 minutes, proves this repo built it, and swaps it in. Nothing pushes to the PC and there is no self-hosted runner (`docs/10-hosting-and-devops.md`, ADR-022).

Everything here was written and tested **without touching the real machine**. Every script is a dry run unless you pass `-Apply`, and prints the exact commands it would run. **Nobody has run the real install yet: it is an owner checkpoint.** Follow the checklist below on the PC, in order.

| Folder | What is in it |
|---|---|
| `docker-compose.yml`, `.env.example` | The production stack, bound to `127.0.0.1` only |
| `deployer/` | `Deploy-CollectionMaxxing.ps1` (the deployer), `Install-Deployer.ps1` / `Uninstall-Deployer.ps1` (the scheduled task), shared `Cm.*.ps1` |
| `apple/` | `New-SigningRequest.ps1` and `Publish-SigningSecrets.ps1`: the TestFlight signing setup and its yearly renewal (ADR-024, [runbooks/iphone-app-testflight.md](runbooks/iphone-app-testflight.md)) |
| `tailscale/` | `Set-TailscaleServe.ps1` |
| `agent/` | The issue agent: `Watch-AgentIssues.ps1` (the scheduled watcher), `Run-AgentIssue.ps1` (one run), `agent-prompt.md` (what the agent is told), `Install-AgentWatcher.ps1` ([The issue agent](#the-issue-agent-optional)) |
| `backup/` | The backup image: `Dockerfile.backup`, `backup.sh`, `backup-lib.sh` (dump, files archive, manifest, retention, heartbeat) |
| `runbooks/`, `drills/` | Short "what do I do when..." runbooks (start at `runbooks/README.md`) and the restore drill log template |
| `RESTORE.md` | The runbook for the restore drill (twice a year) and for a real restore. The drill itself is `scripts/restore-drill.ps1` / `scripts/restore-drill.sh` |
| `docker/` | `Dockerfile.api`, `Dockerfile.migrate` |
| `proposed/release.yml` | The CI workflow that builds and signs the images (the lead copies it to `.github/workflows/`) |
| `tests/` | `pwsh -NoProfile -File deploy/tests/Run-Tests.ps1` runs everything and prints one summary line |

Ports: the API is `127.0.0.1:5480` and the Aspire dashboard `127.0.0.1:18888`. Ports 8080 (SABnzbd), 8409 (ErsatzTV), 32400 (Plex) and 8096 (Jellyfin) belong to other apps; the deployer refuses to start a stack that uses them or that binds anything but `127.0.0.1`. Postgres is not published at all. The same config guard (exit 2, before anything is pulled, dumped or migrated, and for the release's own compose file too) also refuses host networking, privileged containers, `cap_add`, `pid: host` / `ipc: host`, host devices, the Docker socket or engine pipe, a named volume that is secretly a host bind, and any **bind mount of a host folder outside an allow-list**: the `CM_BACKUP_DIR`, `CM_SECRETS_DIR`, `CM_RECOGNITION_DIR` and `CM_EVALS_DIR` folders from `.env`, the clone's own `deploy` and `evals` folders, and the `evals` folder beside the clone. Nothing else, so not `C:\CollectionMaxxing` itself (it holds `.env`) and not the home folder. The messages name the service and the container path, never a host path. A release that needs a new mount changes the guard in the same pull request (`Get-CmAllowedMountRoots` in `deploy/deployer/Cm.Common.ps1`); the poll that first sees both is refused once and the next poll, now running the new guard, accepts it.

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
Fill in `POSTGRES_PASSWORD`, `CM_PUBLIC_ORIGIN` (the address people browse to, for example `https://pc.tailnet.ts.net`), `CM_SECRETS_DIR`, `CM_BACKUP_DIR` (a folder on **F:**, not C:), `CM_TZ` and, if you want email alerts, `CM_HEARTBEAT_URL` (step 9). `CM_GOOGLE_BOOKS_API_KEY` is optional: paste a Google Books API key to add a second source for book lookups (leave it empty to use Open Library alone). Games and movies need theirs: `CM_IGDB_CLIENT_ID` and `CM_IGDB_CLIENT_SECRET` (a Twitch app at dev.twitch.tv/console) and `CM_TMDB_API_KEY` (the API Read Access Token at themoviedb.org/settings/api); without them every game or movie picture finds nothing, silently. Windows environment variables such as `Providers__GoogleBooks__ApiKey` do **not** reach the containers; only these `.env` lines do. After changing one, recreate the API (the next deploy does it too): `$env:CM_TAG='<sha>'; docker compose -p collectionmaxxing -f C:\CollectionMaxxing\repo\deploy\docker-compose.yml --env-file C:\CollectionMaxxing\.env up -d api` (`<sha>` is `current.sha` in `C:\CollectionMaxxing\state\deployer-state.json`). Leave the ports alone. `.env` is never committed. **Undo:** delete the file.

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

### 12. Optional: the issue agent

Only if you want GitHub issues to turn into pull requests without a session of yours. Nothing runs until you do this; see [The issue agent](#the-issue-agent-optional) for what it does, its limits and its switch.

```powershell
pwsh -NoProfile -File C:\CollectionMaxxing\repo\deploy\agent\Install-AgentWatcher.ps1            # dry run
pwsh -NoProfile -File C:\CollectionMaxxing\repo\deploy\agent\Install-AgentWatcher.ps1 -Apply
```
**Undo:** `pwsh -NoProfile -File C:\CollectionMaxxing\repo\deploy\agent\Install-AgentWatcher.ps1 -Uninstall -Apply`.

## Everything the real install runs

| Step | Commands that change the machine |
|---|---|
| 1 | `git clone` into `C:\CollectionMaxxing\repo` |
| 2 | Copies `.env.example` to `.env` (you edit it) |
| 3 | `Apply-Auth0.ps1 -Apply`: changes your Auth0 tenant, and writes the files in `secrets\auth0\` |
| 5, and every 2 minutes after | `git -C ...\repo fetch origin main` plus `git merge --ff-only origin/main` at poll start (after read-only `git status` / `git symbolic-ref`; skipped with a `WARN` when the clone is dirty, off `main` or diverged) and, only when a new image appeared, a second fetch plus read-only `git cat-file` / `git merge-base --is-ancestor`; `docker pull` of the three images; `gh attestation verify` (every local digest, until one verifies); a second `docker pull` of `api:main` plus `image inspect` (the revision must not have moved); read-only `git diff --name-only <deployed> <new>`, and, when a migration file changed or git cannot tell, a pre-migrate dump (`docker compose run --rm --no-deps -T --entrypoint sh backup -c "pg_dump ..."` into `<CM_BACKUP_DIR>\pre-migrate`, the newest 5 kept); `docker compose run --rm migrate`; `docker compose up -d api backup aspire-dashboard`; `curl` of `/health/ready`; `docker compose ps` (every started service must be up); `docker compose ps -q api` plus `docker inspect` (the running api must report the deployed commit); writes `state\deployer-state.json` and `logs\deployer.log*`; once, on a server that still has the retired `.ipa`, `docker run --rm ... sh -c "rm -rf /data/files/ipa"`; after a good deploy and on quiet polls (unless `CM_PRUNE_IMAGES=0`), read-only `docker image ls` / `docker ps -a`, then `docker image rm <repo>:<old commit>` for old tags of the api, migrate and backup repositories only (at most 100 per poll) |
| 6 | `tailscale serve --bg --https=443 http://127.0.0.1:5480` |
| 7 | `schtasks /Create ...` (one task) |
| 8 | `backup.sh once` inside the backup container |
| 12 | `schtasks /Create ...` (one task, `CollectionMaxxingAgent`, optional). Once installed, each run of the agent service changes your own checkout (`git fetch origin`, `git worktree add`, and `git worktree remove` for its own `agent-issue-<n>` folders) and GitHub (labels and comments on one issue, then whatever the agent does for it); see the next section |

It never runs `docker system prune`, `docker volume prune`, `docker image prune`, `docker rm` on any container, `docker image rm` on anything but a single old `repo:tag` of the api, migrate or backup repository ([Old images](#old-images-what-is-removed-and-what-is-kept)), `docker compose down -v`, anything that edits the registry, PATH, system or WSL settings, or anything that binds a port other than `127.0.0.1:5480` and `127.0.0.1:18888`.

## How the deployer decides (for review)

1. Every poll starts by fast-forwarding the deployer's own clone to `origin/main` when it is clean and fast-forwardable (otherwise a `WARN` line and the poll continues), so deployer and compose fixes self-deliver with no manual pull, ever. Then `docker pull` of `...collectionmaxxing-api:main`; its `org.opencontainers.image.revision` label is the commit SHA.
2. Same SHA as the running one: stop. A SHA that already failed here: skip until a newer one appears. Then the **ancestry guard**: `git -C C:\CollectionMaxxing\repo fetch origin main`, then `git merge-base --is-ancestor <running sha> <new sha>`. The new commit must be a strict descendant of the running one. An older commit, a diverged branch, a commit the clone has never seen, a running commit the clone does not know (or a state file that cannot be read), or a failed fetch all **refuse the deploy** (exit 4). Build times and run numbers play no part. The first ever deploy has nothing to compare with.
3. Pull `api`, `migrate` and `backup` at that SHA. For each, `gh attestation verify oci://...@sha256:<digest> --repo coryfinnegan/CollectionMaxxing --signer-workflow coryfinnegan/CollectionMaxxing/.github/workflows/release.yml`, **and** the certificate must say `refs/heads/main` (and the same commit). A pulled image can carry more than one local digest (the `:main` index digest sorts next to the `:sha` manifest digest, and only the manifest one is attested), so every digest is tried and the certificate decides; an unattested envelope never blocks an attested build (#491). Any failure: log it, do nothing. Then `:main` is pulled once more: when its revision moved while the images were checked, the stale commit is left for the next run.
4. When the release may change the schema, a **database dump first** ([Before a migration](#before-a-migration-a-named-database-dump) below). Then `docker compose run --rm migrate` (expand-only migrations, so the running api stays compatible). A failed dump: nothing is migrated and the api is not touched. A failed migration: the api is not touched.
5. `docker compose up -d api backup aspire-dashboard` with `CM_TAG=<sha>`, then up to 12 smoke tests of `http://127.0.0.1:5480/health/ready` 5 seconds apart, then `docker compose ps --format json` until every started service (`api`, `backup`, `aspire-dashboard`) is running (and healthy where it has a healthcheck): a crash-looping service fails the deploy like a failed smoke test. Then the running-version check: the api container's `org.opencontainers.image.revision` label must equal the deployed commit (read with `docker compose ps -q api` plus `docker inspect`), or the previous good version is pinned again like any other failed deploy.
6. Success: record the SHA as last good in `state\deployer-state.json` (with the version it replaced as `previous`, the rollback target), and, once, remove the retired `.ipa`, then clean up old images ([Old images](#old-images-what-is-removed-and-what-is-kept) below). Failure: remember the SHA as bad, `up -d` with the previous good tag, and check the previous version is healthy again. The clone already tracks `origin/main` from step 1; it is never the rollback anchor.

State lives in `C:\CollectionMaxxing\state`, logs in `C:\CollectionMaxxing\logs` (1 MB each, 5 kept). The log never contains the database password or the heartbeat URL.

**If the state file is lost** (deleted, or its path is wrong) while the stack is running, or `.env` / the compose file pins a `CM_TAG`, the deployer refuses (exit 4, ALERT) instead of treating it as a first deploy, because it could not tell an older commit from a newer one. Seed the state by hand with the commit the running api was built from:

```powershell
$env:CM_TAG = 'x'   # any value; compose needs it set to read the stack
$sha = docker inspect (docker compose -p collectionmaxxing -f C:\CollectionMaxxing\repo\deploy\docker-compose.yml --env-file C:\CollectionMaxxing\.env ps -q api) --format '{{index .Config.Labels "org.opencontainers.image.revision"}}'
New-Item -ItemType Directory -Force C:\CollectionMaxxing\state | Out-Null
@{ current = @{ sha = $sha.Trim() } } | ConvertTo-Json | Set-Content C:\CollectionMaxxing\state\deployer-state.json
```
The sha must be 40 lowercase hex characters. A true first deploy (no state, nothing running, no pinned tag) is unaffected.

**Exit codes:** 0 fine or nothing to do (also "already up to date", "known bad, waiting for a newer one", and a dry run), 1 deploy failed or rolled back (migration or its pre-migrate dump failed, smoke test or service check failed, stale lock, crashed), 2 config refused, 3 Docker or GHCR not reachable, 4 refused (not a descendant, not attested, unreadable state). (5 was the retired `.ipa` sync.)

**Refusals are loud.** Every non-zero result of a real (`-Apply`) run writes a line starting `ALERT deploy <outcome>` to `deployer.log` and pings `<CM_HEARTBEAT_URL>/fail` with the reason as the body: the same free healthchecks.io check the nightly backup uses, so you get the same email. (A deploy alert also marks that check "down" until the next good backup ping; that is the price of using one channel.) With no heartbeat URL the log line is the only alert. A failed ping never changes the exit code. The poller repeats the ping every 2 minutes while the cause persists; healthchecks only emails on the change.

**Rolling back on purpose** is the one way to go to an older commit, and it is separate from the poller:

```powershell
pwsh -NoProfile -File C:\CollectionMaxxing\repo\deploy\deployer\Deploy-CollectionMaxxing.ps1 -RollbackTo <40-char sha>          # dry run: prints the plan
pwsh -NoProfile -File C:\CollectionMaxxing\repo\deploy\deployer\Deploy-CollectionMaxxing.ps1 -RollbackTo <40-char sha> -Apply
```
The target must be an **older commit of the running one** (a newer one is a normal deploy; a diverged or unknown one is refused). Its three images get the same attestation proof as any deploy. No migration runs (they are expand-only). If the older version fails its smoke test, the version that was running is pinned again and the run exits 1. On success the commit you rolled back from is remembered as bad, so the poller does not put it straight back; a newer commit on main deploys normally.

### Before a migration: a named database dump

Migrations only add, but a failed one, or a rollback onto a changed schema, would otherwise leave only last night's dump. So before the migrate step the deployer asks git whether any file under a `Migrations` folder differs between the deployed commit and the new one (`git diff --name-only`, read only):

- **Yes, or git cannot tell** (a failed diff, no deployed commit, the very first deploy): it takes a dump. The deployer cannot see inside the migrate bundle, so "cannot tell" means "dump", and the log line says so (`could not tell whether migrations are pending; assuming they are`).
- **No migration file changed:** no dump (`no migration file changed between ... so no pre-migrate dump is taken`); the migrate step runs as before.

The dump is `pg_dump -Fc` run by `docker compose run --rm --no-deps -T --entrypoint sh backup` with the **backup image that is running now** (a proven one; the new one on the very first deploy), so the running postgres is not touched and the image's own `backup.sh` plays no part. It goes to `<CM_BACKUP_DIR>\pre-migrate\pre-migrate-<12 hex of the new commit>-<UTC yyyyMMdd-HHmmss>.dump`, as a `.partial` file that is checked (not empty, `pg_restore --list` can read it) before it is renamed. That folder is its own: the nightly backup, its retention and the restore drill never look in it. The deployer keeps the **last 5** and deletes older ones, only files matching that exact name pattern, and only after a good dump.

**If the dump fails, nothing is migrated:** the deploy stops with outcome `dump-failed` (exit 1, `ALERT` line, failure ping), the running version keeps running, and the commit is *not* marked bad (a full disk is not the release's fault), so the next poll tries again. To restore from one of these, follow [RESTORE.md](RESTORE.md) ("A real restore") with that `.dump` file; `pg_restore` reads it like a nightly one.

### Old images: what is removed and what is kept

Every deploy pulls three images (`collectionmaxxing-api`, `-migrate`, `-backup`, each tagged with the full commit hash); nothing used to remove them, and they filled the C: drive. After a good deploy (smoke test and service check passed) **and on every quiet poll** (so a backlog is worked off even when nothing new deploys), the deployer removes old tags, and only these:

- **Only tags of those three repositories** whose tag is a full 40-digit lower case commit hash. `:main`, any other tag, any other repository and any image of another project are never candidates.
- **Per repository, kept:** the version now running, the previous good version (`previous` in `state\deployer-state.json`, the rollback target), and the **3 newest others by image build time**. Anything whose build time cannot be read is kept. Everything else is removed.
- **Only by explicit `repo:tag`**, one `docker image rm <repo>:<tag>` each, never with `-f`. There is no `docker image prune`, `system prune` or `volume prune` anywhere. A tag that a container (running or stopped) still uses is skipped before docker is even asked; if docker refuses anyway, that is a `WARN` line, not an error.
- **It never fails a deploy.** Any problem (docker cannot list images, a removal fails) is a `WARN` line in the log and the next poll tries again. After 5 failed removals in a row it stops for that poll.
- **Pacing:** at most **100 tags per poll** and at most 90 seconds of removing, oldest first. The first poll after this change therefore removes 100 of the backlog (a few hundred tags on a server that has run for weeks) and says `N more wait for the next poll`; each following quiet poll (every 2 minutes) removes the next batch until only the kept tags are left. A clean machine costs one `docker image ls` per poll and logs nothing.
- **Kill switch:** `CM_PRUNE_IMAGES=0` in `C:\CollectionMaxxing\.env` (or in the deployer's process environment) turns the whole clean-up off. `false`, `off` and `no` work too; anything else, or not set, leaves it on. Remove the line to turn it back on.

The log lines to look for: `removed 100 old images of api, migrate and backup (kept the running and the previous version and the 3 newest of each); 281 more wait for the next poll`, and `could not remove 1 old image (first: ...)` as a `WARN`.

## The issue agent (optional)

A second scheduled task, separate from the deployer. Every 5 minutes `Watch-AgentIssues.ps1` looks on GitHub for a queued issue and, when there is one, starts a fresh headless Claude Code run (`claude -p`) for that one issue in its own git worktree. The agent works the issue test first, opens one pull request, has a separate verifier agent review it, squash-merges it when CI is green and the verifier approves (the owner's standing rule), and follows it to delivery. It replaces the old `scripts/watch-muse-issues.ps1`, which only printed ready issues for a long-lived session; a fresh run per issue keeps contexts small (ADR-023).

**It costs nothing while there is no work.** A watcher pass is plain PowerShell plus the `gh` CLI and never starts a model. An idle pass is one `gh issue list` call; with the switch off, the daily cap reached or a run still alive it makes none. Only a pass that finds an issue starts a model, once, for that issue.

### Queue an issue

Open an issue as yourself (issues by anyone else are never worked) and add the label `agent-task`. Optionally add `p1` to `p5` (no label counts as `p3`), a `type:*` label and a `tier:*` label. The issue text is the brief: write what you want and how you will know it is done. Text pasted into it (logs, alert output) is treated as data, not instructions. To see where it is, look at the issue: the labels and the comments say.

| Label | Meaning | Set by |
|---|---|---|
| `agent-task` | Queued for the agent | you |
| `agent-working` | A run has picked it up (comment "Picked up at ...") | the watcher |
| `agent-review` | The pull request is open; waiting for CI, the verifier and the merge | the agent (it removes it when delivered) |
| `agent-blocked` | The agent stopped and said why in a comment, or the watcher found the run dead; remove the label to try again | the agent or the watcher |
| `needs:cory`, `status:blocked` | Not the agent's to move; the watcher skips these | you or the agent |
| `p1` to `p5` | Order of work (`p1` first, then oldest) | you |
| `tier:mechanical` / `tier:critical` | Model: `haiku` / `opus`. Anything else runs `sonnet` | you |

### How one run goes

1. The pass reads `C:\CollectionMaxxing\.env`, closes out an earlier run if its process is gone, and stops if one is still alive (one run at a time) or the day's cap is reached.
2. It lists open `agent-task` issues written by the repo owner, skips any with `agent-working`, `agent-blocked`, `needs:cory` or `status:blocked`, and picks by priority, then oldest.
3. In your own checkout it runs `git fetch origin` (if that fails the issue is left untouched and the next pass tries again). Then it adds `agent-working`, comments "Picked up at <time>", runs `git worktree add <checkout>\.claude\worktrees\agent-issue-<n> -b agent/issue-<n> origin/main` (a leftover worktree from an earlier failed run of the same issue is reused as it is), and starts `Run-AgentIssue.ps1` in a headless console. The pass then exits.
4. The runner starts `claude -p --permission-mode auto --model <model>` with `agent-prompt.md` (with the issue number filled in) as its input, in that worktree. Everything it prints is appended to `logs\agent\issue-<n>-<yyyyMMdd-HHmmss>.log`.
5. The agent does the work as `CLAUDE.md` describes, comments the PR link on the issue and swaps `agent-working` for `agent-review`, has a verifier subagent review the diff, merges when CI is green and the verifier approves, and follows the merge to delivery. If it is blocked or unsure it comments what it needs, adds `agent-blocked` (and `needs:cory` when the decision is yours) and stops.
6. The next pass sees the process gone. If the issue is closed, the run finished: the worktree and the local branch are removed when its pull request merged. If the agent blocked itself, nothing more happens. Otherwise (a non-zero exit, no merge, still `agent-working`) the watcher adds `agent-blocked` and comments the last 30 log lines with secrets hidden; that worktree is kept so you can look at it. Then the lock is cleared and the same pass moves on to the next issue.

   One exception, so a broken sign-in cannot block the whole queue: a run that exits non-zero **within 5 minutes of starting** (sign-in expired, usage limit, `--permission-mode auto` refused) failed to start; the issue did not fail. That issue goes back in the queue (`agent-working` removed, `agent-task` kept, no `agent-blocked`) with one comment, "The agent couldn't start ... It will try again after <time>", holding the first 10 log lines with secrets hidden. The whole service then pauses for 60 minutes: every pass exits with no call to GitHub. A second quick failure in a row doubles the pause (8 hours at most); any run that gets past 5 minutes, or any other way a run ends, resets it. A quick failure is not counted against the daily cap.

### Limits and the switch

Settings go in `C:\CollectionMaxxing\.env` (all optional, read on every pass; see `.env.example`):

| Setting | Default | Meaning |
|---|---|---|
| `CM_AGENT_ENABLED` | on | `0`, `false`, `off` or `no` switches the agent off: every pass exits at once, with no call to GitHub. A run already going is not stopped by this |
| `CM_AGENT_REPO` | `<home>\source\repos\CollectionMaxxing` | Your own checkout, where the worktrees are made. It is refused if it is the deployer's own clone (`C:\CollectionMaxxing\repo`) or inside it, and nothing is claimed |
| `CM_AGENT_MAX_RUNS_PER_DAY` | 8 | Runs started per local day, counted when an issue is picked up (a run that fails within 5 minutes is given back). `0` pauses new runs. When the cap is hit the log says so once |
| `CM_AGENT_MAX_MINUTES` | 180 | A run longer than this is stopped with all its child processes and its issue is marked `agent-blocked`. The runner enforces this itself, because Task Scheduler may not start the watcher again while an agent runs |

Always on: one run at a time (a lock in `state\agent.lock`; a second watcher pass exits, and a lock whose process is gone or whose id now belongs to a newer process is cleared), only issues written by the repo owner, no admin rights, no second account, and no other scheduled task is touched. The agent itself works under the hard rules in `CLAUDE.md`: its worktree and its own branch only, never `main`, no force-push, Docker and ports as `CLAUDE.md` says, nothing secret in a commit or comment. The log tail the watcher posts passes through the same secret masking as the deployer's log (every secret-looking value in `.env`, GitHub and bearer tokens, the home folder).

The agent runs as your Windows account with your `gh` login and your Claude Code sign-in, while you are signed in. `--permission-mode auto` has to be available for that sign-in; if it is not, the first run fails at once, its log tail lands on the issue and the issue is marked `agent-blocked`.

### Install, look, stop

```powershell
pwsh -NoProfile -File C:\CollectionMaxxing\repo\deploy\agent\Install-AgentWatcher.ps1            # dry run: prints the schtasks command
pwsh -NoProfile -File C:\CollectionMaxxing\repo\deploy\agent\Install-AgentWatcher.ps1 -Apply
schtasks /Query /TN CollectionMaxxingAgent
```
It registers one user-level task, `CollectionMaxxingAgent` (every 5 minutes, `conhost --headless pwsh ... Watch-AgentIssues.ps1 -Root C:\CollectionMaxxing -Apply`, `/RL LIMITED`), and refuses to replace one that exists. Run `Watch-AgentIssues.ps1` by hand without `-Apply` to see what a pass would do; it changes nothing.

| Where | What |
|---|---|
| `C:\CollectionMaxxing\logs\agent-watcher.log` | The watcher's own log; quiet passes write nothing |
| `C:\CollectionMaxxing\logs\agent\issue-<n>-<time>.log` | One run: header, everything claude printed, how it ended. Next to it: `.prompt.md` (what it was told) and `.exit.json` |
| `C:\CollectionMaxxing\state\agent-state.json`, `agent.lock` | The day's run count and the pause after quick failures (`pauseUntilUnix`, `quickFailures`; delete the file to clear it); the run in progress |
| `<checkout>\.claude\worktrees\agent-issue-<n>` | A run's worktree, kept while its issue is blocked |

**If an issue sits in the queue:** it must be written by the repo owner and carry `agent-task` and none of `agent-working`, `agent-blocked`, `needs:cory`, `status:blocked`. Then look for `config-error` lines in `logs\agent-watcher.log` (no checkout at `CM_AGENT_REPO`, `claude` not found), check `CM_AGENT_ENABLED`, the day's cap, whether the service is paused after a quick failure (the last comment on the last issue says until when) and whether another run holds `state\agent.lock`. **If a picked-up issue gets "The agent couldn't start" within minutes**, read the log lines in that comment (sign-in, usage limit, `auto` mode). **If it turns `agent-blocked` within minutes with "no exit code recorded"** and its run log holds only the header line, the run was killed with the watcher that started it; the comment carries the log tail.

**Pause:** set `CM_AGENT_ENABLED=0` in `.env`. **Stop a run now:** `taskkill /PID <pid in state\agent.lock> /T /F`; the next pass marks its issue blocked. **Uninstall:** `pwsh -NoProfile -File C:\CollectionMaxxing\repo\deploy\agent\Install-AgentWatcher.ps1 -Uninstall -Apply` (or `schtasks /Delete /TN CollectionMaxxingAgent /F`). **Run an issue again:** remove `agent-blocked` from it.

## Changing the code

Run `pwsh -NoProfile -File deploy/tests/Run-Tests.ps1` (about a minute and a half; needs Git for Windows' bash, or Linux). Most of it needs no Docker daemon; the restore-drill suite starts real throwaway Postgres containers (`cm-ops1-test-*`, no network, no ports, no volumes, removed afterwards) and needs Docker running and the `postgres:17` image, and `-SkipDocker` leaves it out. `-DockerBuild` also builds the three Dockerfiles as `cm-ops1-test-*` images and removes only those tags. Tests never touch the real Docker, GitHub, Tailscale or Task Scheduler: every external command goes through `Invoke-Cm`, which the tests replace with a recorder.
