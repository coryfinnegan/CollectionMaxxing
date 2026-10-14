<#
.SYNOPSIS
  The pull-based deployer. A scheduled task runs it every 2 minutes with -Apply (see Install-Deployer.ps1).

.DESCRIPTION
  Finds the newest main image on GHCR, checks its build attestation (built by this repo's release workflow from
  refs/heads/main), refuses downgrades, migrates, restarts the api, smoke tests /health/ready and, if that fails,
  pins the previous good tag and restarts. A second run while one is active exits at once.

  Without -Apply this script changes nothing: it checks the config and prints the commands a real run would make.
  -WhatIf does the same. Exit codes: 0 done or nothing to do, 1 deploy failed or rolled back, 2 config refused,
  3 Docker or GHCR not reachable.

.PARAMETER Apply
  Really deploy. The scheduled task passes this; running by hand without it is safe.

.EXAMPLE
  pwsh -NoProfile -File Deploy-CollectionMaxxing.ps1            # dry run: prints the plan
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Root = 'C:\CollectionMaxxing',
    [string]$ComposeFile = '',
    [string]$EnvFile = '',
    [string]$Repo = 'coryfinnegan/CollectionMaxxing',
    [int]$SmokeRetries = 12,
    [int]$SmokeDelaySeconds = 5,
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Cm.Common.ps1')
. (Join-Path $PSScriptRoot 'Cm.Ipa.ps1')
. (Join-Path $PSScriptRoot 'Cm.Deploy.ps1')

$doApply = $Apply.IsPresent -and -not $WhatIfPreference
Initialize-Cm -Apply $doApply
$ctx = New-CmContext -Root $Root -ComposeFile $ComposeFile -EnvFile $EnvFile -Repo $Repo -SmokeRetries $SmokeRetries -SmokeDelaySeconds $SmokeDelaySeconds
$result = Invoke-CmDeploy -Ctx $ctx
exit $result.ExitCode
