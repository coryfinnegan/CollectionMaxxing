<#
.SYNOPSIS
  The load check (QA-2a): virtual scanners against a throwaway copy of the stack, in Docker. Default profile: smoke.

.DESCRIPTION
  Builds (once) the repo's own API and migration images, starts a database on tmpfs, the migrations, the API and a k6 container on a
  private Docker network, runs the profile, prints a short summary, writes the numbers as JSON into the temp folder and removes
  everything it started. Nothing is published to the host and there is no URL parameter: it cannot be pointed at the live stack.
  Providers can't be reached either (the network is internal), so no real provider is ever called.

  Profiles (scripts/perf/profiles.json):   smoke  2 scanners, 30 s      (what the builder ran)
                                           gaming 5 scanners, 3 min
                                           full   20 scanners, 10 min   (needs  -Profile full -ConfirmFull)

  It stops by itself when p95 stays over 2 s for 30 s or errors pass 10%, and at -MaxMinutes at the latest.
  Stop it early with Ctrl+C here, or  pwsh -NoProfile -File scripts/perf/Stop-Perf.ps1  from any other window.

.EXAMPLE
  pwsh -NoProfile -File scripts/perf/Run-Perf.ps1 -DryRun                 # prints the plan, starts nothing
  pwsh -NoProfile -File scripts/perf/Run-Perf.ps1                         # smoke
  pwsh -NoProfile -File scripts/perf/Run-Perf.ps1 -Profile full -ConfirmFull
