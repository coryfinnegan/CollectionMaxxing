<#
.SYNOPSIS
  Tests for the mutation sampler (scripts/mutation/Mutation.psm1). No packages: a tiny runner, plain asserts.

  Run:  pwsh -NoProfile -File scripts/mutation/tests.ps1      (exits non-zero if any test fails)
  The git tests build a throwaway repo in the temp folder; nothing in this repo is touched.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Mutation.psm1') -Force

$script:failures = 0
$script:count = 0

function It([string]$Name, [scriptblock]$Body) {
  $script:count++
  try { & $Body; Write-Host "ok   $Name" }
  catch { $script:failures++; Write-Host "FAIL $Name"; Write-Host "     $($_.Exception.Message)" }
}

function Should-Be($Actual, $Expected, [string]$What = 'value') {
  if (-not ($Actual -ceq $Expected)) { throw "$What : expected <$Expected> but got <$Actual>" }
}

function Should-BeTrue([bool]$Condition, [string]$What) { if (-not $Condition) { throw "expected: $What" } }

# Every mutated line produced from one source line, as plain strings.
function Mutate([string]$Line, [string]$Operator = $null) {
  $all = @(Get-LineMutants -Lines @($Line) -File 'F.cs')
  if ($Operator) { $all = @($all | Where-Object { $_.Operator -eq $Operator }) }
  , @($all | ForEach-Object { $_.Mutated.Trim() })
}

# ---- operators -------------------------------------------------------------------------------------------------
It 'equality swaps == and !=' {
  Should-Be ((Mutate 'if (a == b) x();' 'eq') -join '|') 'if (a != b) x();'
  Should-Be ((Mutate 'return a != b;' 'eq') -join '|') 'return a == b;'
}

It 'relational operators swap to their boundary neighbour' {
  Should-Be ((Mutate 'return a < b;' 'rel') -join '|') 'return a <= b;'
  Should-Be ((Mutate 'return a <= b;' 'rel') -join '|') 'return a < b;'
  Should-Be ((Mutate 'return a > b;' 'rel') -join '|') 'return a >= b;'
  Should-Be ((Mutate 'return a >= b;' 'rel') -join '|') 'return a > b;'
}

It 'generics, lambdas and shifts are not relational operators' {
  Should-Be ((Mutate 'var x = new List<int>(); Func<int, int> f = a => a;' 'rel').Count) 0
  Should-Be ((Mutate 'var y = a << 2;' 'rel').Count) 0
}

It 'logical operators swap && and ||' {
  Should-Be ((Mutate 'return a && b;' 'logic') -join '|') 'return a || b;'
  Should-Be ((Mutate 'return a || b;' 'logic') -join '|') 'return a && b;'
}

It 'arithmetic swaps binary + and - but not ++, += or unary minus' {
  Should-Be ((Mutate 'var c = a + b;' 'arith') -join '|') 'var c = a - b;'
  Should-Be ((Mutate 'var c = a - b;' 'arith') -join '|') 'var c = a + b;'
  Should-Be ((Mutate 'i++; total += 1; var n = -1;' 'arith').Count) 0
}

It 'boolean literals flip' {
  Should-Be ((Mutate 'var ok = true;' 'bool') -join '|') 'var ok = false;'
  Should-Be ((Mutate 'return false;' 'bool') -join '|') 'return true;'
  Should-Be ((Mutate 'var istrue = 1;' 'bool').Count) 0
}

It 'return x becomes return default' {
  Should-Be ((Mutate '    return a + b;' 'return') -join '|') 'return default;'
  Should-Be ((Mutate 'return default;' 'return').Count) 0
  Should-Be ((Mutate 'return;' 'return').Count) 0
}

It 'return of a value that is already the default is not a mutant (it would be equivalent)' {
  foreach ($v in 'false', 'null', '0', '0.0', '0m', 'default') { Should-Be ((Mutate "return $v;" 'return').Count) 0 $v }
}

