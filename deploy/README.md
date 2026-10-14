# Deploying CollectionMaxxing on the PC

The PC **pulls**: a small deployer checks GitHub for a new `main` image every 2 minutes, proves this repo built it, and swaps it in. Nothing pushes to the PC and there is no self-hosted runner (`docs/10-hosting-and-devops.md`, ADR-022).

Everything here was written and tested **without touching the real machine**. Every script is a dry run unless you pass `-Apply`, and prints the exact commands it would run. **Nobody has run the real install yet: it is an owner checkpoint.** Follow the checklist below on the PC, in order.

| Folder | What is in it |
|---|---|
| `docker-compose.yml`, `.env.example` | The production stack, bound to `127.0.0.1` only |
| `deployer/` | `Deploy-CollectionMaxxing.ps1` (the deployer), `Install-Deployer.ps1` / `Uninstall-Deployer.ps1` (the scheduled task), `Sync-Ipa.ps1`, `New-Secrets.ps1`, shared `Cm.*.ps1` |
| `tailscale/` | `Set-TailscaleServe.ps1` |
| `backup/` | The backup image: `Dockerfile.backup`, `backup.sh`, `backup-lib.sh` |
| `docker/` | `Dockerfile.api`, `Dockerfile.migrate` |
| `proposed/release.yml` | The CI workflow that builds and signs the images (the lead copies it to `.github/workflows/`) |
| `tests/` | `pwsh -NoProfile -File deploy/tests/Run-Tests.ps1` runs everything and prints one summary line |

Ports: the API is `127.0.0.1:5480` and the Aspire dashboard `127.0.0.1:18888`. Ports 8080 (SABnzbd), 8409 (ErsatzTV), 32400 (Plex) and 8096 (Jellyfin) belong to other apps; the deployer refuses to start a stack that uses them or that binds anything but `127.0.0.1`. Postgres is not published at all.

## Before you start (nothing here changes anything)

- [ ] **Docker Desktop** is updated (24.0.7 is from 2023) and set to start when you sign in. Windows: Settings > Accounts > Sign-in options > "Use my sign-in info to automatically finish setting up after an update".
- [ ] **GitHub CLI** is version 2.49 or newer (the first with `gh attestation verify`). Check with `gh --version`; update with `winget upgrade GitHub.cli`.
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
When the deploy scripts themselves change, update them with `git -C C:\CollectionMaxxing\repo pull --ff-only`. The *app* updates through images and needs no pull.

### 2. Create `.env`

