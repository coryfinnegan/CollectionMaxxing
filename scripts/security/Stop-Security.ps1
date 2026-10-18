<#
.SYNOPSIS
  Stops and removes everything the security scripts started: exactly the containers (and the networks and images) labelled
  cm.agent=qa-1c. Any other container is left alone, whatever its name.
#>
[CmdletBinding()]
param([switch]$DryRun)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Security.Common.ps1')
try {
    Assert-DockerUp
    if ($DryRun) {
        $ids = @((Invoke-Sec docker @('ps', '-a', '--filter', "label=$($script:Label)", '--format', '{{.Names}}')).Output)
        Write-Sec "dry run: would remove $($ids.Count) container(s) labelled $($script:Label): $($ids -join ', ')"
        exit 0
    }
    $removed = Remove-SecResources
    Write-Sec "ok   removed $($removed.Containers) container(s), $($removed.Networks) network(s), $($removed.Images) image(s) labelled $($script:Label)"
    exit 0
}
catch { Write-Host "FAIL $($_.Exception.Message)"; exit 1 }
