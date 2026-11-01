<#
.SYNOPSIS
  Runs one headless Claude Code agent for one issue, to the end. Started by the watcher (detached, in a headless
  console); not meant to be run by hand.

.DESCRIPTION
  The prompt file goes to claude on standard input and everything claude prints is appended to the log. The run is
  stopped, with its whole process tree, when it outlives -MaxMinutes. How it ended is written to -ExitFile for the next
  watcher pass. When -TokenFile names a file holding a long-lived token (claude setup-token), claude gets it in its own
  environment as CLAUDE_CODE_OAUTH_TOKEN; the token is never printed or put on a command line. The last thing the run does is
  start the watcher task named by -WatcherTask (an empty name switches that off), so the next queued issue does not wait for
  the 5-minute poll; see Start-CmAgentWatcher in Cm.Agent.ps1.
#>
param(
    [Parameter(Mandatory)][int]$Issue,
    [Parameter(Mandatory)][string]$Model,
    [Parameter(Mandatory)][string]$Claude,
    [Parameter(Mandatory)][string]$PromptFile,
    [Parameter(Mandatory)][string]$LogFile,
    [Parameter(Mandatory)][string]$ExitFile,
    [Parameter(Mandatory)][string]$WorkDir,
    [int]$MaxMinutes = 180,
    [string]$TokenFile = '',
    [string]$WatcherTask = 'CollectionMaxxingAgent'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../deployer/Cm.Common.ps1')
. (Join-Path $PSScriptRoot 'Cm.Agent.ps1')

$code = @(Invoke-CmAgentRunner -Claude $Claude -Model $Model -PromptFile $PromptFile -LogFile $LogFile -ExitFile $ExitFile -WorkDir $WorkDir -MaxMinutes $MaxMinutes -Issue $Issue -TokenFile $TokenFile -WatcherTask $WatcherTask) | Select-Object -Last 1
exit $code
