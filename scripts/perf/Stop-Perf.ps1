<#
.SYNOPSIS
  Stops a load check from any window: removes exactly the containers and networks labelled cm.agent=qa-2a and prints "stopped".

.DESCRIPTION
  Anything without that label (Plex, SABnzbd, Jellyfin, the live stack, other agents' containers) is never touched. Run-Perf.ps1
  notices that its containers are gone and ends too. -RemoveImages also removes the images the runner built (also by label).

  pwsh -NoProfile -File scripts/perf/Stop-Perf.ps1
#>
[CmdletBinding()]
param([switch]$RemoveImages)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Perf.psm1') -Force

try { Assert-PerfDocker } catch { Write-Host "refused: $($_.Exception.Message)"; exit 1 }
$gone = Stop-PerfResources -RemoveImages:$RemoveImages
Write-Host "removed $($gone.Containers) container(s) and $($gone.Networks) network(s)"
Write-Host 'stopped'
