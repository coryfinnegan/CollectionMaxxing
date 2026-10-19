<#
.SYNOPSIS
  Runs every deploy test with one command and prints a quiet summary:  pwsh -NoProfile -File deploy/tests/Run-Tests.ps1

.DESCRIPTION
  Nothing on the real machine is touched: every external command is replaced by a recorder and the scripts run against
  throwaway folders in the temp directory. The compose tests call `docker compose config`, which needs the docker CLI
  but not the daemon. The shell tests (backup) run with bash.

  The one exception is the restore-drill suite (restore-drill.test.sh): it starts real throwaway Postgres containers
  (cm-ops1-test-*, no network, no ports, no volumes, removed afterwards) and so needs a running Docker daemon and the
  postgres:17 image. -SkipDocker leaves that suite out (the rest needs no daemon).

  -DockerBuild additionally builds the three Dockerfiles to prove they work. The images are tagged cm-ops1-test-* and
  only those tags are removed afterwards. Not part of the default run (it downloads base images and takes minutes).
#>
[CmdletBinding()]
param([switch]$DockerBuild, [switch]$SkipDocker)
$ErrorActionPreference = 'Stop'
$env:CM_TEST = '1'   # Invoke-Cm refuses to run anything for real while this is set and no fake runner is installed.
$timer = [System.Diagnostics.Stopwatch]::StartNew()
$deploy = Split-Path -Parent $PSScriptRoot

. (Join-Path $deploy 'deployer/Cm.Common.ps1')
. (Join-Path $deploy 'deployer/Cm.Ipa.ps1')
. (Join-Path $deploy 'deployer/Cm.Deploy.ps1')
. (Join-Path $PSScriptRoot 'TestKit.ps1')

Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.tests.ps1' | Sort-Object Name | ForEach-Object { . $_.FullName }

# ---- shell tests (backup retention, heartbeat, a whole backup run) ------------------------------------------------
function Find-Bash {
    $git = if ($env:ProgramFiles) { @((Join-Path $env:ProgramFiles 'Git/bin/bash.exe'), (Join-Path $env:ProgramFiles 'Git/usr/bin/bash.exe')) } else { @() }
    $candidates = @($git + '/bin/bash' + '/usr/bin/bash') | Where-Object { $_ -and (Test-Path -LiteralPath $_) }
    if ($candidates) { return $candidates | Select-Object -First 1 }
    # Never pick Windows' own bash.exe (System32): that starts WSL.
    $cmd = Get-Command bash -ErrorAction SilentlyContinue | Where-Object { $_.Source -notmatch '\\Windows\\System32\\' } | Select-Object -First 1
    if ($cmd) { return $cmd.Source }
    $null
}
$bash = Find-Bash
# backup.test.sh needs nothing but bash. restore-drill.test.sh starts real throwaway Postgres containers (named
# cm-ops1-test-*, no network, no volumes, removed afterwards), so it needs a running Docker daemon: -SkipDocker leaves it out.
$shellSuites = @('backup.test.sh', 'restore-guard.test.sh')
if (-not $SkipDocker) { $shellSuites += 'restore-drill.test.sh' }
foreach ($suite in $shellSuites) {
    if (-not $bash) {
        Add-ExternalResults -Passed 0 -Failed @("FAIL ${suite}: bash was not found (install Git for Windows, or run on Linux)")
        continue
    }
    $lines = @(& $bash ((Join-Path $PSScriptRoot $suite) -replace '\\', '/') 2>&1 | ForEach-Object { "$_" })
    # (Not named $passed: TestKit is dot-sourced, so its $script:Passed counter lives in this very scope.)
    $shellPassed = @($lines | Where-Object { $_ -like 'PASS *' }).Count
    $shellFailed = @($lines | Where-Object { $_ -like 'FAIL *' })
    if ($shellPassed -eq 0 -and $shellFailed.Count -eq 0) { $shellFailed = @("FAIL $suite ran nothing:`n     $($lines | Select-Object -Last 5 | Out-String)") }
    Add-ExternalResults -Passed $shellPassed -Failed $shellFailed
}

# ---- optional: prove the Dockerfiles build ------------------------------------------------------------------------
if ($DockerBuild) {
    $repo = Split-Path -Parent $deploy
    $builds = @(
        @{ Tag = 'cm-ops1-test-api'; File = 'deploy/docker/Dockerfile.api'; Context = '.' },
        @{ Tag = 'cm-ops1-test-migrate'; File = 'deploy/docker/Dockerfile.migrate'; Context = '.' },
        @{ Tag = 'cm-ops1-test-backup'; File = 'deploy/backup/Dockerfile.backup'; Context = 'deploy/backup' }
    )
    Push-Location $repo
    try {
        foreach ($b in $builds) {
            $out = & docker build --quiet -t $b.Tag -f $b.File $b.Context 2>&1
            if ($LASTEXITCODE -eq 0) { Add-ExternalResults -Passed 1 -Failed @() }
            else { Add-ExternalResults -Passed 0 -Failed @("FAIL docker build $($b.File)`n     $(($out | Select-Object -Last 8) -join "`n     ")") }
            $built = & docker image ls --format '{{.Repository}}' $b.Tag 2>$null
            if ($built -eq $b.Tag) { & docker rmi $b.Tag | Out-Null }   # only our own cm-ops1-test-* tag
        }
    }
    finally { Pop-Location }
}

# ---- summary ---------------------------------------------------------------------------------------------------------
$summary = Get-TestSummary
foreach ($f in $summary.Failures) { Write-Host $f }
$seconds = [math]::Round($timer.Elapsed.TotalSeconds, 1)
if ($summary.Failures.Count -eq 0) {
    Write-Host "deploy tests: $($summary.Passed) passed, 0 failed (${seconds}s)"
    exit 0
}
Write-Host "deploy tests: $($summary.Passed) passed, $($summary.Failures.Count) FAILED (${seconds}s)"
exit 1