It 'a guard if (...) return/throw loses its condition' {
  Should-Be ((Mutate '    if (value is null) return;' 'guard') -join '|') 'if (false) return;'
  Should-Be ((Mutate 'if (Check(a, (b + 1))) throw new ArgumentException("x");' 'guard') -join '|') 'if (false) throw new ArgumentException("x");'
  Should-Be ((Mutate 'if (a) { return; }' 'guard').Count) 0
}

It 'Any and All swap' {
  Should-Be ((Mutate 'return xs.Any(x => x > 0);' 'linq') -join '|') 'return xs.All(x => x > 0);'
  Should-Be ((Mutate 'return xs.All(x => x > 0);' 'linq') -join '|') 'return xs.Any(x => x > 0);'
}

It 'Length and Count comparisons move by one' {
  $m = Mutate 'if (s.Length > 3) x();' 'offbyone'
  Should-Be ($m -join '|') 'if (s.Length > 4) x();|if (s.Length > 2) x();'
  Should-Be ((Mutate 'return list.Count == 0;' 'offbyone') -join '|') 'return list.Count == 1;'
}

# ---- what is never mutated -------------------------------------------------------------------------------------
It 'comments are skipped' {
  Should-Be (@(Get-LineMutants -Lines @('// if (a == b) return true;', '/// a && b', 'var x = 1; // a == b')  -File 'F.cs').Count) 0
}

It 'block comments across lines are skipped' {
  $lines = @('/*', ' a == b && true', '*/', 'var x = a == b;')
  $m = @(Get-LineMutants -Lines $lines -File 'F.cs')
  Should-Be ($m.Count) 1
  Should-Be ($m[0].Line) 4
}

It 'string and character literals are skipped' {
  Should-Be (@(Get-LineMutants -Lines @('var s = "a == b && true";', "var c = '<';", 'var t = $"x {y} == true";', 'var v = @"a == b";') -File 'F.cs').Count) 0
}

It 'code after a string on the same line is still mutated' {
  $m = @(Get-LineMutants -Lines @('var s = "x" + (a == b ? "q" : "r");') -File 'F.cs' | Where-Object Operator -eq 'eq')
  Should-Be $m.Count 1
  Should-Be $m[0].Mutated.Trim() 'var s = "x" + (a != b ? "q" : "r");'
}

It 'verbatim strings that span lines are skipped' {
  $lines = @('var s = @"', 'a == b && true', '";', 'var x = a == b;')
  $m = @(Get-LineMutants -Lines $lines -File 'F.cs')
  Should-Be $m.Count 1
  Should-Be $m[0].Line 4
}

It 'attributes, usings and directives are skipped' {
  Should-Be (@(Get-LineMutants -Lines @('[Fact(Skip = "a == b")]', '[Range(1, 2)] // true', 'using static A.B;', '#pragma warning disable CS1 // true', 'namespace X.Y;') -File 'F.cs').Count) 0
}

# ---- sampling --------------------------------------------------------------------------------------------------
$pool = 1..40 | ForEach-Object { [pscustomobject]@{ File = 'F.cs'; Line = $_; Operator = 'eq'; Index = 0; Original = "l$_"; Mutated = "m$_" } }

It 'the same seed picks the same sample' {
  $a = (Select-Sample -Mutants $pool -Count 10 -Seed 7 | ForEach-Object Line) -join ','
  $b = (Select-Sample -Mutants $pool -Count 10 -Seed 7 | ForEach-Object Line) -join ','
  Should-Be $a $b
  Should-Be (@(Select-Sample -Mutants $pool -Count 10 -Seed 7).Count) 10
}

