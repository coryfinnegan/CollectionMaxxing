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
foreach ($line in $step.Lines) {
  if ($line -match '^(Passed|Failed)!\s+- Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+)') {
    $failed += [int]$Matches[2]; $passed += [int]$Matches[3]; $skipped += [int]$Matches[4]
  }
}
# Where the time went: one line per test project, then the slowest tests (anything over 2s, at most 15).
foreach ($line in $step.Lines) {
  if ($line -match '^(Passed|Failed)!.*Passed:\s+(\d+).*Duration:\s+(.*[a-z]) - ([A-Za-z.]+)\.dll') {
    Write-Host "     $($Matches[4]): $($Matches[2]) tests, $($Matches[3])"
  }
}
$slowest = Get-ChildItem $trxDir -Filter *.trx -ErrorAction SilentlyContinue | ForEach-Object {
  ([xml](Get-Content $_.FullName -Raw)).TestRun.Results.UnitTestResult
} | Where-Object { $_ -and [timespan]$_.duration -ge [timespan]'00:00:02' } |
  Sort-Object { [timespan]$_.duration } -Descending | Select-Object -First 15
if ($slowest) {
  Write-Host '     slowest tests:'
  $slowest | ForEach-Object { Write-Host ('       {0,7:N1}s  {1}' -f ([timespan]$_.duration).TotalSeconds, $_.testName) }
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
