<#
.SYNOPSIS
  Removes the deployer's scheduled task. Without -Apply it only prints the command.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$TaskName = 'CollectionMaxxingDeployer',
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Cm.Common.ps1')

Initialize-Cm -Apply ($Apply.IsPresent -and -not $WhatIfPreference)
$exists = Invoke-Cm -Command 'schtasks' -Arguments @('/Query', '/TN', $TaskName)
if ($exists.ExitCode -ne 0) { Write-CmLog "no scheduled task named '$TaskName'; nothing to remove"; exit 0 }
$r = Invoke-Cm -Mutating -Command 'schtasks' -Arguments @('/Delete', '/TN', $TaskName, '/F')
exit $r.ExitCode
