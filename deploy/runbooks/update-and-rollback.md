# Update, or roll back, a release

## When to use this

The deployer updates the app by itself every 2 minutes. Use this to see why it has not, to update by hand, or to go back to an older version on purpose because the newest one is broken. Background: [../README.md](../README.md), "How the deployer decides".

## Steps

1. Read what happened. The log says why in plain words; an `ALERT deploy ...` line means it refused or rolled back (you also got the failure email if the heartbeat is set up):
   ```powershell
   Get-Content C:\CollectionMaxxing\logs\deployer.log -Tail 30
   (Get-Content C:\CollectionMaxxing\state\deployer-state.json | ConvertFrom-Json).current
   ```
2. To update now, run the deployer by hand. Without `-Apply` it only prints the plan:
   ```powershell
   pwsh -NoProfile -File C:\CollectionMaxxing\repo\deploy\deployer\Deploy-CollectionMaxxing.ps1
   pwsh -NoProfile -File C:\CollectionMaxxing\repo\deploy\deployer\Deploy-CollectionMaxxing.ps1 -Apply
   echo $LASTEXITCODE
   ```
   Exit codes: 0 fine, 1 failed or rolled back, 2 config refused, 3 Docker or GHCR not reachable, 4 refused (not newer, not attested, unreadable state), 5 `.ipa` checksum failed.
3. To go back on purpose, pick the older commit (40 characters) from `git -C C:\CollectionMaxxing\repo log --oneline` after `git -C C:\CollectionMaxxing\repo fetch origin main`, and run the plan first:
   ```powershell
   pwsh -NoProfile -File C:\CollectionMaxxing\repo\deploy\deployer\Deploy-CollectionMaxxing.ps1 -RollbackTo <40-char sha>
   pwsh -NoProfile -File C:\CollectionMaxxing\repo\deploy\deployer\Deploy-CollectionMaxxing.ps1 -RollbackTo <40-char sha> -Apply
   ```
   This swaps the running containers for the older images. It does not delete data and runs no migration (they only ever add). The version you left is remembered as bad so it is not put straight back; a newer commit on `main` deploys normally.
4. Tell the lead or open an issue with the broken version's short hash, so a fix lands as a newer commit.
5. Only if the state file is lost: seed it as described in [../README.md](../README.md) ("If the state file is lost"), then repeat step 2.

## How to know it worked

- The log ends with `deployed <12 hex>; smoke test passed` (or, for a rollback, a line naming the commit you went back to) and the exit code is 0.
- `Invoke-RestMethod http://127.0.0.1:5480/health/ready` prints `Healthy`.
- `current.sha` in the state file is the version you meant.
- No new `ALERT` line appears in the next few minutes.
