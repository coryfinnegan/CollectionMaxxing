# A missed backup, or a full disk

## When to use this

The heartbeat email says a night was missed or failed (a missed ping, or one to `/fail`), or the backup drive (F:) is nearly full. A failed night never deletes older backups, so nothing is lost yet.

## Steps

1. Check the drive is there and has room, and see the newest backups:
   ```powershell
   Get-PSDrive F | Select-Object Used, Free
   Get-ChildItem F:\Backups\CollectionMaxxing\db | Sort-Object LastWriteTime -Descending | Select-Object -First 5 Name, Length, LastWriteTime
   ```
2. Read the backup container's own words about the failure (run the shell preamble from [README.md](README.md) first):
   ```powershell
   docker @cm logs backup --tail 40
   ```
   Typical causes: F: is unplugged or full, Docker was off at 03:00 (see [reboot-or-docker-off.md](reboot-or-docker-off.md)), or the heartbeat URL is wrong.
3. Fix the cause, then take a backup now:
   ```powershell
   docker @cm exec backup /opt/cm/backup.sh once
   ```
4. If the drive is full, make room by hand in File Explorer, never with a script. Retention keeps 14 daily, 8 weekly and 12 monthly copies. Look at `F:\Backups\CollectionMaxxing\db` and `...\files`: each backup is a `.dump` plus its `.manifest.json` in `db`, and a `.tar.gz` in `files`. Before deleting anything, run the restore drill on the newest backup (step 5) and keep at least the newest three. Delete only the oldest monthly pairs (dump and manifest together, then the `.tar.gz` of the same date). Better long term: a bigger drive.
5. Prove the newest backup restores. The drill only reads it (the `-ConfirmLive` flag says you mean the real folder):
   ```powershell
   pwsh -NoProfile -File C:\CollectionMaxxing\repo\scripts\restore-drill.ps1 -ConfirmLive
   ```
6. Write the date and the `RESULT:` line in the drill log ([../drills/README.md](../drills/README.md)).

## How to know it worked

- Step 3 prints `database dumped to db/collectionmaxxing-<time>.dump` and `files archived to files/files-<time>.tar.gz`, and both files are on F: with today's time.
- The drill prints `RESULT: PASS`.
- The next morning there is no failure email, and the healthchecks.io page shows a green ping after 03:00.
