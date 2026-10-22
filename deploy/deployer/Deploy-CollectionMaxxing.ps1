<#
.SYNOPSIS
  The pull-based deployer. A scheduled task runs it every 2 minutes with -Apply (see Install-Deployer.ps1).

.DESCRIPTION
  Finds the newest main image on GHCR, checks its build attestation (built by this repo's release workflow from
  refs/heads/main), refuses to go backwards (the new commit must descend from the deployed one: git merge-base
  --is-ancestor; an unknown deployed commit is refused), migrates, restarts the api, smoke tests /health/ready and, if
  that fails, pins the previous good tag and restarts. A second run while one is active exits at once.

  After a good deploy of commit X the deployer's own clone is fast-forwarded to X (git merge --ff-only; skipped with a
  WARN if the clone has local changes or is not on main), so its scripts follow the build. On a server that still has the
  .ipa it served before the iPhone app moved to TestFlight (ADR-024), a run removes it from the files volume, once.

  Refusals are loud: a refused, failed or rolled-back deploy exits non-zero, writes an ALERT line to the log and pings
  <CM_HEARTBEAT_URL>/fail (the free check the nightly backup already uses).

  Without -Apply this script changes nothing: it checks the config and prints the commands a real run would make.
  -WhatIf does the same.
  Exit codes: 0 done or nothing to do, 1 deploy failed or rolled back, 2 config refused, 3 Docker or GHCR not reachable,
  4 refused (not a descendant, not attested, unreadable state).

.PARAMETER Apply
  Really deploy. The scheduled task passes this; running by hand without it is safe.

.PARAMETER RollbackTo
  The explicit rollback path, the only way back to an older commit: a full 40 character commit hash that is an
  ancestor of the deployed one. Its images must be attested like any other; no migration runs. The commit rolled back
  from is not redeployed until a newer one lands on main. Needs -Apply to do anything.

.EXAMPLE
  pwsh -NoProfile -File Deploy-CollectionMaxxing.ps1            # dry run: prints the plan

.EXAMPLE
  pwsh -NoProfile -File Deploy-CollectionMaxxing.ps1 -RollbackTo <sha>          # dry run of a rollback
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Root = 'C:\CollectionMaxxing',
    [string]$ComposeFile = '',
    [string]$EnvFile = '',
    [string]$Repo = 'coryfinnegan/CollectionMaxxing',
    [int]$SmokeRetries = 12,
    [int]$SmokeDelaySeconds = 5,
    [string]$RollbackTo = '',
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Cm.Common.ps1')
. (Join-Path $PSScriptRoot 'Cm.Deploy.ps1')

$doApply = $Apply.IsPresent -and -not $WhatIfPreference
Initialize-Cm -Apply $doApply
Use-CmPortableTools -Root $Root
$ctx = New-CmContext -Root $Root -ComposeFile $ComposeFile -EnvFile $EnvFile -Repo $Repo -SmokeRetries $SmokeRetries -SmokeDelaySeconds $SmokeDelaySeconds
$result = Invoke-CmDeploy -Ctx $ctx -RollbackTo $RollbackTo
exit $result.ExitCode