```powershell
Copy-Item C:\CollectionMaxxing\repo\deploy\.env.example C:\CollectionMaxxing\.env
# a database password: copy this output into POSTGRES_PASSWORD
[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
notepad C:\CollectionMaxxing\.env
```
Fill in `POSTGRES_PASSWORD`, `CM_RELYING_PARTY_ID` (this PC's Tailscale name, for example `pc.tailnet.ts.net`; **it cannot change later without losing every passkey**), `CM_SECRETS_DIR`, `CM_BACKUP_DIR` (a folder on **F:**, not C:), `CM_TZ` and, if you want email alerts, `CM_HEARTBEAT_URL` (step 9). Leave the ports alone. `.env` is never committed. **Undo:** delete the file.

### 3. Create the token keys

```powershell
pwsh -NoProfile -File C:\CollectionMaxxing\repo\deploy\deployer\New-Secrets.ps1 -Path C:\CollectionMaxxing\secrets          # dry run
pwsh -NoProfile -File C:\CollectionMaxxing\repo\deploy\deployer\New-Secrets.ps1 -Path C:\CollectionMaxxing\secrets -Apply
```
Expect: `dry run, would create: ...` twice, then `created: ...` twice. It never overwrites an existing key. Then make the folder readable only by your account (folder Properties > Security) and make sure BitLocker covers that drive. **Undo:** delete the two `.pfx` files (everyone is signed out; passkeys are unaffected).

### 4. Dry run of the deployer (read-only)

```powershell
pwsh -NoProfile -File C:\CollectionMaxxing\repo\deploy\deployer\Deploy-CollectionMaxxing.ps1
```
Expect: `DRY RUN: nothing will be changed`, no `config problem:` lines, then the list of commands a real run makes (`docker pull`, `gh attestation verify`, `compose run --rm migrate`, `compose up -d api backup aspire-dashboard`, a `curl` of `/health/ready`). If you see `config problem:` read it: a forbidden port, a non-loopback bind, or a missing `.env` value.

### 5. First deploy, by hand

```powershell
pwsh -NoProfile -File C:\CollectionMaxxing\repo\deploy\deployer\Deploy-CollectionMaxxing.ps1 -Apply
```
Expect, in `C:\CollectionMaxxing\logs\deployer.log` and on screen: `deploying <12 hex> (attestations verified)`, then `deployed <12 hex>; smoke test passed`. Exit code 0 (`$LASTEXITCODE`). Check:

```powershell
Invoke-RestMethod http://127.0.0.1:5480/health/ready      # Healthy
docker compose -p collectionmaxxing ps                     # postgres, api, aspire-dashboard, backup running; migrate exited 0
```
Open `http://127.0.0.1:18888` for logs, traces and metrics. The one-time **setup code** (to create the owner passkey) is printed in `docker compose -p collectionmaxxing logs api` and written to `C:\CollectionMaxxing\secrets\setup-code.txt`.

If a step fails, the log says why: no `gh` login or an image that is not attested = nothing deployed; a failed migration = the old api keeps running; a failed smoke test = the previous good version is pinned and restarted.
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
The only thing it registers is `schtasks /Create /TN CollectionMaxxingDeployer /SC MINUTE /MO 2 /TR "pwsh ... Deploy-CollectionMaxxing.ps1 -Root C:\CollectionMaxxing -Apply" /RL LIMITED`: a user-level task, no admin, no SYSTEM account. It runs while you are signed in, like Docker Desktop. Runs never overlap (a second one exits at once). It refuses to replace an existing task of that name.
**Undo:** `pwsh -NoProfile -File C:\CollectionMaxxing\repo\deploy\deployer\Uninstall-Deployer.ps1 -Apply` (or `schtasks /Delete /TN CollectionMaxxingDeployer /F`).

### 8. Check the first backup

```powershell
docker compose -p collectionmaxxing -f C:\CollectionMaxxing\repo\deploy\docker-compose.yml --env-file C:\CollectionMaxxing\.env exec backup /opt/cm/backup.sh once
Get-ChildItem F:\Backups\CollectionMaxxing -Recurse
```
(`exec` here needs `CM_TAG` set; take it from `C:\CollectionMaxxing\state\deployer-state.json` > `current.sha`, and run `$env:CM_TAG='<sha>'` first.) Expect `database dumped to db/collectionmaxxing-<time>.dump` and `files archived to files/files-<time>.tar.gz`, and those two files on F:. Nightly runs happen at `CM_BACKUP_SCHEDULE` (default 03:00 in `CM_TZ`). Retention is 14 daily, 8 weekly and 12 monthly; a failed night never deletes anything.
**Undo:** delete the files in the backup folder.

### 9. Heartbeat (an email when a night is missed)

Create a free check at healthchecks.io with a 1-day period and a grace of a few hours, put its ping URL in `CM_HEARTBEAT_URL` in `.env`, then recreate the backup container: `$env:CM_TAG='<sha>'; docker compose -p collectionmaxxing -f ... --env-file ... up -d backup`. A good night pings the URL, a failed dump pings `<url>/fail`, an empty URL pings nothing.

### 10. The iPhone `.ipa`

The deployer ends every successful deploy with `Sync-Ipa.ps1`: it downloads the `.ipa` and its `.sha256` from the **latest GitHub release**, checks the checksum, and copies it into the files volume as `ipa/CollectionMaxxing.ipa`. A bad checksum, or a release with no checksum file, leaves the previous file alone. By hand: `Sync-Ipa.ps1` (dry run) or `Sync-Ipa.ps1 -Apply`.
**Undo:** nothing to undo: it is a downloadable copy under `ipa/` in the files volume, and the next sync replaces it. (The release workflow must upload a `.sha256` file next to the `.ipa`; see the PR notes.)

### 11. Optional: off-site copy (off by default)

Nothing here is set up. When you want it (docs/10): create a free Backblaze B2 bucket or use OneDrive, then `rclone config` (an `crypt` remote on top), and schedule `rclone copy F:\Backups\CollectionMaxxing <crypt-remote>:` weekly with a normal Windows scheduled task you create yourself. Keep the rclone password somewhere safe: without it the copy cannot be read.

## Everything the real install runs

| Step | Commands that change the machine |
|---|---|
| 1 | `git clone` into `C:\CollectionMaxxing\repo` |
| 2 | Copies `.env.example` to `.env` (you edit it) |
| 3 | `New-Secrets.ps1 -Apply`: writes two `.pfx` files |
| 5, and every 2 minutes after | `docker pull` of the three images; `gh attestation verify`; `docker compose run --rm migrate`; `docker compose up -d api backup aspire-dashboard`; `curl` of `/health/ready`; writes `state\deployer-state.json` and `logs\deployer.log*`; `docker run --rm ... sh -c "cp ..."` (the `.ipa` copy) and `gh release download` |
| 6 | `tailscale serve --bg --https=443 http://127.0.0.1:5480` |
| 7 | `schtasks /Create ...` (one task) |
| 8 | `backup.sh once` inside the backup container |

It never runs `docker system prune`, `docker volume prune`, `docker rm` on anything else, `docker compose down -v`, anything that edits the registry, PATH, system or WSL settings, or anything that binds a port other than `127.0.0.1:5480` and `127.0.0.1:18888`.

## How the deployer decides (for review)

1. `docker pull` of `...collectionmaxxing-api:main`; its `org.opencontainers.image.revision` label is the commit SHA.
2. Same SHA as the running one: stop. A SHA that already failed here: skip until a newer one appears. A build time older than the running one: **downgrade refused**.
3. Pull `api`, `migrate` and `backup` at that SHA. For each, `gh attestation verify oci://...@sha256:<digest> --repo coryfinnegan/CollectionMaxxing --signer-workflow coryfinnegan/CollectionMaxxing/.github/workflows/release.yml`, **and** the certificate must say `refs/heads/main` (and the same commit). Any failure: log it, do nothing.
4. `docker compose run --rm migrate` (expand-only migrations, so the running api stays compatible). Failure: the api is not touched.
5. `docker compose up -d api backup aspire-dashboard` with `CM_TAG=<sha>`, then up to 12 smoke tests of `http://127.0.0.1:5480/health/ready` 5 seconds apart.
6. Success: record the SHA as last good, then sync the `.ipa`. Failure: remember the SHA as bad, `up -d` with the previous good tag, and smoke test again.

State lives in `C:\CollectionMaxxing\state`, logs in `C:\CollectionMaxxing\logs` (1 MB each, 5 kept). The log never contains the database password or the heartbeat URL. Exit codes: 0 fine or nothing to do, 1 deploy failed or rolled back, 2 config refused, 3 Docker or GHCR not reachable.

## Changing the code

Run `pwsh -NoProfile -File deploy/tests/Run-Tests.ps1` (about 30 seconds; needs Git for Windows' bash, or Linux). `-DockerBuild` also builds the three Dockerfiles as `cm-ops1-test-*` images and removes only those tags. Tests never touch the real Docker, GitHub, Tailscale or Task Scheduler: every external command goes through `Invoke-Cm`, which the tests replace with a recorder.
