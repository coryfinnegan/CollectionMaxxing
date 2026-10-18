<#
.SYNOPSIS
  Trivy scan (HIGH and CRITICAL) of the API image built from deploy/docker/Dockerfile.api. Quiet output: counts only.

.DESCRIPTION
  Builds the image (tagged cm-sec-api:<run>, labelled cm.agent=qa-1c), saves it to a tar in the temp folder, and scans the tar
  with aquasec/trivy in a container named cm-sec-trivy-<run>. No Docker socket is mounted and nothing is published.
  The vulnerability database is cached in the temp folder (cm-sec-trivy-cache) so repeat runs download less.
  A HIGH or CRITICAL finding that has a fixed version fails the run; one with no fix yet is counted and listed as "unfixed".

  -DryRun  print the plan and start nothing.
#>
[CmdletBinding()]
param([switch]$DryRun, [string]$ReportDir, [string]$WebDist = 'deploy/docker/no-web', [int]$MaxMinutes = 10)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Security.Common.ps1')

function Get-TrivyCounts {
    param([string]$ReportPath)
    $c = [ordered]@{ CRITICAL = 0; HIGH = 0; Fixable = 0; Unfixed = 0 }
    $ids = [System.Collections.Generic.List[string]]::new()
    if (-not (Test-Path -LiteralPath $ReportPath)) { return [pscustomobject]@{ Counts = $c; Ids = @(); Found = $false } }
    $report = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json -Depth 30
    foreach ($result in @($report.Results)) {
        foreach ($v in @($result.Vulnerabilities)) {
            if (-not $v) { continue }
            if ($v.Severity -in 'CRITICAL', 'HIGH') { $c[$v.Severity]++ }
            if ($v.FixedVersion) { $c.Fixable++ } else { $c.Unfixed++ }
            $ids.Add("$($v.VulnerabilityID) $($v.PkgName) [$($v.Severity)$(if (-not $v.FixedVersion) { ', unfixed' })]")
        }
    }
    [pscustomobject]@{ Counts = $c; Ids = @($ids | Select-Object -Unique); Found = $true }
}

$exit = 1; $runId = $null; $work = $null
try {
    $runId = New-RunId
    $reports = Get-ReportDir $runId $ReportDir
    $plan = Get-SecPlan -Mode image-scan -RunId $runId -MaxMinutes $MaxMinutes -ReportDir $reports
    if ($DryRun) { Write-SecPlan $plan; exit 0 }
    Assert-DockerUp
    Write-SecPlan $plan
    $work = Join-Path ([IO.Path]::GetTempPath()) "cm-sec-work-$runId"
    $cache = Join-Path ([IO.Path]::GetTempPath()) 'cm-sec-trivy-cache'
    Assert-OutsideRepo $work; Assert-OutsideRepo $cache
    New-Item -ItemType Directory -Force -Path $work, $cache, $reports | Out-Null
    $image = "$($script:NamePrefix)-api:$runId"
    Build-ApiImage $image $WebDist
    $tar = Join-Path $work 'api.tar'
    $r = Invoke-Sec docker @('save', '-o', $tar, $image)
    if ($r.ExitCode -ne 0) { Stop-SecFailure "docker save failed: $($r.Output -join ' ')" }

    $name = "$($script:NamePrefix)-trivy-$runId"
    $r = Invoke-Sec docker (@('create', '--name', $name) + (Get-LabelArgs) + @('--security-opt', 'no-new-privileges:true', '-v', "${tar}:/in/api.tar:ro", '-v', "${cache}:/root/.cache", $script:TrivyImage,
            'image', '--input', '/in/api.tar', '--severity', 'HIGH,CRITICAL', '--scanners', 'vuln', '--format', 'json', '--output', '/out/trivy.json', '--exit-code', '0', '--quiet'))
    if ($r.ExitCode -ne 0) { Stop-SecFailure "Could not create the Trivy container: $($r.Output -join ' ')" }
    # /out does not exist in the image; make it writable by copying an empty folder in.
    $empty = Join-Path $work 'out'; New-Item -ItemType Directory -Force -Path $empty | Out-Null
    Invoke-Sec docker @('cp', $empty, "${name}:/out") | Out-Null
    Invoke-Sec docker @('start', $name) | Out-Null
    $done = Wait-SecContainer -Name $name -MaxMinutes $MaxMinutes
    Invoke-Sec docker @('cp', "${name}:/out/trivy.json", (Join-Path $reports 'trivy.json')) | Out-Null
    Set-Content -LiteralPath (Join-Path $reports 'trivy.log') -Value (Invoke-Sec docker @('logs', $name)).Output
    $found = Get-TrivyCounts (Join-Path $reports 'trivy.json')
    if (-not $found.Found -or $done.TimedOut -or $done.ExitCode -ne 0) { Stop-SecFailure "Trivy did not finish (exit $($done.ExitCode)$(if ($done.TimedOut) { ', wall-clock cap' })); see $reports" }
    Write-Tally 'trivy HIGH/CRITICAL' @{ CRITICAL = $found.Counts.CRITICAL; HIGH = $found.Counts.HIGH; withFix = $found.Counts.Fixable; unfixed = $found.Counts.Unfixed }
    foreach ($id in $found.Ids) { Write-Sec "      $id" }
    if ($found.Counts.Fixable -gt 0) { Write-Sec "FAIL $($found.Counts.Fixable) HIGH/CRITICAL finding(s) have a fix: update the base image or the package (raw report: $reports)"; $exit = 1 }
    else { Write-Sec "ok   trivy: nothing with a fix available (raw report: $reports)"; $exit = 0 }
}
catch { Write-Host "FAIL $($_.Exception.Message)" }
finally {
    if (-not $DryRun -and $runId) {
        Remove-SecResources | Out-Null
        if ($work -and (Test-Path -LiteralPath $work)) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
    }
}
exit $exit
