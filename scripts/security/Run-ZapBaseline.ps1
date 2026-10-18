<#
.SYNOPSIS
  OWASP ZAP baseline (passive) scan of the web build served by the API, on a throwaway stack. Quiet output: counts only.

.DESCRIPTION
  Builds the API image from deploy/docker/Dockerfile.api, starts Postgres (tmpfs, no volume) and the API on an isolated Docker
  network (cm-sec-net-<run>), runs ghcr.io/zaproxy/zaproxy:stable against http://api:5480 and tears everything down again.
  Every container, network and image is named cm-sec-* and labelled cm.agent=qa-1c. Raw reports go to the temp folder.
  ZAP is capped at 2 threads per host and a wall-clock limit (default 10 minutes) because the PC may be busy.
  Alerts listed in .zap/rules.tsv are explained there; anything else fails the run.

  -DryRun     print the plan (images, network, ZAP mode, thread cap) and start nothing.
  -Port       a host loopback port for the API (default 0: a random one). 8080, 8409, 32400 and 8096 are refused.
  -KeepStack  leave the stack running afterwards (Stop-Security.ps1 removes it).
  -WebDist    a folder with the Expo web export (default: the placeholder page).
#>
[CmdletBinding()]
param(
    [switch]$DryRun, [int]$Port = 0, [int]$Threads = 2, [int]$MaxMinutes = 10,
    [string]$ReportDir, [string]$WebDist = 'deploy/docker/no-web', [switch]$KeepStack
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Security.Common.ps1')
. (Join-Path $PSScriptRoot 'Security.Stack.ps1')
. (Join-Path $PSScriptRoot 'Security.Zap.ps1')
exit (Invoke-ZapRun -Kind baseline -DryRun:$DryRun -Port $Port -Threads $Threads -MaxMinutes $MaxMinutes -ReportDir $ReportDir -WebDist $WebDist -KeepStack:$KeepStack)
