<#
.SYNOPSIS
  OWASP ZAP API scan from contracts/openapi.json, as a throwaway editor, on a throwaway stack. Quiet output: counts only.

.DESCRIPTION
  Same stack as Run-ZapBaseline.ps1 (isolated network, tmpfs Postgres, the API image from deploy/docker/Dockerfile.api, every
  container labelled cm.agent=qa-1c), plus a fake Auth0 in the same network (scripts/fake-auth0: TLS with a per-run CA the API trusts, no
  internet, no weakened setting). It signs a throwaway owner in on the web, makes a library and an editor invite, then signs the editor in
  with a bearer token the harness signs itself, accepts the invite and keeps that token in memory. ZAP gets it through the environment
  (CM_ZAP_TOKEN, read by a ZAP hook), so it is never on a command line, in a file or in a log. The token only works on this one stack.
  ZAP is capped at 2 threads per host and a wall-clock limit (default 10 minutes).

  -DryRun, -Port, -KeepStack, -WebDist: as in Run-ZapBaseline.ps1.
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
exit (Invoke-ZapRun -Kind api -DryRun:$DryRun -Port $Port -Threads $Threads -MaxMinutes $MaxMinutes -ReportDir $ReportDir -WebDist $WebDist -KeepStack:$KeepStack)