It 'a different seed picks a different sample, and the pool order does not matter' {
  $a = (Select-Sample -Mutants $pool -Count 10 -Seed 7 | ForEach-Object Line) -join ','
  $c = (Select-Sample -Mutants $pool -Count 10 -Seed 8 | ForEach-Object Line) -join ','
  $reversed = (Select-Sample -Mutants @($pool[($pool.Count - 1)..0]) -Count 10 -Seed 7 | ForEach-Object Line) -join ','
  Should-BeTrue ($a -ne $c) 'seeds 7 and 8 differ'
  Should-Be $reversed $a
}

It 'asking for more than the pool returns the whole pool' {
  Should-Be (@(Select-Sample -Mutants $pool -Count 500 -Seed 1).Count) 40
}

# ---- equivalents -----------------------------------------------------------------------------------------------
It 'an equivalents entry is skipped and counted separately' {
  $equiv = @([pscustomobject]@{ file = 'F.cs'; line = 3; operator = 'eq'; reason = 'x' })
  $split = Split-Equivalents -Mutants $pool -Equivalents $equiv
  Should-Be (@($split.Kept).Count) 39
  Should-Be (@($split.Skipped).Count) 1
  Should-Be $split.Skipped[0].Line 3
}

# ---- runs (a throwaway git repo and a fake test runner) --------------------------------------------------------
function New-Repo {
  $root = Join-Path ([System.IO.Path]::GetTempPath()) ("mut-test-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
  New-Item -ItemType Directory -Path (Join-Path $root 'src') | Out-Null
  [System.IO.File]::WriteAllText((Join-Path $root 'src/A.cs'), "class A {`n  bool F(int a, int b) { return a == b; }`n  bool G(int a) { return a > 3 && true; }`n}`n")
  [System.IO.File]::WriteAllText((Join-Path $root 'src/B.cs'), "class B {`n  int H(int a) { return a + 1; }`n}`n")
  git -C $root init -q 2>&1 | Out-Null
  git -C $root config user.email 'test@example.invalid' | Out-Null
  git -C $root config user.name 'test' | Out-Null
  git -C $root config core.autocrlf false | Out-Null
  git -C $root add -A 2>&1 | Out-Null
  git -C $root commit -q -m init 2>&1 | Out-Null
  $root
}

function Snapshot([string]$Root) { (Get-ChildItem (Join-Path $Root 'src') -Filter *.cs | Sort-Object Name | ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) }) -join '||' }

$repoFiles = @('src/A.cs', 'src/B.cs')

It 'the run refuses a dirty working tree' {
  $root = New-Repo
  try {
    Add-Content (Join-Path $root 'src/A.cs') '// local edit'
    $threw = $false
    try { Invoke-MutationRun -RepoRoot $root -Files $repoFiles -Runner { 'Killed' } | Out-Null } catch { $threw = $_.Exception.Message -match 'clean' }
    Should-BeTrue $threw 'a dirty tree is refused with a message about a clean tree'
  } finally { Remove-Item $root -Recurse -Force }
}

It 'every file is restored after a normal run' {
  $root = New-Repo
  try {
    $before = Snapshot $root
    $script:seen = [System.Collections.Generic.List[string]]::new(); $script:root = $root
    $result = Invoke-MutationRun -RepoRoot $root -Files $repoFiles -Runner {
      param($Mutant) $script:seen.Add([System.IO.File]::ReadAllText((Join-Path $script:root $Mutant.File))); 'Killed' }
    Should-BeTrue ($result.Total -ge 4) 'several mutants were tried'
    Should-BeTrue (@($script:seen).Count -ge 4) 'the runner saw mutated files'
    Should-Be (Snapshot $root) $before 'files after the run'
    Should-Be ((git -C $root status --porcelain) -join '') '' 'git status'
  } finally { Remove-Item $root -Recurse -Force }
}

It 'every file is restored when the runner fails' {
  $root = New-Repo
  try {
    $before = Snapshot $root
    $threw = $false
    try { Invoke-MutationRun -RepoRoot $root -Files $repoFiles -Runner { throw 'boom' } | Out-Null } catch { $threw = $_.Exception.Message -match 'boom' }
    Should-BeTrue $threw 'the failure surfaces'
    Should-Be (Snapshot $root) $before 'files after the failed run'
  } finally { Remove-Item $root -Recurse -Force }
}

