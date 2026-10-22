<#
.SYNOPSIS
  The quiet local gate: restore (locked), build (warnings are errors), test, and the app's own check if there is one.

.DESCRIPTION
  Prints one line per step. When a step fails it prints just what you need to fix it (the first lines of each error
  or failing test) and exits non-zero. Needs Docker running: the tests start a throwaway Postgres.

  Run from anywhere:  pwsh scripts/check.ps1
  Full output of the last failing step is kept in the file named in the failure message.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

$solution = 'CollectionMaxxing.slnx'
$maxLines = 15
$logDir = Join-Path ([System.IO.Path]::GetTempPath()) 'collectionmaxxing-check'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

# Runs a command, keeps its output in a log file and returns the exit code, the duration and the lines.
# While it runs, prints a line every 30s (when output arrives) so a long step never looks hung; the test step counts
# finished tests in it.
function Invoke-Step([string]$Name, [scriptblock]$Command) {
  $log = Join-Path $logDir "$Name.log"
  $timer = [System.Diagnostics.Stopwatch]::StartNew()
  $script:finished = 0; $script:lastBeat = 0
  $lines = & $Command 2>&1 | ForEach-Object {
    $line = "$_"
    if ($line -match '^\s+(Passed|Failed|Skipped) ') { $script:finished++ }
    if ($timer.Elapsed.TotalSeconds - $script:lastBeat -ge 30) {
      $script:lastBeat = $timer.Elapsed.TotalSeconds
      $note = if ($script:finished -gt 0) { ", $($script:finished) tests finished" } else { '' }
      Write-Host "     ... $Name running, $([int]$timer.Elapsed.TotalSeconds)s$note"
    }
    $line
  }
  $code = $LASTEXITCODE
  $timer.Stop()
  Set-Content -Path $log -Value $lines
  [pscustomobject]@{ Name = $Name; Code = $code; Seconds = [int][math]::Ceiling($timer.Elapsed.TotalSeconds); Lines = @($lines); Log = $log }
}

function Write-Ok([string]$Message) { Write-Host "ok   $Message" }

function Write-Failure([string]$Message, [string[]]$Details, [string]$Log) {
  Write-Host "FAIL $Message"
  $Details | ForEach-Object { Write-Host "     $_" }
  Write-Host "     (full output: $Log)"
  exit 1
}

# ---- restore --------------------------------------------------------------------------------------------------
$step = Invoke-Step 'restore' { dotnet restore $solution --locked-mode -v q }
if ($step.Code -ne 0) {
  $errors = $step.Lines | Where-Object { $_ -match 'error' } | Select-Object -Unique -First $maxLines
  Write-Failure 'restore (locked mode)' $errors $step.Log
}
Write-Ok "restore (locked mode) ($($step.Seconds)s)"

# ---- build ----------------------------------------------------------------------------------------------------
$step = Invoke-Step 'build' { dotnet build $solution --no-restore -v q -warnaserror }
if ($step.Code -ne 0) {
  # The same error shows up once per target framework and per project; keep each message once.
  $errors = $step.Lines | Where-Object { $_ -match ': (error|warning) ' } |
    ForEach-Object { $_ -replace '\s*\[[^\]]*\]\s*$', '' } | Select-Object -Unique -First $maxLines
  Write-Failure 'build' $errors $step.Log
}
Write-Ok "build, warnings as errors ($($step.Seconds)s)"

# ---- format ---------------------------------------------------------------------------------------------------
$step = Invoke-Step 'format' { dotnet format $solution --verify-no-changes --no-restore }
if ($step.Code -ne 0) {
  $files = $step.Lines | Where-Object { $_ -match ': error ' } | ForEach-Object { $_ -replace '\s*\[[^\]]*\]\s*$', '' } |
    Select-Object -Unique -First $maxLines
  Write-Failure 'format (fix with: dotnet format CollectionMaxxing.slnx)' $files $step.Log
}
Write-Ok "format ($($step.Seconds)s)"

