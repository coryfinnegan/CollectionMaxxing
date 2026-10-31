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

Ports: the API is `127.0.0.1:5480` and the Aspire dashboard `127.0.0.1:18888`. Ports 8080 (SABnzbd), 8409 (ErsatzTV), 32400 (Plex) and 8096 (Jellyfin) belong to other apps; the deployer refuses to start a stack that uses them or that binds anything but `127.0.0.1`. Postgres is not published at all. The same config guard (exit 2, before anything is pulled, dumped or migrated, and for the release's own compose file too) also refuses host networking, privileged containers, `cap_add`, `pid: host` / `ipc: host`, host devices, the Docker socket or engine pipe, a named volume that is secretly a host bind, and any **bind mount of a host folder outside an allow-list**: the `CM_BACKUP_DIR`, `CM_SECRETS_DIR`, `CM_RECOGNITION_DIR` and `CM_EVALS_DIR` folders from `.env`, the clone's own `deploy` and `evals` folders, and the `evals` folder beside the clone. Nothing else, so not `C:\CollectionMaxxing` itself (it holds `.env`) and not the home folder. The messages name the service and the container path, never a host path. A release that needs a new mount changes the guard in the same pull request (`Get-CmAllowedMountRoots` in `deploy/deployer/Cm.Common.ps1`); the poll that first sees both is refused once and the next poll, now running the new guard, accepts it.

## From merge to running (the delivery path)

What happens after a merge to `main`, in order, and what each step needs from the world:

1. **CI builds and signs** (`.github/workflows/release.yml`, "Release images", about 4-5 minutes). Three images land on GHCR tagged with the full commit SHA, each with a build attestation; then the moving `:main` tag jumps onto all three — but only after every leg AND its attestation succeeded. Needs: GitHub Actions running. When the run fails, `:main` stays where it was and the deployer never sees the commit.
2. **The deployer polls** (the `CollectionMaxxingDeployer` scheduled task, every 2 minutes). It pulls `api:main`, reads the commit SHA from the image's `org.opencontainers.image.revision` label, and stops when that is the commit already running. Needs: the PC on, you signed in, Docker Desktop running. The task does not run while you are signed out, and nothing queues up for it — the first poll after you sign in just picks up whatever `:main` is then.
3. **The deployer proves and swaps** ("How the deployer decides" below): ancestry (the new commit must descend from the running one), one attestation per image (the certificate must say this repo built it from `main` at that commit), the expand-only migration, `up -d`, then the smoke test, the service check and the running-version check (the api container's revision label must equal the deployed commit). A refusal or a failure is an `ALERT` line in `logs\deployer.log`, a labeled GitHub issue ([Alerts as GitHub issues](#alerts-as-github-issues)) and, only when `CM_HEARTBEAT_URL` is set in `.env`, a failure email.
4. **Delivery is verified against the running server**, not the registry: `GET http://127.0.0.1:5480/health/version` reports the commit the api is running (`CM_BUILD_SHA`, set from the deployed tag). A server-side fix is delivered when that endpoint reports the merge commit (or a newer one) — the rule proposal is `docs/proposed-server-delivery-rule-491.md`.

Manual steps, honestly listed: the one-time install checklist below; keeping the PC on, signed in and Docker running (steps 2-3 only happen then); setting `CM_HEARTBEAT_URL` if you also want failure emails (the alert issues need nothing from you but a working `gh` login). There is no manual pull, ever: at the start of every poll the deployer fast-forwards its own clone to `origin/main` when the clone is clean and fast-forwardable (a dirty, diverged or off-`main` clone is left alone with a `WARN` line until it is fixed), so a deployer or compose fix self-delivers within one poll cycle and the next run loads it. Last-good stays solely in `state\deployer-state.json` (the rollback anchor), never in the clone position.

## Before you start (nothing here changes anything)

- [ ] **Docker Desktop** is updated (24.0.7 is from 2023) and set to start when you sign in. Windows: Settings > Accounts > Sign-in options > "Use my sign-in info to automatically finish setting up after an update".
- [ ] **GitHub CLI** is version 2.49 or newer (the first with `gh attestation verify`). Check with `gh --version`; update with `winget upgrade GitHub.cli` (needs admin), or, without admin, unzip the official `gh_<version>_windows_amd64.zip` from [cli/cli releases](https://github.com/cli/cli/releases) to `C:\CollectionMaxxing\tools\gh`. The deployer puts `tools\gh\bin` first on its own PATH when it is there.
- [ ] **`gh auth status`** says you are logged in (the attestation check needs a token; read-only is enough, but the alert issues need one that can write issues on this repo, which the normal `gh auth login` gives). If not, run `gh auth login` yourself.
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
Fill in `POSTGRES_PASSWORD`, `CM_PUBLIC_ORIGIN` (the address people browse to, for example `https://pc.tailnet.ts.net`), `CM_SECRETS_DIR`, `CM_BACKUP_DIR` (a folder on **F:**, not C:), `CM_TZ` and, if you want email alerts, `CM_HEARTBEAT_URL` (step 9). `CM_GOOGLE_BOOKS_API_KEY` is optional: paste a Google Books API key to add a second source for book lookups (leave it empty to use Open Library alone). Games and movies need theirs: `CM_IGDB_CLIENT_ID` and `CM_IGDB_CLIENT_SECRET` (a Twitch app at dev.twitch.tv/console) and `CM_TMDB_API_KEY` (the API Read Access Token at themoviedb.org/settings/api); without them every game or movie picture finds nothing, silently. Windows environment variables such as `Providers__GoogleBooks__ApiKey` do **not** reach the containers; only these `.env` lines do. After changing one, recreate the API (the next deploy does it too): `$env:CM_TAG='<sha>'; docker compose -p collectionmaxxing -f C:\CollectionMaxxing
epo\deploy\docker-compose.yml --env-file C:\CollectionMaxxing\.env up -d api` (`<sha>` is `current.sha` in `C:\CollectionMaxxing\state\deployer-state.json`). Leave the ports alone. `.env` is never committed. **Undo:** delete the file.

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
| 5, and every 2 minutes after | `git -C ...\repo fetch origin main` plus `git merge --ff-only origin/main` at poll start (after read-only `git status` / `git symbolic-ref`; skipped with a `WARN` when the clone is dirty, off `main` or diverged) and, only when a new image appeared, a second fetch plus read-only `git cat-file` / `git merge-base --is-ancestor`; `docker pull` of the three images; `gh attestation verify` (every local digest, until one verifies); a second `docker pull` of `api:main` plus `image inspect` (the revision must not have moved); read-only `git diff --name-only <deployed> <new>`, and, when a migration file changed or git cannot tell, a pre-migrate dump (`docker compose run --rm --no-deps -T --entrypoint sh backup -c "pg_dump ..."` into `<CM_BACKUP_DIR>\pre-migrate`, the newest 5 kept); `docker compose run --rm migrate`; `docker compose up -d api backup aspire-dashboard`; `curl` of `/health/ready`; `docker compose ps` (every started service must be up); `docker compose ps -q api` plus `docker inspect` (the running api must report the deployed commit); writes `state\deployer-state.json` and `logs\deployer.log*`; after a failed run `gh issue list` / `create` / `comment` on the repo for the alert issue, after a good one `gh issue close` of the ones it opened, and on quiet polls and after a deploy a read-only `docker compose ps -q backup` plus `docker inspect` of the backup container (unless `CM_ALERT_ISSUES=0`); once, on a server that still has the retired `.ipa`, `docker run --rm ... sh -c "rm -rf /data/files/ipa"`; after a good deploy and on quiet polls (unless `CM_PRUNE_IMAGES=0`), read-only `docker image ls` / `docker ps -a`, then `docker image rm <repo>:<old commit>` for old tags of the api, migrate and backup repositories only (at most 100 per poll) |
| 6 | `tailscale serve --bg --https=443 http://127.0.0.1:5480` |
| 7 | `schtasks /Create ...` (one task) |
| 8 | `backup.sh once` inside the backup container |

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

**Refusals are loud.** Every non-zero result of a real (`-Apply`) run writes a line starting `ALERT deploy <outcome>` to `deployer.log`, opens or updates a labeled GitHub issue ([below](#alerts-as-github-issues)), and pings `<CM_HEARTBEAT_URL>/fail` with the reason as the body: the same free healthchecks.io check the nightly backup uses, so you get the same email. (A deploy alert also marks that check "down" until the next good backup ping; that is the price of using one channel.) With no heartbeat URL and `CM_ALERT_ISSUES=0` the log line is the only alert. A failed ping or issue never changes the exit code. The poller repeats the ping every 2 minutes while the cause persists; healthchecks only emails on the change, and the issue is touched at most once an hour.

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

### Alerts as GitHub issues

A problem on the PC reaches you without anyone reading `deployer.log`: the deployer opens a GitHub issue through the portable `gh` it already uses (it runs as your Windows user, so it is your `gh` login; `guard-issues.yml` leaves owner-authored issues alone). It is **once per problem, not once per poll**:

- Every alert has a **kind**, and a kind has at most one open issue. The issue is found by the `alert` label plus a hidden marker in its body (`<!-- cm-alert:<kind> -->`). While the problem lasts the deployer adds a comment ("Again at 13:00 UTC, 27 times today"), **at most one per kind per hour**; it never opens a second issue for the same kind.
- When the problem clears, the deployer **closes** that issue with a one-line comment saying what cleared it. A deploy problem clears with the next good deploy (or when `main` is the running version again); a quiet `known-bad` poll does not clear it, because the bad commit is still `main`. `backup-unhealthy` clears when the backup container reports healthy, the two Docker kinds when Docker answers again.
- **Transient kinds wait.** `docker-unavailable` and `main-tag-moving` file nothing until they have lasted 10 polls in a row (about 20 minutes), `deployer-crashed` until 3; one good poll starts the count again. The log line and the heartbeat ping are not delayed.
- Every issue carries `alert` and `type:infra`. **Code problems an agent can fix** also get `agent-task`, `p1`, `sev:blocker`. **Anything only you can fix, or that an agent must not "fix"**, gets `needs:cory` and `p1` (`p2` where the deployer carries on by itself). An `attestation-failed` issue is never an agent task: it may be tampering, and nobody loosens the verification.
- The body says in one plain sentence what happened, when, the running and the new commit, what to do, and the last 20 lines of the log. Everything is passed through `Hide-CmSecrets` and a second scrub (every `.env` value that is a secret, token, key, URL, origin, domain or client id, anything shaped like a token, tailnet host names, home folders and email addresses) before it leaves the PC.
- It never fails or blocks a deploy. If `gh` fails (offline, not logged in) the deployer logs a `WARN`, carries on, and leaves that kind alone for 15 minutes; the alert is kept and filed (with the time it happened) once `gh` works again, unless the problem clears first. At most **10 issue writes a day** (create, comment, close, all kinds together, counted in UTC days).
- **Switch:** `CM_ALERT_ISSUES=0` in `C:\CollectionMaxxing\.env` (or the deployer's process environment) turns issue filing off; `false`, `off` and `no` work too. The `ALERT` log line and the heartbeat ping stay. Manual `-RollbackTo` runs never file an issue.
- What it remembers (the open issue number, when it last wrote, today's count, the polls in a row) is under the `alerts` key of `state\deployer-state.json`; nothing else in that file is touched, and an unreadable state file is never overwritten.

| Kind | Labels beyond `alert` and `type:infra` | Raised when | Cleared by |
|---|---|---|---|
| `deploy-rolled-back` | `agent-task` `p1` `sev:blocker` | a new version started but a service crashed or the wrong build answered, and the previous version was put back | a later good deploy, or `main` is the running version again |
| `smoke-failed` | `agent-task` `p1` `sev:blocker` | the new version never answered `/health/ready`, previous version put back | same |
| `migrate-failed` | `agent-task` `p1` `sev:blocker` | the database migration failed; the running api was not touched | same |
| `config-guard-refused` | `agent-task` `p1` `sev:blocker` | the config guard refused the setup or the release's compose file | same |
| `deployer-crashed` | `agent-task` `p1` `sev:blocker` | the deployer script threw, 3 polls in a row | a run that finishes normally |
| `attestation-failed` | `needs:cory` `p1` | an image has no valid build proof, or is not the commit it claims to be | same as the deploy kinds |
| `downgrade-refused` | `needs:cory` `p1` | the newest `main` image is older than, diverged from or unrelated to the running version | same |
| `deployer-setup-refused` | `needs:cory` `p1` | the state file is unreadable or lost, or the clone cannot supply the compose file | same |
| `pre-migrate-dump-failed` | `needs:cory` `p1` | the dump before a migration failed (usually a full or missing backup drive) | a later good deploy |
| `deployer-stuck` | `needs:cory` `p1` | a run has held the lock for over 30 minutes | a run that finishes normally |
| `backup-unhealthy` | `needs:cory` `p1` | the backup container reports unhealthy (newest good backup over 30 h old, Postgres unreachable) or is not running; read with `docker inspect` on every quiet poll and after a deploy | the container reports healthy |
| `docker-unavailable` | `needs:cory` `p2` | Docker or GHCR did not answer for 10 polls in a row | Docker answers again |
| `main-tag-moving` | `needs:cory` `p2` | the `main` image tag moved during the checks, 10 polls in a row | the tag settles |

A quiet poll with nothing open and a healthy backup makes no `gh` call at all. The deployer has no free-disk check of its own; a full backup drive shows up as `pre-migrate-dump-failed` or `backup-unhealthy`.

## Changing the code

Run `pwsh -NoProfile -File deploy/tests/Run-Tests.ps1` (about a minute and a half; needs Git for Windows' bash, or Linux). Most of it needs no Docker daemon; the restore-drill suite starts real throwaway Postgres containers (`cm-ops1-test-*`, no network, no ports, no volumes, removed afterwards) and needs Docker running and the `postgres:17` image, and `-SkipDocker` leaves it out. `-DockerBuild` also builds the three Dockerfiles as `cm-ops1-test-*` images and removes only those tags. Tests never touch the real Docker, GitHub, Tailscale or Task Scheduler: every external command goes through `Invoke-Cm`, which the tests replace with a recorder.
