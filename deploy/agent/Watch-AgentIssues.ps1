<#
.SYNOPSIS
  The issue agent's watcher. A scheduled task runs it every 5 minutes with -Apply (see Install-AgentWatcher.ps1).

.DESCRIPTION
  Plain PowerShell plus `gh`; it never starts a model by itself. Each pass:
    1. reads C:\CollectionMaxxing\.env (CM_AGENT_ENABLED, CM_AGENT_REPO, CM_AGENT_MAX_RUNS_PER_DAY, CM_AGENT_MAX_MINUTES);
       with the agent switched off it exits at once;
    2. closes out an earlier run whose process is gone (or kills one past its time limit) and marks its issue
       agent-blocked when it did not finish;
    3. stops when a run is still alive or the daily cap is reached;
    4. otherwise asks GitHub for open issues labelled agent-task (one call), picks the most urgent one written by the
       repo owner, claims it (agent-working), makes a worktree from origin/main in the owner's checkout and starts
       `claude -p` on it, detached, in a headless console. The watcher then exits; the next pass finds the run done.

  Without -Apply this script changes nothing: it makes the read-only gh calls and prints what a real pass would do.
  -WhatIf does the same.
  Exit codes: 0 fine or nothing to do, 1 a run could not be started, 2 config refused (no checkout, no claude),
  3 GitHub not reachable.

.EXAMPLE
  pwsh -NoProfile -File Watch-AgentIssues.ps1            # dry run: prints what it would do
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Root = 'C:\CollectionMaxxing',
    [string]$Repo = 'coryfinnegan/CollectionMaxxing',
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../deployer/Cm.Common.ps1')
. (Join-Path $PSScriptRoot 'Cm.Agent.ps1')

Initialize-Cm -Apply ($Apply.IsPresent -and -not $WhatIfPreference)
Use-CmPortableTools -Root $Root
$ctx = New-CmAgentContext -Root $Root -Repo $Repo -AgentDir $PSScriptRoot
$result = @(Invoke-CmAgentWatch -Ctx $ctx) | Select-Object -Last 1
exit $result.ExitCode