# ---- test -----------------------------------------------------------------------------------------------------
# Normal console verbosity logs every test as it finishes (with its time); the trx files give the slowest-tests report.
$trxDir = Join-Path $logDir 'trx'
Remove-Item -Recurse -Force $trxDir -ErrorAction SilentlyContinue
$step = Invoke-Step 'test' { dotnet test $solution --no-build --logger 'console;verbosity=normal' --logger 'trx;LogFilePrefix=check' --results-directory $trxDir }
$passed = 0; $failed = 0; $skipped = 0
# Normal verbosity prints a "Passed: N" block per project (Failed and Skipped only when non-zero); minimal prints
# "Passed! - Failed: ..." lines. Count whichever is there.
foreach ($line in $step.Lines) {
  if ($line -match '^(Passed|Failed)!\s+- Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+)') {
    $failed += [int]$Matches[2]; $passed += [int]$Matches[3]; $skipped += [int]$Matches[4]
  }
  elseif ($line -match '^\s+Passed:\s+(\d+)\s*$') { $passed += [int]$Matches[1] }
  elseif ($line -match '^\s+Failed:\s+(\d+)\s*$') { $failed += [int]$Matches[1] }
  elseif ($line -match '^\s+Skipped:\s+(\d+)\s*$') { $skipped += [int]$Matches[1] }
}
# Where the time went: one line per test project, a histogram of test times, then the slowest classes and tests.
foreach ($line in $step.Lines) {
  if ($line -match 'xUnit\.net ([0-9:.]+)\]\s+Finished:\s+([A-Za-z.]+) ') { Write-Host "     $($Matches[2]): $($Matches[1])" }
}
$results = @(Get-ChildItem $trxDir -Filter *.trx -ErrorAction SilentlyContinue | ForEach-Object {
  ([xml](Get-Content $_.FullName -Raw)).TestRun.Results.UnitTestResult
} | Where-Object { $_ } | ForEach-Object {
  $name = $_.testName -replace '\(.*$', ''
  [pscustomobject]@{ Name = $name; Class = $name -replace '\.[^.]*$', ''; Seconds = ([timespan]$_.duration).TotalSeconds }
})
if ($results.Count -gt 0) {
  Write-Host ('     {0} tests, {1:N0}s of test time in total' -f $results.Count, ($results | Measure-Object Seconds -Sum).Sum)
  $results | Group-Object { if ($_.Seconds -lt 0.1) { '1 under 0.1s' } elseif ($_.Seconds -lt 0.5) { '2 0.1-0.5s' } elseif ($_.Seconds -lt 1) { '3 0.5-1s' } elseif ($_.Seconds -lt 2) { '4 1-2s' } else { '5 2s and over' } } |
    Sort-Object Name | ForEach-Object { Write-Host ('       {0,-14} {1,5} tests {2,7:N0}s' -f $_.Name.Substring(2), $_.Count, ($_.Group | Measure-Object Seconds -Sum).Sum) }
  Write-Host '     slowest classes (total time):'
  $results | Group-Object Class | ForEach-Object { [pscustomobject]@{ Class = $_.Name; Count = $_.Count; Seconds = ($_.Group | Measure-Object Seconds -Sum).Sum } } |
    Sort-Object Seconds -Descending | Select-Object -First 12 | ForEach-Object { Write-Host ('       {0,7:N1}s {1,5} tests  {2}' -f $_.Seconds, $_.Count, $_.Class) }
  Write-Host '     slowest tests:'
  $results | Sort-Object Seconds -Descending | Select-Object -First 10 | Where-Object { $_.Seconds -ge 2 } |
    ForEach-Object { Write-Host ('       {0,7:N1}s  {1}' -f $_.Seconds, $_.Name) }
}
if ($step.Code -ne 0 -or $failed -gt 0) {
  $details = @()
  for ($i = 0; $i -lt $step.Lines.Count; $i++) {
    if ($step.Lines[$i] -match '^\s+Failed \S') {
      $details += $step.Lines[$i].Trim()
      $details += $step.Lines[($i + 1)..([math]::Min($i + $maxLines - 1, $step.Lines.Count - 1))] |
        Where-Object { $_.Trim() -ne '' } | ForEach-Object { "  $($_.Trim())" }
    }
  }
  if ($details.Count -eq 0) {
    # Nothing ran at all (a crash, or Docker is not running): show the tail instead.
    $details = $step.Lines | Where-Object { $_.Trim() -ne '' } | Select-Object -Last $maxLines
  }
  Write-Failure "test ($passed passed, $failed failed, $skipped skipped)" $details $step.Log
}
if ($skipped -gt 0) {
  Write-Failure "test: $skipped tests were skipped (skipped tests are not allowed)" @() $step.Log
}
if ($passed -eq 0) {
  Write-Failure 'test: no tests ran' ($step.Lines | Select-Object -Last $maxLines) $step.Log
}
Write-Ok "test: $passed passed ($($step.Seconds)s)"

# ---- app (only once the Expo app exists) --------------------------------------------------------------------
if (Test-Path 'app/package.json') {
  $step = Invoke-Step 'app' { npm run check --prefix app --silent }
  if ($step.Code -ne 0) {
    Write-Failure 'app (npm run check)' ($step.Lines | Where-Object { $_.Trim() -ne '' } | Select-Object -Last $maxLines) $step.Log
  }
  Write-Ok "app: npm run check ($($step.Seconds)s)"
}

Write-Host 'all green'
