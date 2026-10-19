# Runbooks: "what do I do when..."

One screen each: numbered steps, exact commands, and how to know it worked. Written for the owner of the PC, a year from now, with no memory of the details. Nothing here prunes Docker, removes containers or volumes, or deletes data without saying exactly what and asking you to check first.

| When | Runbook |
|---|---|
| The PC rebooted, or Docker Desktop is off | [reboot-or-docker-off.md](reboot-or-docker-off.md) |
| A new version is out, or the latest one is broken | [update-and-rollback.md](update-and-rollback.md) |
| The "missed backup" email arrived, or F: is full | [missed-backup-or-full-disk.md](missed-backup-or-full-disk.md) |
| A phone is lost or stolen; someone leaves the family | [lost-phone-or-remove-member.md](lost-phone-or-remove-member.md) |
| Yearly: replace the token signing keys | [rotate-signing-keys.md](rotate-signing-keys.md) |
| The app is unreachable over Tailscale, or the HTTPS certificate is wrong | [tailscale-and-https.md](tailscale-and-https.md) |
| AltStore will not re-sign; setting up a family member's iPhone | [altstore-resigning.md](altstore-resigning.md) |
| Cutting v1.0 and the first week of real use (the owner's to-do list) | [v1-release-and-week-of-use.md](v1-release-and-week-of-use.md) |
| The restore drill (twice a year) and a real restore | [../RESTORE.md](../RESTORE.md), log in [../drills/README.md](../drills/README.md) |

Other references, not repeated here: the install checklist and how the deployer decides ([../README.md](../README.md)); the load check ([../../docs/perf/README.md](../../docs/perf/README.md)); the security scans and checklists ([../../docs/security/asvs-l2-checklist.md](../../docs/security/asvs-l2-checklist.md)); the iPhone accessibility pass ([../../docs/accessibility/ios-checklist.md](../../docs/accessibility/ios-checklist.md)).

## The shell preamble (used by several runbooks)

Open PowerShell 7 and paste this first. It reads which version is running and sets up a short name for the compose command. It changes nothing.

```powershell
$env:CM_TAG = (Get-Content C:\CollectionMaxxing\state\deployer-state.json | ConvertFrom-Json).current.sha
$cm = @('compose', '-p', 'collectionmaxxing', '-f', 'C:\CollectionMaxxing\repo\deploy\docker-compose.yml', '--env-file', 'C:\CollectionMaxxing\.env')
docker @cm ps
```

You should see `postgres`, `api`, `aspire-dashboard` and `backup` running. If the state file is missing, see "If the state file is lost" in [../README.md](../README.md).

## Rules that hold for every runbook

- Stop, never delete: `docker @cm stop` keeps the data. Taking the stack apart with the volumes flag would delete the database; never do that.
- Only touch containers of the `collectionmaxxing` project (and `cm-drill-*` ones the drill leaves behind). Plex, ErsatzTV, Jellyfin and SABnzbd are not yours to stop here.
- If a step does not look like its "How to know it worked", stop and ask rather than trying the next idea.
