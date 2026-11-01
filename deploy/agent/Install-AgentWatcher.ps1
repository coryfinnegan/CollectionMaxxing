<#
.SYNOPSIS
  Registers (or with -Uninstall, removes) the Windows scheduled task that runs the issue agent's watcher every 5 minutes.

.DESCRIPTION
  Without -Apply (or with -WhatIf) it only prints the exact schtasks command and the folders it would create, like the
  deployer's installer. With -Apply it creates C:\CollectionMaxxing\logs\agent and \state and registers the task for the
  current user: no admin rights, no other account, and it runs while you are signed in. It refuses to replace an
  existing task of that name, and it only ever touches a task whose name starts with CollectionMaxxingAgent.
  Settings live in C:\CollectionMaxxing\.env (CM_AGENT_*, see deploy/.env.example); this script never edits it.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Root = 'C:\CollectionMaxxing',
    [string]$TaskName = 'CollectionMaxxingAgent',
    [switch]$Uninstall,
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../deployer/Cm.Common.ps1')
. (Join-Path $PSScriptRoot 'Cm.Agent.ps1')

Initialize-Cm -Apply ($Apply.IsPresent -and -not $WhatIfPreference)
if ($TaskName -notmatch '^CollectionMaxxingAgent') {
    Write-CmLog "refusing the task name '$TaskName': this script only manages a task named CollectionMaxxingAgent (optionally with a suffix)" 'ERROR'
    exit 2
}
$exists = Invoke-Cm -Command 'schtasks' -Arguments @('/Query', '/TN', $TaskName)

if ($Uninstall) {
    if ($exists.ExitCode -ne 0) { Write-CmLog "no scheduled task named '$TaskName'; nothing to remove"; exit 0 }
    $r = Invoke-Cm -Mutating -Command 'schtasks' -Arguments @('/Delete', '/TN', $TaskName, '/F')
    if ($r.ExitCode -ne 0) { Write-CmLog 'schtasks failed' 'ERROR' }
    exit $r.ExitCode
}

if ($exists.ExitCode -eq 0) {
    Write-CmLog "a scheduled task named '$TaskName' already exists; not overwriting it. Run this script with -Uninstall first." 'ERROR'
    exit 1
}
if (-not (Resolve-CmAgentClaude)) {
    Write-CmLog 'claude was not found on PATH or in the .local\bin folder of this account; the task will start but cannot run an agent until Claude Code is installed' 'WARN'
}
# conhost --headless: the task runs in the signed-in session (gh keeps its token in that user's keyring), and without it
# a console window would flash on screen every 5 minutes.
$action = Get-CmAgentTaskAction -Pwsh (Get-CmAgentPwsh) -Script (Join-Path $PSScriptRoot 'Watch-AgentIssues.ps1') -Root $Root -TaskName $TaskName
$create = @('/Create', '/TN', $TaskName, '/SC', 'MINUTE', '/MO', '5', '/TR', $action, '/RL', 'LIMITED')
$folders = @([IO.Path]::Combine($Root, 'logs', 'agent'), [IO.Path]::Combine($Root, 'state'))
if ($global:CmApply) {
    foreach ($dir in $folders) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
}
else {
    Write-CmLog "DRY RUN: would create $($folders -join ' and ')"
}
$r = Invoke-Cm -Mutating -Command 'schtasks' -Arguments $create
if ($r.ExitCode -ne 0) { Write-CmLog 'schtasks failed' 'ERROR'; exit 1 }
Write-CmLog "undo: pwsh -NoProfile -File $(Join-Path $PSScriptRoot 'Install-AgentWatcher.ps1') -Uninstall -Apply   (or schtasks /Delete /TN $TaskName /F)"
exit 0