#>
[CmdletBinding()]
param(
  [string]$Profile = 'smoke',
  [switch]$ConfirmFull,
  [switch]$DryRun,
  [int]$MaxMinutes = 0,
  [switch]$BuildOnly,
  [switch]$Rebuild
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Perf.psm1') -Force
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path

function Refuse([string]$Message) { Write-Host "refused: $Message"; exit 1 }

# ---- guards (before anything is started) ----------------------------------------------------------------------------------
try {
  $chosen = Resolve-PerfProfile -Name $Profile
  Assert-PerfStartAllowed -Profile $chosen -ConfirmFull:$ConfirmFull
}
catch { Refuse $_.Exception.Message }

$cap = if ($MaxMinutes -gt 0) { $MaxMinutes } else { $chosen.MaxMinutes }
$runId = New-PerfRunId
$out = Join-Path ([System.IO.Path]::GetTempPath()) "collectionmaxxing-perf/$runId"
$secrets = Join-Path $out 'secrets'
$tag = ((& git -C $repo rev-parse --short HEAD 2>$null) | Select-Object -First 1)
if (-not $tag) { $tag = 'dev' }

$plan = Get-PerfPlan -Profile $chosen -RunId $runId -OutDir $out -SecretsDir $secrets -Tag $tag
$build = Get-PerfBuildCommands -Tag $tag -RepoRoot $repo
$unsafe = @(Test-PerfPlanSafe -Plan $plan -BuildCommands $build)
if ($unsafe.Count -gt 0) { Refuse "the plan broke a safety rule: $($unsafe -join '; ')" }

Format-PerfPlan -Plan $plan -BuildCommands $build | ForEach-Object { Write-Host $_ }
Write-Host "hard cap:     $cap min"
if ($DryRun) { Write-Host 'dry run: nothing was started.'; exit 0 }

try { Assert-PerfDocker } catch { Refuse $_.Exception.Message }
$already = @((Invoke-PerfDocker -DockerArgs @('ps', '-a', '-q', '--filter', 'label=cm.agent=qa-2a')).Output | Where-Object { $_ })
if ($already.Count -gt 0) { Refuse 'a perf run is already active (or was left behind). Run scripts/perf/Stop-Perf.ps1 first.' }

# ---- images -------------------------------------------------------------------------------------------------------------------
foreach ($b in $build) {
  $image = $b.Args[$b.Args.IndexOf('-t') + 1]
  $have = (Invoke-PerfDocker -DockerArgs @('image', 'inspect', $image, '--format', 'ok')).Code -eq 0
  if ($have -and -not $Rebuild) { Write-Host "ok   $($b.Step): $image exists"; continue }
  Write-Host "...  $($b.Step) (the first build downloads base images and takes minutes)"
  Push-Location $b.WorkDir
  try { $built = Invoke-PerfDocker -DockerArgs $b.Args } finally { Pop-Location }
  if ($built.Code -ne 0) { Refuse "$($b.Step) failed:`n$(($built.Output | Select-Object -Last 8) -join "`n")" }
  Write-Host "ok   $($b.Step)"
}
if ($BuildOnly) { Write-Host 'images are ready.'; exit 0 }

# ---- the run ---------------------------------------------------------------------------------------------------------------------
$password = New-PerfPassword
$secretValues = @{ DB_PASSWORD = $password; SETUP_CODE = '' }
$exit = 0
New-Item -ItemType Directory -Force -Path $secrets | Out-Null
$process = $null
try {
  New-PerfCertificate -Path (Join-Path $secrets 'signing.pfx')
  New-PerfCertificate -Path (Join-Path $secrets 'encryption.pfx')
  $step = @{}; foreach ($c in $plan.Commands) { $step[$c.Step] = $c }
  $docker = { param($c) $r = Invoke-PerfDocker -DockerArgs (Expand-PerfArgs -CommandArgs $c.Args -Secrets $secretValues); if ($r.Code -ne 0) { throw "$($c.Step) failed: $(($r.Output | Select-Object -Last 6) -join ' | ')" }; $r }

  & $docker $step['network'] | Out-Null
  & $docker $step['database'] | Out-Null
  $ready = $false
  for ($i = 0; $i -lt 40 -and -not $ready; $i++) {
    $ready = (Invoke-PerfDocker -DockerArgs @('exec', $plan.Containers[0], 'pg_isready', '-U', 'collectionmaxxing')).Code -eq 0
    if (-not $ready) { Start-Sleep -Seconds 2 }
  }
  if (-not $ready) { throw 'the database did not become ready in 80 s' }
  & $docker $step['migrate'] | Out-Null
  Write-Host 'ok   database ready, migrations applied'

  & $docker $step['api'] | Out-Null
  $codeFile = Join-Path $secrets 'setup-code.txt'
  $listening = $false
  for ($i = 0; $i -lt 60 -and -not $listening; $i++) {
    $logs = (Invoke-PerfDocker -DockerArgs @('logs', $plan.Containers[1])).Output -join "`n"
    $listening = ($logs -match 'Now listening on') -and (Test-Path -LiteralPath $codeFile)
    if (-not $listening) { Start-Sleep -Seconds 2 }
  }
  if (-not $listening) { throw 'the API did not start in 120 s (docker logs of the cm-perf api container may say why before cleanup)' }
  $secretValues.SETUP_CODE = ((Get-Content -LiteralPath $codeFile -Raw) -split '\s+' | Where-Object { $_ -match '^[A-Za-z0-9-]{8,}$' } | Select-Object -First 1)
  if (-not $secretValues.SETUP_CODE) { throw 'no setup code was written' }
  Write-Host 'ok   API is up; starting k6 (Ctrl+C stops everything)'

  $k6 = Expand-PerfArgs -CommandArgs $step['k6'].Args -Secrets $secretValues
  $psi = [System.Diagnostics.ProcessStartInfo]::new('docker')
  foreach ($a in $k6) { $psi.ArgumentList.Add($a) }
  $psi.UseShellExecute = $false
  $process = [System.Diagnostics.Process]::Start($psi)
  $clock = [System.Diagnostics.Stopwatch]::StartNew()
  $samples = [System.Collections.Generic.List[object]]::new()
  $capped = $false
  while (-not $process.WaitForExit(5000)) {
    $stats = Invoke-PerfDocker -DockerArgs (@('stats', '--no-stream', '--format', '{{json .}}') + $plan.Containers[0], $plan.Containers[1], $plan.Containers[3])
    foreach ($line in $stats.Output) { try { $samples.Add(($line | ConvertFrom-Json)) } catch { } }
    if ($clock.Elapsed.TotalMinutes -ge $cap) {
      $capped = $true
      Write-Host "hard cap of $cap min reached: stopping"
      break
    }
  }
  if ($capped) { $exit = 4 } elseif ($process.ExitCode -ne 0 -and $process.ExitCode -ne 99) { $exit = 4 }

  $summaryFile = Join-Path $out 'k6-summary.json'
  if (Test-Path -LiteralPath $summaryFile) {
    $summary = Get-Content -LiteralPath $summaryFile -Raw | ConvertFrom-Json
    $summary | Add-Member -NotePropertyName runId -NotePropertyValue $runId -Force
    $summary | Add-Member -NotePropertyName containers -NotePropertyValue (Get-PerfStatsSummary -Samples $samples) -Force
    $summary | Add-Member -NotePropertyName capped -NotePropertyValue $capped -Force
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $out 'summary.json')
    Write-Host ''
    Write-Host "result:   $(if ($summary.aborted) { 'ABORTED by an abort rule' } elseif ($summary.thresholdsPassed) { 'thresholds passed' } else { 'thresholds FAILED' })"
    Write-Host "requests: $($summary.requests)   p50 $($summary.p50) ms   p95 $($summary.p95) ms   p99 $($summary.p99) ms   errors $([math]::Round($summary.errorRate * 100, 2))%   429s $($summary.rateLimited)"
    foreach ($n in $summary.calls.PSObject.Properties.Name) {
      $c = $summary.calls.$n
      Write-Host ('  {0,-14} n={1,-6} p50 {2,-7} p95 {3,-7} p99 {4}' -f $n, $c.count, $c.p50, $c.p95, $c.p99)
    }
    foreach ($n in $summary.containers.PSObject.Properties.Name) {
      $c = $summary.containers.$n
      Write-Host ('  {0,-10} cpu avg {1}% max {2}%   mem max {3} MiB' -f $n, $c.cpuAvgPercent, $c.cpuMaxPercent, $c.memMaxMiB)
    }
    Write-Host "json:     $(Join-Path $out 'summary.json')"
    if ($exit -eq 0) { $exit = if ($summary.aborted) { 3 } elseif ($summary.thresholdsPassed) { 0 } else { 2 } }
  }
  else {
    Write-Host 'no summary was written (the run was stopped or k6 failed before the end).'
    if ($exit -eq 0) { $exit = 4 }
  }
}
catch {
  Write-Host "failed: $($_.Exception.Message)"
  $exit = 1
}
finally {
  if ($process -and -not $process.HasExited) { try { $process.Kill() } catch { } }
  [void](Stop-PerfResources -RunId $runId)
  try { Remove-Item -LiteralPath $secrets -Recurse -Force -ErrorAction SilentlyContinue } catch { }
  Write-Host 'stopped'
}
exit $exit
