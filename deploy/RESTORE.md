# Restore runbook

Two jobs: the **restore drill** (twice a year, and before v1) proves the backups restore; a **real restore** is what you do after losing the database. The drill never touches the running stack, so it is safe to run any time.

## The restore drill (about 2 minutes)

Needs Docker Desktop running and Git for Windows (for bash). From a PowerShell window:

```powershell
cd C:\CollectionMaxxing\repo
pwsh -NoProfile -File scripts\restore-drill.ps1 -ConfirmLive                      # newest backup in CM_BACKUP_DIR (from C:\CollectionMaxxing\.env)
pwsh -NoProfile -File scripts\restore-drill.ps1 -ConfirmLive -Path F:\Backups\CollectionMaxxing
pwsh -NoProfile -File scripts\restore-drill.ps1 -ConfirmLive -Path F:\Backups\CollectionMaxxing\db\collectionmaxxing-<time>.dump   # a specific dump
```
(On Linux or in Git Bash: `scripts/restore-drill.sh [--confirm-live] [folder-or-dump]`.)

**`-ConfirmLive`** is your "yes, I mean the real thing". The drill only reads the backup, but it refuses (exit 3) to look at `C:\CollectionMaxxing`, the nightly backup folder or the `CM_BACKUP_DIR` from `.env` without it, so a pasted path or a default can never point it at live data by accident. To rehearse without that flag, copy one dump, its `.manifest.json` and the matching `files-<time>.tar.gz` into a temp folder (keeping the `db\` and `files\` sub-folders) and give that folder as `-Path`.

Record every drill in [drills/README.md](drills/README.md). The local nightly backup on F: is what the drill covers; the off-site copy is optional and not set up (README step 11).

What it does, in order:

1. Takes the newest `collectionmaxxing-<time>.dump` and its `.manifest.json` (written by the nightly backup).
2. Checks the dump's sha256 against the manifest.
3. Starts **one** throwaway `postgres:17` container named `cm-drill-*` with no network, no published port and no volume, and restores the dump into it.
4. Migrations check: the newest applied migration must equal the one recorded in the manifest.
5. Row counts: every table's restored count must equal the count recorded at backup time (or lie between the counts taken just before and just after the dump).
6. Restores the files archive in the same container and compares the sha256 of every file with the manifest's list.
7. Removes its container and prints `RESULT: PASS` or `RESULT: FAIL`.

Expected on a good backup (numbers are examples):

```
PASS dump sha256 matches the manifest
PASS restore: pg_restore finished without errors
PASS migrations: newest applied is 20261013153821_InitialCreate, as recorded
PASS table items: restored 412, expected 412
...
PASS files: 398 restored, 398 expected, every hash matches
RESULT: PASS
```
Exit codes: 0 PASS, 1 FAIL, 2 the drill could not run (no backup found, Docker not running), 3 refused (the path is live data and you did not pass `-ConfirmLive`).

It never reads or writes the live database or its volume and never uses a port. If you stop it half way, find any leftover with `docker ps -a --filter "label=cm-drill=1"` and delete just those `cm-drill-...` containers in Docker Desktop (touch nothing else). A normal finish, FAIL included, removes the container and its temporary data volume by itself.

### If it says FAIL

| Line | Meaning | What to do |
|---|---|---|
| `FAIL no manifest` | A backup from before manifests existed, or the manifest was deleted. | Run a fresh backup (`backup.sh once`, README step 8) and drill that one. |
| `FAIL dump sha256 ...` | The dump changed after it was written (disk fault, a copy that broke). | Try the next older backup; check the drive's health. Do not rely on this one. |
| `FAIL table <name>: restored X, expected Y` | Rows are missing from the dump. | Try the previous night's backup, then ask the lead to look at why. |
| `FAIL migrations ...` | The dump's schema is not the one recorded. | Same as above; do not restore this dump into a newer database by hand. |
| `FAIL files: ...` | Missing or changed cover files in the archive. | Try the previous night's archive. |
| `restore drill: ... Docker` | Docker is not running. | Start Docker Desktop and run it again. |

Also check the failure email: a missed or failed night pings the heartbeat check, so a stale backup should already have told you.

Write the date and the `RESULT:` line down somewhere (a note is enough). Do the drill again in six months, and after any change to the backup job.

## A real restore (the database is lost)

Only if the live database is gone or corrupt. Slow down; nothing here is urgent enough to rush.

1. Run the drill on the backup you intend to use. Do not restore a dump that fails it.
2. Stop the stack without deleting anything: `docker compose -p collectionmaxxing -f C:\CollectionMaxxing\repo\deploy\docker-compose.yml --env-file C:\CollectionMaxxing\.env stop`. **Never** `down -v`: it deletes the volumes.
3. Ask the lead (or Claude Code in this repo) to walk you through restoring that dump into the `postgres` service, because the exact commands depend on what is left of the old volume. Keep the old volume until the restore has been checked.
4. Restore the covers from `files\files-<time>.tar.gz` of the same night into the `files` volume (`covers/` and `home/`; `home/` holds the sign-in key ring, so everyone stays signed in).
5. Start the stack, open the app, and spot-check a few items and covers.

If an off-site copy is ever switched on (README step 11), copy one back next to each other as `db` and `files` in a temp folder and drill that folder the same way.