It 'every file is restored when the run is cancelled' {
  $root = New-Repo
  try {
    $before = Snapshot $root
    $ps = [powershell]::Create()
    $null = $ps.AddScript({
      param($Module, $Root, $Files)
      Import-Module $Module -Force
      Invoke-MutationRun -RepoRoot $Root -Files $Files -Runner { Start-Sleep -Seconds 60; 'Killed' }
    }).AddArgument((Join-Path $PSScriptRoot 'Mutation.psm1')).AddArgument($root).AddArgument($repoFiles)
    $handle = $ps.BeginInvoke()
    # Wait until a file really is mutated, then cancel the way Ctrl+C does.
    $deadline = [datetime]::UtcNow.AddSeconds(30)
    while ((Snapshot $root) -eq $before -and [datetime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    Should-BeTrue ((Snapshot $root) -ne $before) 'a file was mutated while the runner was busy'
    $ps.Stop()
    $ps.Dispose()
    Should-Be (Snapshot $root) $before 'files after the cancelled run'
  } finally { Remove-Item $root -Recurse -Force }
}

It 'a mutant that does not compile is left out of the score, and a timeout counts as killed' {
  $root = New-Repo
  try {
    $script:calls = 0
    $result = Invoke-MutationRun -RepoRoot $root -Files @('src/A.cs') -Runner {
      $script:calls++
      switch ($script:calls % 3) { 0 { 'BuildError' } 1 { 'Timeout' } default { 'Survived' } } }
    Should-BeTrue ($result.BuildErrors -ge 1) 'build errors counted'
    Should-Be $result.Total ($result.Killed + $result.Survived) 'total excludes build errors'
    Should-BeTrue ($result.Killed -ge 1) 'timeouts are killed'
    Should-Be $result.Survivors.Count $result.Survived 'survivor list'
  } finally { Remove-Item $root -Recurse -Force }
}

It 'a run that cannot start is an error: excluded from the score, never a survivor' {
  $root = New-Repo
  try {
    $result = Invoke-MutationRun -RepoRoot $root -Files @('src/A.cs') -Runner { 'Error' }
    Should-BeTrue ($result.Errors -ge 1) 'errors counted'
    Should-Be $result.Errors $result.Tried 'every run errored'
    Should-Be $result.Survived 0 'survived'
    Should-Be $result.Total 0 'total'
    Should-Be $result.Survivors.Count 0 'survivor list'
    Should-Be (Format-Score $result) '0 / 0 = n/a'
  } finally { Remove-Item $root -Recurse -Force }
}

# ---- reading a `dotnet test` result ----------------------------------------------------------------------------
$started = "Test run for C:\x\T.dll (.NETCoreApp,Version=v10.0)`nA total of 1 test files matched the specified pattern.`n"

It 'a green run with tests is Passed' {
  Should-Be (Get-TestRunStatus 0 ($started + "`nPassed!  - Failed:     0, Passed:   369, Skipped:     0, Total:   369, Duration: 340 ms - T.dll (net10.0)")) 'Passed'
}

It 'a red run is Failed' {
  Should-Be (Get-TestRunStatus 1 ($started + "`nFailed!  - Failed:     2, Passed:   367, Skipped:     0, Total:   369, Duration: 340 ms - T.dll (net10.0)")) 'Failed'
}

It 'exit 0 with no output at all (the restore-less fresh worktree) is an Error, not Passed' {
  Should-Be (Get-TestRunStatus 0 '') 'Error'
  Should-Be (Get-TestRunStatus 0 "`n") 'Error'
}

It 'a filter that matches no test is NoTests, and zero total is NoTests' {
  Should-Be (Get-TestRunStatus 0 $started) 'NoTests'
  Should-Be (Get-TestRunStatus 0 ($started + "Passed!  - Failed:     0, Passed:     0, Skipped:     0, Total:     0, Duration: 1 ms - T.dll (net10.0)")) 'NoTests'
}

It 'a compile error in the mutant is BuildError (not a mutant); a missing restore or other failure to start is Error' {
  Should-Be (Get-TestRunStatus 1 'C:\a\F.cs(3,9): error CS1002: ; expected [C:\a\P.csproj]') 'BuildError'
  Should-Be (Get-TestRunStatus 1 "C:\sdk\x.targets(266,5): error NETSDK1004: Assets file 'C:\a\project.assets.json' not found. [C:\a\P.csproj]") 'Error'
  Should-Be (Get-TestRunStatus 1 'MSBUILD : error MSB1009: Project file does not exist.') 'Error'
  Should-Be (Get-TestRunStatus 1 'something unexpected') 'Error'
  Should-Be (Get-TestRunStatus 1 ($started + 'The test host process crashed')) 'Error'
}

# ---- one answer from several test projects ---------------------------------------------------------------------
It 'several test projects: the first failure kills; all must pass to survive; NoTests is skipped' {
  Should-Be (Merge-TestStatuses @('Passed', 'Failed')) 'Failed'
  Should-Be (Merge-TestStatuses @('Failed', 'Passed')) 'Failed'
  Should-Be (Merge-TestStatuses @('Passed', 'Passed')) 'Passed'
  Should-Be (Merge-TestStatuses @('NoTests', 'Passed')) 'Passed'
  Should-Be (Merge-TestStatuses @('NoTests', 'NoTests')) 'Error'
  Should-Be (Merge-TestStatuses @('Passed', 'Error')) 'Error'
  Should-Be (Merge-TestStatuses @('Passed', 'BuildError')) 'BuildError'
  Should-Be (Merge-TestStatuses @('Passed', 'Timeout')) 'Timeout'
}

It 'the per-feature filter names the top folder under the project; no folder means no filter' {
  Should-Be (Get-FeatureFilter 'src/CollectionMaxxing.Application/Scanning/Matching/X.cs' 'Application') 'FullyQualifiedName~.Scanning.'
  Should-Be (Get-FeatureFilter 'src/CollectionMaxxing.Application/Ownership/Y.cs' 'Application') 'FullyQualifiedName~.Ownership.'
  Should-Be (Get-FeatureFilter 'src/CollectionMaxxing.Application/DependencyInjection.cs' 'Application') ''
}

It 'an equivalent mutant is skipped by the run and reported in the count' {
  $root = New-Repo
  try {
    $equiv = @([pscustomobject]@{ file = 'src/B.cs'; line = 2; operator = 'arith'; reason = 'x' })
    $script:ran = [System.Collections.Generic.List[string]]::new()
    $result = Invoke-MutationRun -RepoRoot $root -Files @('src/B.cs') -Equivalents $equiv -Runner { param($Mutant) $script:ran.Add($Mutant.Operator); 'Killed' }
    Should-Be $result.Equivalent 1
    Should-BeTrue (-not ($script:ran -contains 'arith')) 'the equivalent mutant was not run'
  } finally { Remove-Item $root -Recurse -Force }
}

It 'the score line reads killed / total = score' {
  $r = [pscustomobject]@{ Killed = 3; Total = 4; Survived = 1 }
  Should-Be (Format-Score $r) '3 / 4 = 75.0%'
  Should-Be (Format-Score ([pscustomobject]@{ Killed = 0; Total = 0; Survived = 0 })) '0 / 0 = n/a'
}

Write-Host ''
if ($script:failures -gt 0) { Write-Host "$($script:failures) of $($script:count) tests FAILED"; exit 1 }
Write-Host "all $($script:count) mutation-tooling tests passed"
