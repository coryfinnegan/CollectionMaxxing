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
  Full output of the last failing step is kept in the file named in the failure message. The logs live in a folder of
  this checkout's own under the temp folder (collectionmaxxing-check/<folder name>-<hash of the path>), so gate runs
  in parallel worktrees never read each other's logs.

  -Changed narrows the gate to what the change can affect (the same classification CI's changes job uses).
  The changed files are `git diff origin/main...HEAD` plus uncommitted and untracked files. Only paths under app/ (except
  app/playwright/serve.mjs, which the backend tests read) run just the app phase. Only paths under src/, tests/, deploy/ and
  evals/ skip the app phase, and run the test phase only for the test projects the change affects: a test project whose own
  folder changed, or one that references (through ProjectReference, transitively) a project whose folder changed, plus the
  Architecture tests, which read the source and test folders as a whole. Format runs only on projects whose folder changed.
  A shared file (tests/Directory.Build.props, a file in src/ or tests/ outside any project), an empty change, a change
  outside those folders, or a missing origin/main runs everything. One `changed:` line says what ran and what was skipped.
  Without -Changed the gate behaves as it always did. CI never passes -Changed: it is the full test of record.

  Environment: CM_TEST_ONLY limits the test phase to the named test projects (whitespace-separated basenames).
  CM_TEST_FILTER hands a VSTest filter expression (`dotnet test --filter`) to every selected project; see check.sh for
  the syntax. A filter that matches nothing fails, because an empty CI shard would hide a coverage gap.
  CM_SHARD_REPORT (a file path, with CM_TEST_FILTER) records "project listed-in-shard listed-in-suite ran" per project.
  CM_FORMAT_PARALLEL sets how many `dotnet format` runs go at once (default: the CPU count, at least 2 and at most 4).
#>
[CmdletBinding()]
param(
  [string[]]$Phase,
  [switch]$Changed
)

$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

$solution = 'CollectionMaxxing.slnx'
$maxLines = 15
# Every tests/ project except these two support libraries is a test project (see tests/Directory.Build.targets).
$skipLibs = @('CollectionMaxxing.TestSupport', 'CollectionMaxxing.Api.TestKit')

# Agents run this gate at the same time in different worktrees of one PC, so the logs cannot share a folder: a run
# would read the format.log another run just wrote. The folder name is the checkout's folder name (so a person can tell
# which is which) plus a hash of its full path (so two worktrees that end in the same name still differ). The same
# checkout gets the same folder every run. Windows paths compare without regard to case.
function Get-CheckLogDir([string]$CheckoutRoot) {
  $path = $CheckoutRoot.TrimEnd('\', '/')
  if ([System.IO.Path]::DirectorySeparatorChar -eq '\') { $path = $path.ToLowerInvariant() }
  $sha = [System.Security.Cryptography.SHA256]::Create()
  try { $hash = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($path)) } finally { $sha.Dispose() }
  $id = -join ($hash[0..3] | ForEach-Object { $_.ToString('x2') })
  $name = [System.IO.Path]::GetFileName($path) -replace '[^A-Za-z0-9._-]', '_'
  if (-not $name) { $name = 'root' }
  if ($name.Length -gt 40) { $name = $name.Substring(0, 40) }
  Join-Path ([System.IO.Path]::GetTempPath()) "collectionmaxxing-check/$name-$id"
}
$logDir = Get-CheckLogDir (Get-Location).Path
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$scriptTimer = [System.Diagnostics.Stopwatch]::StartNew()

# ---- -Changed: which part of the gate a change can affect ---------------------------------------------------------------
# Runs git quietly: Code is non-zero when git is missing or the command fails, Text is its standard output.
function Invoke-GitQuiet([string[]]$GitArgs) {
  $prevErr = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'
  try { $out = @(& git @GitArgs 2>$null); $code = $LASTEXITCODE }
  catch { $out = @(); $code = 1 }
  finally { $ErrorActionPreference = $prevErr }
  [pscustomobject]@{ Code = $code; Text = ($out -join "`n") }
}

# The changed paths (forward slashes, relative to the repo root): committed on this branch since it left origin/main, plus
# staged, unstaged and untracked files. $null when that cannot be worked out. -z keeps odd file names unquoted, and
# --no-renames lists both ends of a move, so a file moved out of src/ still counts as a src/ change.
function Get-ChangedPaths {
  if ((Invoke-GitQuiet @('rev-parse', '--verify', '--quiet', 'origin/main^{commit}')).Code -ne 0) { return $null }
  $committed = Invoke-GitQuiet @('diff', '--name-only', '--no-renames', '-z', 'origin/main...HEAD')
  $uncommitted = Invoke-GitQuiet @('diff', '--name-only', '--no-renames', '-z', 'HEAD')
  $untracked = Invoke-GitQuiet @('ls-files', '--others', '--exclude-standard', '-z')
  if (($committed.Code -ne 0) -or ($uncommitted.Code -ne 0) -or ($untracked.Code -ne 0)) { return $null }
  $paths = foreach ($r in @($committed, $uncommitted, $untracked)) { $r.Text -split "`0" | Where-Object { $_ } }
  , @($paths | Sort-Object -Unique)
}

# 'app' (only the app changed), 'server' (only src/, tests/, deploy/ and evals/ changed) or 'all' (anything else, or nothing).
# app/playwright/serve.mjs counts as backend: SecurityHeadersTests reads it.
function Get-GateArea([string[]]$Paths) {
  if (@($Paths).Count -eq 0) { return 'all' }
  $appOnly = $true
  $serverOnly = $true
  foreach ($path in $Paths) {
    if (-not ($path.StartsWith('app/') -and ($path -ne 'app/playwright/serve.mjs'))) { $appOnly = $false }
    if ($path -notmatch '^(src|tests|deploy|evals)/') { $serverOnly = $false }
  }
  if ($appOnly) { return 'app' }
  if ($serverOnly) { return 'server' }
  'all'
}

# The solution's projects (src/*/ and tests/*/), each with the names of the projects it references. A reference is
# matched by file name, which is how this repo names projects (one folder, one csproj of the same name).
function Get-GateProjects {
  $projects = @{}
  foreach ($file in @(Get-ChildItem 'src/*/*.csproj', 'tests/*/*.csproj' -ErrorAction SilentlyContinue)) {
    $refs = @()
    try {
      $xml = [xml](Get-Content -LiteralPath $file.FullName -Raw)
      $refs = @($xml.SelectNodes("//*[local-name()='ProjectReference']") | ForEach-Object {
          [IO.Path]::GetFileNameWithoutExtension(($_.GetAttribute('Include') -replace '\\', '/'))
        } | Where-Object { $_ })
    }
    catch { $refs = @() }
    $top = Split-Path -Leaf (Split-Path -Parent $file.Directory.FullName)
    $projects[$file.BaseName] = [pscustomobject]@{ Name = $file.BaseName; Path = $file.FullName; Dir = "$top/$($file.Directory.Name)"; Refs = $refs }
  }
  $projects
}

# The project itself and everything it references, transitively.
function Get-ProjectClosure($Projects, [string]$Name) {
  $seen = [System.Collections.Generic.HashSet[string]]::new()
  $queue = [System.Collections.Generic.Queue[string]]::new()
  $queue.Enqueue($Name)
  while ($queue.Count -gt 0) {
    $current = $queue.Dequeue()
    if (-not $seen.Add($current)) { continue }
    if ($Projects.ContainsKey($current)) { foreach ($ref in $Projects[$current].Refs) { $queue.Enqueue($ref) } }
  }
  @($seen)
}

function Format-GateNames([string[]]$Names) {
  (@($Names | ForEach-Object { $_ -replace '^CollectionMaxxing\.', '' } | Sort-Object) -join ', ')
}

# What -Changed will run: the area, the phases (RunBackend, RunApp), the test projects (TestAll, or TestNames), the
# projects to format (FormatAll, or FormatNames) and the one line that says so.
function Get-GateScope {
  $everything = {
    param($why, $count)
    $files = if ($count) { ", $count file$(if ($count -ne 1) { 's' })" } else { '' }
    [pscustomobject]@{
      Area = 'all'; RunBackend = $true; RunApp = $true; TestAll = $true; TestNames = @(); FormatAll = $true; FormatNames = @()
      Line = "changed: everything$files. skipped: nothing ($why)"
    }
  }
  $paths = Get-ChangedPaths
  if ($null -eq $paths) { return (& $everything 'cannot tell what changed: no origin/main to compare with' 0) }
  if ($paths.Count -eq 0) { return (& $everything 'no changed files found' 0) }
  $count = $paths.Count
  $files = "$count file$(if ($count -ne 1) { 's' })"
  $area = Get-GateArea $paths
  if ($area -eq 'app') {
    return [pscustomobject]@{
      Area = 'app'; RunBackend = $false; RunApp = $true; TestAll = $true; TestNames = @(); FormatAll = $true; FormatNames = @()
      Line = "changed: app-only, $files. skipped: restore, build, format and test (nothing outside app/ changed)"
    }
  }
  if ($area -eq 'all') {
    $outside = @($paths | Where-Object { -not ($_ -match '^(app|src|tests|deploy|evals)/') })
    $why = if ($outside.Count -gt 0) { "$($outside[0]) is outside app/ and the server folders" }
           elseif ($paths -contains 'app/playwright/serve.mjs') { 'app/playwright/serve.mjs is read by the backend tests' }
           else { 'the change touches both the app and the server' }
    return (& $everything $why $count)
  }

  # Server-only: work out which projects the change touches.
  $projects = Get-GateProjects
  $changedProjects = [System.Collections.Generic.HashSet[string]]::new()
  $touchedForTests = [System.Collections.Generic.HashSet[string]]::new()   # changedProjects, plus test projects that read a changed data folder
  $shared = $null
  $touchesCode = $false
  foreach ($path in $paths) {
    if ($path -match '^(src|tests)/') {
      $touchesCode = $true
      $owner = @($projects.Values | Where-Object { $path.StartsWith($_.Dir + '/') }) | Select-Object -First 1
      if ($owner) { [void]$changedProjects.Add($owner.Name) }
      elseif (-not $shared) { $shared = $path }
    }
    elseif (($path -match '^evals/') -and $projects.ContainsKey('CollectionMaxxing.Recognition.Evals')) {
      # The recognition eval tests read evals/ (the baseline, the acceptance truth, the results). Nothing there needs formatting.
      [void]$touchedForTests.Add('CollectionMaxxing.Recognition.Evals')
    }
  }
  foreach ($name in $changedProjects) { [void]$touchedForTests.Add($name) }
  $lanes = @($projects.Values | Where-Object { $_.Dir -like 'tests/*' -and $_.Name -notin $skipLibs } | ForEach-Object { $_.Name } | Sort-Object)
  $allProjects = @($projects.Keys)
  $scope = [pscustomobject]@{ Area = 'server'; RunBackend = $true; RunApp = $false; TestAll = $false; TestNames = @(); FormatAll = $false; FormatNames = @(); Line = '' }
  $skipped = @('app phase (nothing under app/ changed)')
  $kept = ''
  if ($shared) {
    # A file in src/ or tests/ that no project owns (tests/Directory.Build.props, a shared props file): it can reach any project.
    $scope.TestAll = $true
    $scope.FormatAll = $true
    $kept = " Everything else runs: $shared is shared by the projects."
  }
  else {
    # The Architecture tests read the source and test folders as a whole (dependency rules, migrations, which servers tests start).
    $always = if ($touchesCode) { @('CollectionMaxxing.Architecture.Tests') } else { @() }
    $scope.TestNames = @($lanes | Where-Object {
        $lane = $_
        ($lane -in $always) -or (@(Get-ProjectClosure $projects $lane) | Where-Object { $touchedForTests.Contains($_) })
      })
    $scope.FormatNames = @($allProjects | Where-Object { $changedProjects.Contains($_) } | Sort-Object)
    $skippedTests = $lanes.Count - $scope.TestNames.Count
    if ($scope.TestNames.Count -eq 0) { $skipped += "all $($lanes.Count) test projects (none is affected)" }
    elseif ($skippedTests -gt 0) { $skipped += "$skippedTests of $($lanes.Count) test projects (not affected)" }
    $skippedFormat = $allProjects.Count - $scope.FormatNames.Count
    if ($scope.FormatNames.Count -eq 0) { $skipped += "format of all $($allProjects.Count) projects (none changed)" }
    elseif ($skippedFormat -gt 0) { $skipped += "format of $skippedFormat of $($allProjects.Count) projects (unchanged)" }
    if ($scope.TestNames.Count -gt 0) { $kept += " testing: $(Format-GateNames $scope.TestNames)." }
    if ($scope.FormatNames.Count -gt 0) { $kept += " formatting: $(Format-GateNames $scope.FormatNames)." }
  }
  $scope.Line = "changed: server-only, $files. skipped: $($skipped -join '; ').$kept"
  $scope
}

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
# With -Changed, the scope says which phases a change can affect; without it every phase wants to run.
$scope = $null
if ($Changed) {
  $scope = Get-GateScope
  Write-Host $scope.Line
}
$runBackend = (-not $scope) -or $scope.RunBackend
$runApp = (-not $scope) -or $scope.RunApp
function Test-Want([string]$Name) {
  if (-not ($Phase -contains $Name)) { return $false }
  if ($Name -eq 'app') { return $runApp }
  $runBackend
}

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
# Projects run side by side (CM_FORMAT_PARALLEL at a time, default the CPU count clamped to 2..4): each is its own
# dotnet process, so they do not share state, and the log below is written in project order afterwards.
$formatProjects = @(Get-ChildItem 'src/*/*.csproj', 'tests/*/*.csproj' | Sort-Object FullName)
if ($scope -and (-not $scope.FormatAll)) {
  $formatProjects = @($formatProjects | Where-Object { $_.BaseName -in $scope.FormatNames })
}
$formatThrottle = 0
if (-not [int]::TryParse("$($env:CM_FORMAT_PARALLEL)", [ref]$formatThrottle) -or $formatThrottle -lt 1) {
  $formatThrottle = [math]::Max(2, [math]::Min(4, [Environment]::ProcessorCount))
}
$formatJob = {
  param($ProjectPath, $Root, $MaxAttempts)
  Set-Location $Root
  $lines = @()
  $retries = @()
  $verified = $false
  $attempt = 0
  while ((-not $verified) -and ($attempt -lt $MaxAttempts)) {
    $attempt++
    # Windows PowerShell 5.1 throws NativeCommandError on merged native stderr while $ErrorActionPreference is
    # Stop (the ForEach stringify does not save it); dotnet format's flaky workspace warning (exit 0) hit this.
    # Run the call under Continue so stderr lines land in $attemptLines as text (#398).
    $ErrorActionPreference = 'Continue'
    $attemptLines = @(dotnet format $ProjectPath --verify-no-changes --no-restore 2>&1 | ForEach-Object { "$_" })
    $code = $LASTEXITCODE
    $lines += $attemptLines
    if ($code -eq 0) {
      $verified = $true
    } elseif ($attemptLines -match 'Unhandled exception') {
      if ($attempt -lt $MaxAttempts) { $retries += "hit the Roslyn crash, retrying (attempt $($attempt + 1)/$MaxAttempts; see issue #308)" }
    } else {
      break
    }
  }
  [pscustomobject]@{ Verified = $verified; Lines = $lines; Retries = $retries }
}
$formatRoot = (Get-Location).Path
$formatResults = @{}
$formatQueue = [System.Collections.Generic.Queue[object]]::new()
foreach ($proj in $formatProjects) { $formatQueue.Enqueue($proj) }
$formatRunning = @()
while (($formatQueue.Count -gt 0) -or ($formatRunning.Count -gt 0)) {
  while (($formatQueue.Count -gt 0) -and ($formatRunning.Count -lt $formatThrottle)) {
    $proj = $formatQueue.Dequeue()
    $job = Start-Job -ScriptBlock $formatJob -ArgumentList @($proj.FullName, $formatRoot, $maxAttempts)
    $formatRunning += [pscustomobject]@{ Job = $job; Proj = $proj }
  }
  [void](Wait-Job -Job @($formatRunning | ForEach-Object { $_.Job }) -Any)
  foreach ($item in @($formatRunning | Where-Object { $_.Job.State -ne 'Running' })) {
    $formatResults[$item.Proj.BaseName] = @(Receive-Job -Job $item.Job)[-1]
    Remove-Job -Job $item.Job -Force
    $formatRunning = @($formatRunning | Where-Object { $_.Job.Id -ne $item.Job.Id })
  }
}
foreach ($proj in $formatProjects) {
  $result = $formatResults[$proj.BaseName]
  if ($result) {
    @($result.Lines) | Add-Content -Path $formatLog
    foreach ($retry in @($result.Retries)) { Write-Host "     format: $($proj.BaseName) $retry" }
  }
  if ((-not $result) -or (-not $result.Verified)) { $formatFailed += $proj.BaseName }
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
Write-Ok "format ($($formatProjects.Count) projects, $([int][math]::Ceiling($formatTimer.Elapsed.TotalSeconds))s)"

# Comments say what the code does and why, not where the idea came from. The paths held to that rule are listed in
# scripts/comment-history-enforced.txt (see the header there for how to widen it).
if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
  Write-Failure 'comments (node is needed for scripts/check-comments.mjs)' @() (Join-Path $logDir 'comments.log')
}
$step = Invoke-Step 'comments' { node scripts/check-comments.mjs }
if ($step.Code -ne 0) {
  Write-Failure 'comments (say what the code does and why; to widen the ratchet see scripts/comment-history-enforced.txt)' ($step.Lines | Select-Object -First $maxLines) $step.Log
}
Write-Ok "comments, history-free in the enforced paths ($($step.Seconds)s)"
}

# ---- test -----------------------------------------------------------------------------------------------------
if (Test-Want 'test') {
# Every tests/ project except the two support libraries (see tests/Directory.Build.targets), each in its own process at
# the same time. A project that needs Postgres starts its own container, so lanes never see each other's data.
$lanes = @(Get-ChildItem 'tests/*/*.csproj' | Where-Object { $_.BaseName -notin $skipLibs } | Sort-Object FullName)
$noneAffected = $false
if ($scope -and (-not $scope.TestAll)) {
  # -Changed: only the test projects the change affects (the scope line printed above names them).
  $lanes = @($lanes | Where-Object { $_.BaseName -in $scope.TestNames })
  $noneAffected = ($lanes.Count -eq 0)
  if ($noneAffected) { Write-Ok 'test: skipped, no test project is affected by this change' }
}
if (-not $noneAffected) {
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
}

# ---- app (only once the Expo app exists) --------------------------------------------------------------------
if ((Test-Want 'app') -and (Test-Path 'app/package.json')) {   # Test-Want 'app' is false for a server-only -Changed run
  # The app gate's own logs land here too, so one upload carries app.log and the test.log it points to (#260). On CI the
  # workflow names the folder it uploads; anywhere else a value in the environment is ignored, because one left by an
  # earlier in-process run would send every checkout back to a single shared folder.
  if (-not (($env:GITHUB_ACTIONS -eq 'true') -and $env:CM_APP_CHECK_LOG_DIR)) {
    $env:CM_APP_CHECK_LOG_DIR = Join-Path $logDir 'app'
  }
  $step = Invoke-Step 'app' { npm run check --prefix app --silent }
  if ($step.Code -ne 0) {
    Write-Failure 'app (npm run check)' ($step.Lines | Where-Object { $_.Trim() -ne '' } | Select-Object -Last $maxLines) $step.Log
  }
  Write-Ok "app: npm run check ($($step.Seconds)s)"
}

if ($fullRun) {
  Write-Host "all green ($([int][math]::Ceiling($scriptTimer.Elapsed.TotalSeconds))s$(if ($scope -and $scope.Area -ne 'all') { ', changed files only' }))"
}
