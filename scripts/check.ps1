<#
.SYNOPSIS
  The quiet local gate: restore (locked), build (warnings are errors), format, test, and the app's own check if there is one.

.DESCRIPTION
  Prints one line per step (one line per test project inside the test phase). When a step fails it prints just what you
  need to fix it (the first lines of each error or failing test) and exits non-zero. Needs Docker running: the tests
  start a throwaway Postgres per test project that needs one.

  Run from anywhere:  pwsh scripts/check.ps1 [-Phase phase ...]
  Phases are restore, build, format, test and app; with no arguments all of them run. CI runs one phase per step so
  timings and failures localize (the backend job skips the app phase; the app job owns npm run check).
  Full output of the last failing step is kept in the file named in the failure message.

  Environment: CM_TEST_ONLY limits the test phase to the named test projects (whitespace-separated basenames).
  CM_TEST_FILTER hands a VSTest filter expression (`dotnet test --filter`) to every selected project; see check.sh for
  the syntax. A filter that matches nothing fails, because an empty CI shard would hide a coverage gap.
  CM_SHARD_REPORT (a file path, with CM_TEST_FILTER) records "project listed-in-shard listed-in-suite ran" per project.
#>
[CmdletBinding()]
param(
  [string[]]$Phase
)

$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

$solution = 'CollectionMaxxing.slnx'
$maxLines = 15
$logDir = Join-Path ([System.IO.Path]::GetTempPath()) 'collectionmaxxing-check'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$scriptTimer = [System.Diagnostics.Stopwatch]::StartNew()

# The phases to run. No arguments = the whole gate.
$allPhases = @('restore', 'build', 'format', 'test', 'app')
$fullRun = (-not $Phase) -or ($Phase.Count -eq 0)
if ($fullRun) {
  $Phase = $allPhases
}
foreach ($p in $Phase) {
  if ($p -notin $allPhases) {
    Write-Host "unknown phase '$p' (want: restore build format test app)"
    exit 2
  }
}
function Test-Want([string]$Name) { $Phase -contains $Name }

# Runs a command, keeps its output in a log file and returns the exit code, the duration and the lines.
function Invoke-Step([string]$Name, [scriptblock]$Command) {
  $log = Join-Path $logDir "$Name.log"
  $timer = [System.Diagnostics.Stopwatch]::StartNew()
  $lines = & $Command 2>&1 | ForEach-Object { "$_" }
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
if (Test-Want 'restore') {
$step = Invoke-Step 'restore' { dotnet restore $solution --locked-mode -v q }
if ($step.Code -ne 0) {
  $errors = $step.Lines | Where-Object { $_ -match 'error' } | Select-Object -Unique -First $maxLines
  Write-Failure 'restore (locked mode)' $errors $step.Log
}
Write-Ok "restore (locked mode) ($($step.Seconds)s)"
}

# ---- build ----------------------------------------------------------------------------------------------------
if (Test-Want 'build') {
$step = Invoke-Step 'build' { dotnet build $solution --no-restore -v q -warnaserror }
if ($step.Code -ne 0) {
  # The same error shows up once per target framework and per project; keep each message once.
  $errors = $step.Lines | Where-Object { $_ -match ': (error|warning) ' } |
    ForEach-Object { $_ -replace '\s*\[[^\]]*\]\s*$', '' } | Select-Object -Unique -First $maxLines
  Write-Failure 'build' $errors $step.Log
}
Write-Ok "build, warnings as errors ($($step.Seconds)s)"
}

# ---- format ---------------------------------------------------------------------------------------------------
if (Test-Want 'format') {
# One project at a time, not solution-wide: `dotnet format` intermittently crashes inside Roslyn's analyzer-config
# lookup (OneToOneUnicodeComparer.GetHashCode via AnalyzerFormatter.FilterAnalyzersAsync: NullReferenceException on
# the .slnx, IndexOutOfRangeException per-project; see issue #308). Per-project runs crash less often, and a run that
# does crash is retried below; only output with the CLR crash banner is retried, so genuine formatting violations
# still fail immediately. Same coverage as the solution-wide run.
$formatLog = Join-Path $logDir 'format.log'
Remove-Item -Force -ErrorAction SilentlyContinue $formatLog
$formatTimer = [System.Diagnostics.Stopwatch]::StartNew()
$formatFailed = @()
$maxAttempts = 3
foreach ($proj in @((Get-ChildItem 'src/*/*.csproj', 'tests/*/*.csproj' | Sort-Object FullName))) {
  $attempt = 0
  $verified = $false
  while ((-not $verified) -and ($attempt -lt $maxAttempts)) {
    $attempt++
    # Windows PowerShell 5.1 throws NativeCommandError on merged native stderr while $ErrorActionPreference is
    # Stop (the ForEach stringify does not save it); dotnet format's flaky workspace warning (exit 0) hit this.
    # Run the call under Continue so stderr lines land in $attemptLines as text (#398).
    $prevErr = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
      $attemptLines = @(dotnet format $proj.FullName --verify-no-changes --no-restore 2>&1 | ForEach-Object { "$_" })
      $code = $LASTEXITCODE
    }
    finally {
      $ErrorActionPreference = $prevErr
    }
    $attemptLines | Add-Content -Path $formatLog
    if ($code -eq 0) {
      $verified = $true
    } elseif ($attemptLines -match 'Unhandled exception') {
      if ($attempt -lt $maxAttempts) {
        Write-Host "     format: $($proj.BaseName) hit the Roslyn crash, retrying (attempt $($attempt + 1)/$maxAttempts; see issue #308)"
      }
    } else {
      break
    }
  }
  if (-not $verified) { $formatFailed += $proj.BaseName }
}
$formatTimer.Stop()
if ($formatFailed.Count -gt 0) {
  $formatLines = @((Get-Content -Path $formatLog -ErrorAction SilentlyContinue | ForEach-Object { "$_" }))
  $files = $formatLines | Where-Object { $_ -match ': error ' } | ForEach-Object { $_ -replace '\s*\[[^\]]*\]\s*$', '' } |
    Select-Object -Unique -First $maxLines
  if (-not $files) {
    # A crash (or any failure without file diagnostics): show the tail instead.
    $files = @($formatLines | Where-Object { $_.Trim() -ne '' } | Select-Object -Last $maxLines)
  }
  Write-Failure "format ($($formatFailed -join ', '); fix with: dotnet format <project>.csproj)" $files $formatLog
}
Write-Ok "format ($([int][math]::Ceiling($formatTimer.Elapsed.TotalSeconds))s)"
}

