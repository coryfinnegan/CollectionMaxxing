# Restore drill log

One line per drill, twice a year and after any change to the backup job. The drill (`scripts/restore-drill.ps1`) restores the newest **local nightly backup on F:** into a throwaway container and compares it with the manifest; how to run it, and what a FAIL means, is in [../RESTORE.md](../RESTORE.md). An off-site copy is not part of the drill (it is the optional step 11 in [../README.md](../README.md)).

Record counts, times and PASS or FAIL only: no row data, file names, hostnames or personal details.

## How to add an entry

1. Run `pwsh -NoProfile -File C:\CollectionMaxxing\repo\scripts\restore-drill.ps1 -ConfirmLive` and time it.
2. Add a row to the table below (or a file `deploy/drills/<yyyy-mm-dd>.md` with the same columns if you prefer one file per drill).
3. Paste the `RESULT:` line, and the PASS or FAIL lines if it failed, under the table.

## Log

| Date | Backup age (hours) | Counts (tables / files restored) | Result | Minutes taken | Notes |
|---|---|---|---|---|---|
| yyyy-mm-dd | 6 | 14 tables, 398 files | PASS | 2 | example row, delete when you add the first real one |

## Template for the pasted output

```
Date:
Backup (name of the dump, from the first line of the output):
Backup age (hours):
Counts (every "PASS table ..." line is fine to paste; they hold numbers only):
RESULT: PASS | FAIL
Minutes taken:
What I did that the runbook did not say:
```
