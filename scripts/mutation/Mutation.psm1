# The hand-rolled mutation sampler: line-level operator swaps, a seeded sample, and a run loop that always puts
# the files back. Used by scripts/mutate.ps1; tested by scripts/mutation/tests.ps1. No packages.

Set-StrictMode -Version Latest

# ---- masking: blank out comments, strings and characters so operators are only found in real code ---------------

# Returns one string per input line, the same length, with comment/string/char content replaced by spaces.
function Get-CodeMask([string[]]$Lines) {
  $state = 'code'   # code | block | verbatim | raw
  $rawQuotes = 0
  foreach ($line in $Lines) {
    $sb = [System.Text.StringBuilder]::new($line.Length)
    $i = 0
    $n = $line.Length
    while ($i -lt $n) {
      $c = $line[$i]
      $next = if ($i + 1 -lt $n) { $line[$i + 1] } else { [char]0 }
      switch ($state) {
        'block' {
          if ($c -eq '*' -and $next -eq '/') { [void]$sb.Append('  '); $i += 2; $state = 'code' }
          else { [void]$sb.Append(' '); $i++ }
        }
        'verbatim' {
          if ($c -eq '"' -and $next -eq '"') { [void]$sb.Append('  '); $i += 2 }
          elseif ($c -eq '"') { [void]$sb.Append(' '); $i++; $state = 'code' }
          else { [void]$sb.Append(' '); $i++ }
        }
        'raw' {
          if ($c -eq '"' -and $line.Substring($i).StartsWith('"' * $rawQuotes)) {
            [void]$sb.Append(' ' * $rawQuotes); $i += $rawQuotes; $state = 'code'
          } else { [void]$sb.Append(' '); $i++ }
        }
        default {
          if ($c -eq '/' -and $next -eq '/') { [void]$sb.Append(' ' * ($n - $i)); $i = $n }
          elseif ($c -eq '/' -and $next -eq '*') { [void]$sb.Append('  '); $i += 2; $state = 'block' }
          elseif ($c -eq '"') {
            $m = [regex]::Match($line.Substring($i), '^"{3,}')
            $prefix = $sb.ToString()
            $isVerbatim = $prefix -match '@\$?$' -or $prefix -match '\$@$'
            if ($m.Success) {
              $rawQuotes = $m.Length; [void]$sb.Append(' ' * $m.Length); $i += $m.Length; $state = 'raw'
              # A raw string that opens and closes on one line.
              $rest = $line.Substring($i)
              $close = $rest.IndexOf('"' * $rawQuotes)
              if ($close -ge 0) { [void]$sb.Append(' ' * ($close + $rawQuotes)); $i += $close + $rawQuotes; $state = 'code' }
            } elseif ($isVerbatim) { [void]$sb.Append(' '); $i++; $state = 'verbatim' }
            else {
              # Ordinary (or interpolated) string: runs to the closing quote on this line.
              [void]$sb.Append(' '); $i++
              while ($i -lt $n) {
                if ($line[$i] -eq '\') { [void]$sb.Append('  '); $i += 2; continue }
                if ($line[$i] -eq '"') { [void]$sb.Append(' '); $i++; break }
                [void]$sb.Append(' '); $i++
              }
            }
          }
          elseif ($c -eq "'" -and ($i + 2 -lt $n)) {
            # Character literal: 'x' or '\x' or '\u0000'.
            $m = [regex]::Match($line.Substring($i), "^'(\\.[0-9A-Fa-f]*|[^'\\])'")
            if ($m.Success) { [void]$sb.Append(' ' * $m.Length); $i += $m.Length } else { [void]$sb.Append($c); $i++ }
          }
          else { [void]$sb.Append($c); $i++ }
        }
      }
    }
    # An unterminated ordinary string never carries to the next line, so only block/verbatim/raw remain in $state.
    $sb.ToString().PadRight($n).Substring(0, $n)
  }
}

# ---- operators ------------------------------------------------------------------------------------------------

# Each operator gives spans on the masked line: @{Index; Length; Text}. The text replaces the span in the real line.
function Find-Spans([string]$Mask, [string]$Pattern, [scriptblock]$Replace, [string]$Operator, [int]$Group = 0) {
  foreach ($m in [regex]::Matches($Mask, $Pattern)) {
    $g = $m.Groups[$Group]
    $text = & $Replace $m
    [pscustomobject]@{ Operator = $Operator; Index = $g.Index; Length = $g.Length; Text = $text }
  }
}

function Get-LineSpans([string]$Mask) {
  # equality
  Find-Spans $Mask '(?<![=!<>])==(?!=)' { '!=' } 'eq'
  Find-Spans $Mask '(?<![=!<>])!=(?!=)' { '==' } 'eq'
  # relational: only with spaces around, so generics, lambdas and shifts are left alone
  Find-Spans $Mask '(?<=\s)<=(?=\s)' { '<' } 'rel'
  Find-Spans $Mask '(?<=\s)>=(?=\s)' { '>' } 'rel'
  Find-Spans $Mask '(?<=\s)<(?=\s)' { '<=' } 'rel'
  Find-Spans $Mask '(?<=\s)>(?=\s)' { '>=' } 'rel'
  # logical
  Find-Spans $Mask '(?<=\s)&&(?=\s)' { '||' } 'logic'
  Find-Spans $Mask '(?<=\s)\|\|(?=\s)' { '&&' } 'logic'
  # arithmetic: binary operators written with spaces around them
  Find-Spans $Mask '(?<=\S\s)\+(?=\s)' { '-' } 'arith'
  Find-Spans $Mask '(?<=\S\s)-(?=\s)' { '+' } 'arith'
  # boolean literals
  Find-Spans $Mask '\btrue\b' { 'false' } 'bool'
  Find-Spans $Mask '\bfalse\b' { 'true' } 'bool'
  # return x -> return default
  Find-Spans $Mask '^\s*return\s+(?!default\s*;)([^;]+);\s*$' { 'default' } 'return' 1
  # LINQ
  Find-Spans $Mask '\.Any(?=\()' { '.All' } 'linq'
  Find-Spans $Mask '\.All(?=\()' { '.Any' } 'linq'
  # off-by-one on Length / Count comparisons
  foreach ($m in [regex]::Matches($Mask, '\.(?:Length|Count)\s*(?:==|!=|<=|>=|<|>)\s*(\d+)\b')) {
    $g = $m.Groups[1]; $v = [long]$g.Value
    [pscustomobject]@{ Operator = 'offbyone'; Index = $g.Index; Length = $g.Length; Text = "$($v + 1)" }
    if ($v -gt 0) { [pscustomobject]@{ Operator = 'offbyone'; Index = $g.Index; Length = $g.Length; Text = "$($v - 1)" } }
  }
  # a single-line guard: if (cond) return/throw ...;  ->  if (false) ...
  $gm = [regex]::Match($Mask, '^\s*if\s*\(')
  if ($gm.Success) {
    $depth = 1; $j = $gm.Length
    while ($j -lt $Mask.Length -and $depth -gt 0) {
      if ($Mask[$j] -eq '(') { $depth++ } elseif ($Mask[$j] -eq ')') { $depth-- }
      $j++
    }
    if ($depth -eq 0) {
      $after = $Mask.Substring($j)
      if ($after -match '^\s*(return|throw)\b.*;\s*$') {
        [pscustomobject]@{ Operator = 'guard'; Index = $gm.Length; Length = ($j - 1 - $gm.Length); Text = 'false' }
      }
    }
  }
}

# Lines that are never mutated: attributes, usings, namespaces, directives.
function Test-SkippableLine([string]$Mask) {
  $t = $Mask.Trim()
  if ($t -eq '') { return $true }
  return ($t -match '^(\[|#|using\b|namespace\b)')
}

# All mutants for the given source lines: objects with File, Line (1-based), Operator, Index, Original, Mutated.
function Get-LineMutants([string[]]$Lines, [string]$File) {
  $masks = @(Get-CodeMask $Lines)
  for ($i = 0; $i -lt $Lines.Count; $i++) {
    $mask = $masks[$i]
    if (Test-SkippableLine $mask) { continue }
    $line = $Lines[$i]
    $seen = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($s in @(Get-LineSpans $mask)) {
      $mutated = $line.Substring(0, $s.Index) + $s.Text + $line.Substring($s.Index + $s.Length)
      if ($mutated -ceq $line -or -not $seen.Add("$($s.Operator)|$($s.Index)|$($s.Text)")) { continue }
      [pscustomobject]@{ File = $File; Line = $i + 1; Operator = $s.Operator; Index = $s.Index; Original = $line; Mutated = $mutated }
    }
  }
}

# ---- sampling and equivalents ---------------------------------------------------------------------------------

# A seeded sample: the pool is put in a stable order first, so the seed alone decides the pick.
function Select-Sample($Mutants, [int]$Count, [int]$Seed) {
  $sorted = @($Mutants | Sort-Object @{ Expression = { $_.File }; Ascending = $true },
    @{ Expression = { $_.Line }; Ascending = $true }, @{ Expression = { $_.Operator }; Ascending = $true },
    @{ Expression = { $_.Index }; Ascending = $true }, @{ Expression = { $_.Mutated }; Ascending = $true })
  if ($Count -ge $sorted.Count) { return $sorted }
  $rng = [System.Random]::new($Seed)
  for ($i = $sorted.Count - 1; $i -gt 0; $i--) {
    $j = $rng.Next($i + 1)
    $tmp = $sorted[$i]; $sorted[$i] = $sorted[$j]; $sorted[$j] = $tmp
  }
  $picked = @($sorted[0..($Count - 1)])
  $picked | Sort-Object @{ Expression = { $_.File } }, @{ Expression = { $_.Line } }, @{ Expression = { $_.Operator } }, @{ Expression = { $_.Index } }
}

# Splits mutants into Kept and Skipped (judged equivalent). An entry matches on file + line + operator.
function Split-Equivalents($Mutants, $Equivalents) {
  $keys = [System.Collections.Generic.HashSet[string]]::new()
  foreach ($e in @($Equivalents)) { if ($e) { [void]$keys.Add("$($e.file -replace '\\', '/')|$($e.line)|$($e.operator)") } }
  $kept = [System.Collections.Generic.List[object]]::new()
  $skipped = [System.Collections.Generic.List[object]]::new()
  foreach ($m in @($Mutants)) {
    if ($keys.Contains("$($m.File -replace '\\', '/')|$($m.Line)|$($m.Operator)")) { $skipped.Add($m) } else { $kept.Add($m) }
  }
  [pscustomobject]@{ Kept = $kept.ToArray(); Skipped = $skipped.ToArray() }
}

function Format-Score($Result) {
  if ($Result.Total -eq 0) { return "$($Result.Killed) / $($Result.Total) = n/a" }
  '{0} / {1} = {2:N1}%' -f $Result.Killed, $Result.Total, (100.0 * $Result.Killed / $Result.Total)
}

# ---- the run loop ---------------------------------------------------------------------------------------------

function Assert-CleanTree([string]$RepoRoot) {
  $dirty = @(git -C $RepoRoot status --porcelain 2>&1)
  if ($LASTEXITCODE -ne 0) { throw "git status failed in ${RepoRoot}: $dirty" }
  if ($dirty.Count -gt 0) {
    throw "The working tree must be clean (commit or stash first): the run edits source files and restores them afterwards. Dirty: $((($dirty | Select-Object -First 5) -join '; '))"
  }
}

# Runs the sample. -Runner is called with a mutant (its file already mutated on disk) and returns one of
# Killed | Survived | Timeout | BuildError. Every touched file is restored in finally, even on Ctrl+C.
function Invoke-MutationRun {
  [CmdletBinding()]
  param(
    [Parameter(Mandatory)][string]$RepoRoot,
    [Parameter(Mandatory)][string[]]$Files,
    [Parameter(Mandatory)][scriptblock]$Runner,
    [int]$Sample = 0,
    [int]$Seed = 1,
    $Equivalents = @(),
    [scriptblock]$OnProgress = $null
  )

  Assert-CleanTree $RepoRoot

  $utf8 = [System.Text.UTF8Encoding]::new($false)
  $mutants = [System.Collections.Generic.List[object]]::new()
  foreach ($f in $Files) {
    $text = [System.IO.File]::ReadAllText((Join-Path $RepoRoot $f), $utf8)
    foreach ($m in @(Get-LineMutants -Lines ($text -split "`r?`n") -File ($f -replace '\\', '/'))) { $mutants.Add($m) }
  }
  $split = Split-Equivalents $mutants.ToArray() $Equivalents
  $pool = @($split.Kept)
  $picked = if ($Sample -gt 0) { @(Select-Sample $pool $Sample $Seed) } else { @(Select-Sample $pool ([int]::MaxValue) $Seed) }

  $originals = @{}   # path -> original bytes of every file we touch
  $result = [pscustomobject]@{
    Candidates = $pool.Count; Tried = 0; Killed = 0; Survived = 0; Timeout = 0; BuildErrors = 0
    Total = 0; Equivalent = $split.Skipped.Count; Survivors = [System.Collections.Generic.List[object]]::new()
  }

  try {
    foreach ($m in $picked) {
      $path = Join-Path $RepoRoot $m.File
      if (-not $originals.ContainsKey($path)) { $originals[$path] = [System.IO.File]::ReadAllBytes($path) }
      $orig = $originals[$path]
      $hasBom = $orig.Length -ge 3 -and $orig[0] -eq 0xEF -and $orig[1] -eq 0xBB -and $orig[2] -eq 0xBF
      $enc = [System.Text.UTF8Encoding]::new($hasBom)
      $text = $enc.GetString($orig, $(if ($hasBom) { 3 } else { 0 }), $orig.Length - $(if ($hasBom) { 3 } else { 0 }))
      $eol = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
      $lines = $text -split "`r?`n"
      $lines[$m.Line - 1] = $m.Mutated
      [System.IO.File]::WriteAllText($path, ($lines -join $eol), $enc)

      $status = & $Runner $m
      # Put the file back straight away so a Runner that reads other files sees clean code.
      [System.IO.File]::WriteAllBytes($path, $orig)

      $result.Tried++
      switch ([string]$status) {
        'BuildError' { $result.BuildErrors++ }
        'Survived' { $result.Survived++; $result.Total++; $result.Survivors.Add($m) }
        'Timeout' { $result.Timeout++; $result.Killed++; $result.Total++ }
        'Killed' { $result.Killed++; $result.Total++ }
        default { throw "The runner returned '$status' for $($m.File):$($m.Line); expected Killed, Survived, Timeout or BuildError." }
      }
      if ($OnProgress) { & $OnProgress $result $m $status }
    }
  } finally {
    foreach ($kv in $originals.GetEnumerator()) { [System.IO.File]::WriteAllBytes($kv.Key, $kv.Value) }
  }
  $result
}

# ---- the real runner: dotnet test ------------------------------------------------------------------------------

# Builds and tests one project at below-normal priority. Returns Status (Passed | Failed | Timeout | BuildError),
# the seconds it took, and the output.
function Invoke-DotnetTest([string]$TestProject, [int]$TimeoutSeconds, [string]$Filter = '') {
  $psi = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
  foreach ($a in @('test', $TestProject, '--no-restore', '-v', 'q', '--nologo')) { $psi.ArgumentList.Add($a) }
  if ($Filter) { $psi.ArgumentList.Add('--filter'); $psi.ArgumentList.Add($Filter) }
  $psi.RedirectStandardOutput = $true
  $psi.RedirectStandardError = $true
  $psi.UseShellExecute = $false
  $psi.Environment['DOTNET_NOLOGO'] = '1'
  $psi.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
  $timer = [System.Diagnostics.Stopwatch]::StartNew()
  $p = [System.Diagnostics.Process]::Start($psi)
  try { $p.PriorityClass = [System.Diagnostics.ProcessPriorityClass]::BelowNormal } catch { }
  $out = $p.StandardOutput.ReadToEndAsync()
  $err = $p.StandardError.ReadToEndAsync()
  if (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
    try { $p.Kill($true) } catch { }
    return [pscustomobject]@{ Status = 'Timeout'; Seconds = [int]$timer.Elapsed.TotalSeconds; Output = '' }
  }
  $p.WaitForExit()
  $text = $out.Result + "`n" + $err.Result
  $status = if ($p.ExitCode -eq 0) { 'Passed' }
  elseif ($text -match '(?m): error (CS|MSB|NETSDK|NU)\d+') { 'BuildError' }
  else { 'Failed' }
  [pscustomobject]@{ Status = $status; Seconds = [int][math]::Ceiling($timer.Elapsed.TotalSeconds); Output = $text }
}

Export-ModuleMember -Function Get-CodeMask, Get-LineMutants, Select-Sample, Split-Equivalents, Format-Score,
  Assert-CleanTree, Invoke-MutationRun, Invoke-DotnetTest