# ---- test -----------------------------------------------------------------------------------------------------
if (Test-Want 'test') {
# Every tests/ project except the two support libraries (see tests/Directory.Build.targets), each in its own process at
# the same time. A project that needs Postgres starts its own container, so lanes never see each other's data.
$skipLibs = @('CollectionMaxxing.TestSupport', 'CollectionMaxxing.Api.TestKit')
$lanes = @(Get-ChildItem 'tests/*/*.csproj' | Where-Object { $_.BaseName -notin $skipLibs } | Sort-Object FullName)
if ($env:CM_TEST_ONLY) {
  $only = @($env:CM_TEST_ONLY -split '\s+' | Where-Object { $_ })
  $lanes = @($lanes | Where-Object { $_.BaseName -in $only })
  if ($lanes.Count -eq 0) {
    Write-Host "test: CM_TEST_ONLY matched no test projects ('$($env:CM_TEST_ONLY)')"
    exit 2
  }
}
$testFilter = $env:CM_TEST_FILTER
$listedCounts = @{}
$totalCounts = @{}
if ($testFilter) {
  # Discovery only (nothing runs, no Docker needed): how many tests the filter selects, and how many the project has.
  function Get-ListedCount([string]$Proj, [string]$Filter, [string]$Log) {
    $listArgs = @('--no-build', '--list-tests')
    if ($Filter) { $listArgs += @('--filter', $Filter) }
    $out = @(& dotnet test $Proj @listArgs 2>&1 | ForEach-Object { "$_" })
    Set-Content -Path $Log -Value $out
    @($out | Where-Object { $_ -match '^    \S' }).Count
  }
  if ($env:CM_SHARD_REPORT) { Remove-Item -Force -ErrorAction SilentlyContinue $env:CM_SHARD_REPORT }
  foreach ($lane in $lanes) {
    $listLog = Join-Path $logDir ("list-" + $lane.BaseName + ".log")
    $listedCounts[$lane.BaseName] = Get-ListedCount $lane.FullName $testFilter $listLog
    if ($listedCounts[$lane.BaseName] -eq 0) {
      Write-Failure "test $($lane.BaseName): CM_TEST_FILTER matches no tests (an empty shard would hide a coverage gap)" @("filter: $testFilter") $listLog
    }
    if ($env:CM_SHARD_REPORT) {
      $totalCounts[$lane.BaseName] = Get-ListedCount $lane.FullName '' (Join-Path $logDir ("list-" + $lane.BaseName + "-all.log"))
    }
  }
}
$perfLog = Join-Path $logDir 'postgres-perf.log'
Remove-Item -Force -ErrorAction SilentlyContinue $perfLog
$root = (Get-Location).Path
$wallTimer = [System.Diagnostics.Stopwatch]::StartNew()
$jobs = foreach ($lane in $lanes) {
  Start-Job -ScriptBlock {
    param($Root, $Proj, $Log, $PerfLog, $Filter)
    Set-Location $Root
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:CM_PERF_LOG = $PerfLog
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    $testArgs = @('--no-build', '--logger', 'console;verbosity=minimal')
    if ($Filter) { $testArgs += @('--filter', $Filter) }
    dotnet test $Proj @testArgs > $Log 2>&1
    $code = $LASTEXITCODE
    $timer.Stop()
    [pscustomobject]@{ Name = [System.IO.Path]::GetFileNameWithoutExtension($Proj); Code = $code; Seconds = [int][math]::Ceiling($timer.Elapsed.TotalSeconds) }
  } -ArgumentList @($root, $lane.FullName, (Join-Path $logDir ("test-" + $lane.BaseName + ".log")), $perfLog, $testFilter)
}
$jobs | Wait-Job | Out-Null
$wallTimer.Stop()
$testWall = [int][math]::Ceiling($wallTimer.Elapsed.TotalSeconds)
$results = @($jobs | Receive-Job)
$jobs | Remove-Job | Out-Null

$passed = 0; $failed = 0; $skipped = 0; $skippedLanes = @()
foreach ($lane in $lanes) {
  $name = $lane.BaseName
  $log = Join-Path $logDir "test-$name.log"
  $result = $results | Where-Object { $_.Name -eq $name } | Select-Object -First 1
  $lines = @(Get-Content -Path $log -ErrorAction SilentlyContinue | ForEach-Object { "$_" })
  $lpassed = 0; $lfailed = 0; $lskipped = 0
  foreach ($line in $lines) {
    if ($line -match '^(Passed|Failed)!\s+- Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+)') {
      $lfailed += [int]$Matches[2]; $lpassed += [int]$Matches[3]; $lskipped += [int]$Matches[4]
    }
  }
  if ((-not $result) -or ($result.Code -ne 0) -or ($lfailed -gt 0)) {
    $details = @()
    for ($i = 0; $i -lt $lines.Count; $i++) {
      if ($lines[$i] -match '^\s+Failed \S') {
        $details += $lines[$i].Trim()
        $details += $lines[($i + 1)..([math]::Min($i + $maxLines - 1, $lines.Count - 1))] |
          Where-Object { $_.Trim() -ne '' } | ForEach-Object { "  $($_.Trim())" }
      }
    }
    if ($details.Count -eq 0) {
      # Nothing ran at all (a crash, or Docker is not running): show the tail instead.
      $details = @($lines | Where-Object { $_.Trim() -ne '' } | Select-Object -Last $maxLines)
    }
    Write-Failure "test $name ($lpassed passed, $lfailed failed, $lskipped skipped)" $details $log
  }
  if ($testFilter -and ($lpassed -eq 0)) {
    Write-Failure "test ${name}: CM_TEST_FILTER ran no tests (an empty shard would hide a coverage gap)" @("filter: $testFilter") $log
  }
  Write-Ok "test ${name}: $lpassed passed ($($result.Seconds)s)"
  if ($testFilter) {
    $total = if ($totalCounts.ContainsKey($name)) { $totalCounts[$name] } else { '?' }
    Write-Host "     shard: $($listedCounts[$name]) of $total listed tests"
    if ($env:CM_SHARD_REPORT) {
      New-Item -ItemType Directory -Force -Path (Split-Path -Parent $env:CM_SHARD_REPORT) | Out-Null
      Add-Content -Path $env:CM_SHARD_REPORT -Value "$name $($listedCounts[$name]) $total $lpassed"
    }
  }
  $failed += $lfailed; $passed += $lpassed; $skipped += $lskipped
  if ($lskipped -gt 0) { $skippedLanes += $name }
}
if ($skipped -gt 0) {
  Write-Failure "test: $skipped tests were skipped in $($skippedLanes -join ', ') (skipped tests are not allowed)" @() (Join-Path $logDir 'test-CollectionMaxxing.Api.Tests.log')
}
if ($passed -eq 0) {
  Write-Failure 'test: no tests ran' (@(Get-Content -Path (Join-Path $logDir ("test-" + $lanes[0].BaseName + ".log")) | Select-Object -Last $maxLines)) (Join-Path $logDir ("test-" + $lanes[0].BaseName + ".log"))
}
Write-Ok "test: $passed passed in $($lanes.Count) projects (${testWall}s wall)"
if (Test-Path $perfLog) {
  $perfLines = @(Get-Content -Path $perfLog | Where-Object { $_ -match '\[cm-perf\]' })
  $startup = 0.0
  foreach ($line in $perfLines) {
    if ($line -match 'ready in ([0-9.]+)s') { $startup += [double]$Matches[1] }
  }
  Write-Host ("     postgres: {0} containers, {1:F1}s combined startup (started concurrently)" -f $perfLines.Count, $startup)
}
}

# ---- app (only once the Expo app exists) --------------------------------------------------------------------
if ((Test-Want 'app') -and (Test-Path 'app/package.json')) {
  # The app gate's own logs land here too, so one upload carries app.log and the test.log it points to (#260).
  $env:CM_APP_CHECK_LOG_DIR = Join-Path $logDir 'app'
  $step = Invoke-Step 'app' { npm run check --prefix app --silent }
  if ($step.Code -ne 0) {
    Write-Failure 'app (npm run check)' ($step.Lines | Where-Object { $_.Trim() -ne '' } | Select-Object -Last $maxLines) $step.Log
  }
  Write-Ok "app: npm run check ($($step.Seconds)s)"
}

if ($fullRun) {
  Write-Host "all green ($([int][math]::Ceiling($scriptTimer.Elapsed.TotalSeconds))s)"
}
