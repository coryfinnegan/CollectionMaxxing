<#
.SYNOPSIS
  Hand-rolled mutation sweep for Domain and Application (Stryker.NET can't drive our xUnit v3 projects; see tests/mutation/README.md).

.DESCRIPTION
  Picks line-level mutants (operator swaps) from the project's source files, applies them one at a time, runs the
  matching test project, and reports which ones the tests did NOT catch.

  Prints one line per surviving mutant (file:line, the change) and a last line:  killed / total = score.
  - Needs a clean git tree. Every touched file is restored in a finally block, also on Ctrl+C.
  - A mutant that does not compile is "not a mutant" and left out of the score; a timeout counts as killed.
  - Mutants listed in tests/mutation/equivalents.json are skipped and counted separately.
  - Runs at below-normal priority. Domain needs no Docker. Application's tests start Postgres in Docker, so run
    it one folder at a time (-Path src/CollectionMaxxing.Application/Items) and keep -Sample small.

  Examples:
    pwsh scripts/mutate.ps1 -Project Domain -Sample 150 -Seed 1
    pwsh scripts/mutate.ps1 -Project Domain -Path src/CollectionMaxxing.Domain/Matching
    pwsh scripts/mutate.ps1 -Project Application -Path src/CollectionMaxxing.Application/Items -Sample 40
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][ValidateSet('Domain', 'Application')][string]$Project,
  # A folder or a file to mutate, relative to the repo root (or to the project folder). Default: the whole project.
  [string]$Path,
  # How many mutants to try (a seeded random pick). Default: all of them.
  [int]$Sample = 0,
  [int]$Seed = 0,
  # Passed to `dotnet test --filter`, to run only part of the tests for each mutant.
  [string]$TestFilter = '',
  # Per-mutant limit. Default: three times the unmutated run, at least 60 s.
  [int]$TimeoutSeconds = 0,
  # Print each mutant's result as it is decided.
  [switch]$ShowProgress
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $root
Import-Module (Join-Path $PSScriptRoot 'mutation/Mutation.psm1') -Force

$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
try { [System.Diagnostics.Process]::GetCurrentProcess().PriorityClass = [System.Diagnostics.ProcessPriorityClass]::BelowNormal } catch { }

if ($Seed -eq 0) { $Seed = Get-Random -Minimum 1 -Maximum 1000000 }

$srcDir = Join-Path $root "src/CollectionMaxxing.$Project"
$testProject = Join-Path $root "tests/CollectionMaxxing.$Project.Tests/CollectionMaxxing.$Project.Tests.csproj"
$target = $srcDir
if ($Path) {
  $target = if (Test-Path $Path) { (Resolve-Path $Path).Path } else { Join-Path $srcDir $Path }
  if (-not (Test-Path $target)) { throw "Path not found: $Path" }
  if (-not $target.StartsWith($srcDir, [System.StringComparison]::OrdinalIgnoreCase)) { throw "Path must be inside src/CollectionMaxxing.$Project." }
}

Assert-CleanTree $root

$files = Get-ChildItem -Path $target -Recurse -Filter *.cs -File |
  Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.Name -notmatch '\.(g|generated)\.cs$' } |
  Where-Object { -not ($Project -eq 'Application' -and $_.FullName -match '[\\/]Evals[\\/]') } |
  Sort-Object FullName |
  ForEach-Object { [System.IO.Path]::GetRelativePath($root, $_.FullName) -replace '\\', '/' }
if (@($files).Count -eq 0) { throw "No .cs files under $target." }

$equivalentsFile = Join-Path $root 'tests/mutation/equivalents.json'
$equivalents = if (Test-Path $equivalentsFile) { @(Get-Content $equivalentsFile -Raw | ConvertFrom-Json) } else { @() }

# Baseline: the unmutated tests must be green, or every mutant would look "killed".
Write-Host "baseline: dotnet test $Project ..."
$baseline = Invoke-DotnetTest $testProject 1800 $TestFilter
if ($baseline.Status -ne 'Passed') {
  Write-Host "FAIL baseline run is $($baseline.Status); fix the tests first."
  $baseline.Output -split "`n" | Where-Object { $_.Trim() } | Select-Object -Last 15 | ForEach-Object { Write-Host "     $_" }
  exit 1
}
if ($TimeoutSeconds -le 0) { $TimeoutSeconds = [math]::Max(60, 3 * $baseline.Seconds) }
Write-Host "baseline green in $($baseline.Seconds)s; per-mutant limit ${TimeoutSeconds}s; seed $Seed; $(@($files).Count) files"

$runner = {
  param($Mutant)
  $r = Invoke-DotnetTest $testProject $TimeoutSeconds $TestFilter
  switch ($r.Status) { 'Passed' { 'Survived' } 'Failed' { 'Killed' } 'Timeout' { 'Timeout' } default { 'BuildError' } }
}
$progress = if ($ShowProgress) {
  { param($Result, $Mutant, $Status) Write-Host ("  [{0}] {1}:{2} {3}" -f $Status, $Mutant.File, $Mutant.Line, $Mutant.Operator) }
} else { $null }

$timer = [System.Diagnostics.Stopwatch]::StartNew()
$result = Invoke-MutationRun -RepoRoot $root -Files $files -Runner $runner -Sample $Sample -Seed $Seed -Equivalents $equivalents -OnProgress $progress
$timer.Stop()

foreach ($m in $result.Survivors) {
  Write-Host ("SURVIVED {0}:{1} [{2}]  {3}  =>  {4}" -f $m.File, $m.Line, $m.Operator, $m.Original.Trim(), $m.Mutated.Trim())
}
Write-Host ("tried {0} of {1} candidates in {2:N0} min; {3} did not compile (not counted); {4} timed out (counted as killed); {5} equivalent skipped" -f `
    $result.Tried, $result.Candidates, $timer.Elapsed.TotalMinutes, $result.BuildErrors, $result.Timeout, $result.Equivalent)
Write-Host (Format-Score $result)
