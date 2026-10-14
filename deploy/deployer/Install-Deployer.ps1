<#
.SYNOPSIS
  Registers the Windows scheduled task that runs the deployer every 2 minutes. OWNER CHECKPOINT: nobody has run this yet.

.DESCRIPTION
  Without -Apply (or with -WhatIf) it only prints the exact schtasks command and the folders it would create.
  With -Apply it creates C:\CollectionMaxxing\logs and \state, then registers the task for the current user (no admin
  needed; the task runs while you are signed in, and Docker Desktop starts at sign-in too). It refuses to overwrite a
  task of the same name. Undo with Uninstall-Deployer.ps1.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Root = 'C:\CollectionMaxxing',
    [string]$TaskName = 'CollectionMaxxingDeployer',
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Cm.Common.ps1')

$doApply = $Apply.IsPresent -and -not $WhatIfPreference
Initialize-Cm -Apply $doApply
$deployScript = Join-Path $PSScriptRoot 'Deploy-CollectionMaxxing.ps1'
$pwsh = Join-Path $PSHOME 'pwsh.exe'
$action = "`"$pwsh`" -NoProfile -ExecutionPolicy Bypass -File `"$deployScript`" -Root `"$Root`" -Apply"
$create = @('/Create', '/TN', $TaskName, '/SC', 'MINUTE', '/MO', '2', '/TR', $action, '/RL', 'LIMITED')

$exists = Invoke-Cm -Command 'schtasks' -Arguments @('/Query', '/TN', $TaskName)
if ($exists.ExitCode -eq 0) {
    Write-CmLog "a scheduled task named '$TaskName' already exists; not overwriting it. Run Uninstall-Deployer.ps1 first." 'ERROR'
    exit 1
}
if ($doApply) {
    foreach ($dir in 'logs', 'state') { New-Item -ItemType Directory -Force -Path (Join-Path $Root $dir) | Out-Null }
}
else {
    Write-CmLog "DRY RUN: would create $(Join-Path $Root 'logs') and $(Join-Path $Root 'state')"
}
$r = Invoke-Cm -Mutating -Command 'schtasks' -Arguments $create
if ($r.ExitCode -ne 0) { Write-CmLog 'schtasks failed' 'ERROR'; exit 1 }
Write-CmLog "undo: schtasks /Delete /TN $TaskName /F   (or run Uninstall-Deployer.ps1 -Apply)"
exit 0
