# Tests for the local gate scripts (scripts/check.ps1 and scripts/check.sh): the format phase runs `dotnet format` per
# project and must survive dotnet-format's flaky 'Warnings were encountered while loading the workspace' stderr line
# (exit 0), including under Windows PowerShell 5.1, where merged native stderr throws under $ErrorActionPreference Stop
# (#398); and each checkout keeps its phase logs in a folder of its own, so gate runs in parallel worktrees never read
# each other's logs.
# Each case runs the real script in a child process with a fake dotnet first on PATH, under every engine available
# (pwsh always; powershell.exe where it exists, which is the #398 regression leg; bash for check.sh where it exists).
# Dot-sourced by Run-Tests.ps1.

$script:CheckScript = Join-Path $PSScriptRoot '../../scripts/check.ps1'
$script:CheckShell = Join-Path $PSScriptRoot '../../scripts/check.sh'

function Get-CheckEngines {
    $pwsh = Get-Command pwsh -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $pwsh) { throw 'pwsh was not found; the gate tests need it' }
    $engines = @($pwsh.Source)
    $ps51 = Get-Command powershell.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($ps51) { $engines += $ps51.Source }
    $engines
}

function New-CheckFakeDotnet {
    <#
      A fake dotnet first on PATH for the child gate run. 'warn' prints the flaky workspace warning to stderr
      and exits 0 (#398); 'fail' adds a genuine file diagnostic that names the folder it ran in and exits 2.
      -Posix writes the sh flavour even on Windows, for check.sh under Git Bash. Returns the bin dir.
    #>
    param([Parameter(Mandatory)][string]$Mode, [switch]$Posix)
    $bin = Join-Path ([IO.Path]::GetTempPath()) ('cm-check-test-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force -Path $bin | Out-Null
    if ($IsWindows -and -not $Posix) {
        $lines = @('@echo off', 'echo Warnings were encountered while loading the workspace. 1>&2')
        if ($Mode -eq 'fail') { $lines += 'echo src/Fake/Fake.cs(1,1): error WHITESPACE: fake violation in %CD%' }
        $lines += if ($Mode -eq 'fail') { 'exit /b 2' } else { 'exit /b 0' }
        Set-Content -LiteralPath (Join-Path $bin 'dotnet.cmd') -Value $lines
    }
    else {
        $lines = @('#!/bin/sh', "echo 'Warnings were encountered while loading the workspace.' 1>&2")
        if ($Mode -eq 'fail') { $lines += 'echo "src/Fake/Fake.cs(1,1): error WHITESPACE: fake violation in $(pwd)"' }
        $lines += if ($Mode -eq 'fail') { 'exit 2' } else { 'exit 0' }
        Write-CheckFakeShellScript -Path (Join-Path $bin 'dotnet') -Lines $lines
    }
    $bin
}

function Write-CheckFakeShellScript {
    <# LF endings and no BOM, or the shebang line breaks; executable where the file system has a mode. #>
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string[]]$Lines)
    [IO.File]::WriteAllText($Path, (($Lines -join "`n") + "`n"))
    if (-not $IsWindows) {
        & chmod +x $Path
        if ($LASTEXITCODE -ne 0) { throw "chmod +x failed on the fake tool at $Path" }
    }
}

function Add-CheckFakeNpm {
    <# A fake npm beside the fake dotnet: it reports the app gate's log dir and fails, so the gate prints what it saw. #>
    param([Parameter(Mandatory)][string]$Bin, [switch]$Posix)
    if ($IsWindows -and -not $Posix) {
        Set-Content -LiteralPath (Join-Path $Bin 'npm.cmd') -Value @('@echo off', 'echo app log dir=%CM_APP_CHECK_LOG_DIR%', 'exit /b 1')
    }
    else {
        Write-CheckFakeShellScript -Path (Join-Path $Bin 'npm') -Lines @('#!/bin/sh', 'echo "app log dir=$CM_APP_CHECK_LOG_DIR"', 'exit 1')
    }
}

function Remove-CheckFakeDotnet {
    param([string]$Bin)
    # Only ever deletes folders this file created.
    if ($Bin -like '*cm-check-test-*' -and (Test-Path -LiteralPath $Bin)) { Remove-Item -LiteralPath $Bin -Recurse -Force }
}

function Assert-CheckFakeResolves {
    <# The child inherits our PATH, so the fake must already win here or the case would run the real dotnet. #>
    param([Parameter(Mandatory)][string]$Bin)
    $oldPath = $env:PATH
    $env:PATH = $Bin + [IO.Path]::PathSeparator + $oldPath
    try {
        $found = Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -First 1
        Assert-True $found 'dotnet resolves at all with the fake bin on PATH'
        Assert-Match $found.Source 'cm-check-test-' "the fake dotnet wins PATH (got $($found.Source))"
    }
    finally {
        $env:PATH = $oldPath
    }
}

function Invoke-CheckFormatChild {
    param([Parameter(Mandatory)][string]$Engine, [Parameter(Mandatory)][string]$FakeBin)
    $oldPath = $env:PATH
    $env:PATH = $FakeBin + [IO.Path]::PathSeparator + $oldPath
    try {
        $lines = @(& $Engine -NoProfile -File $script:CheckScript -Phase format 2>&1 | ForEach-Object { "$_" })
        [pscustomobject]@{ Exit = $LASTEXITCODE; Lines = @($lines) }
    }
    finally {
        $env:PATH = $oldPath
    }
}

Test-Case 'check format: flaky workspace stderr still passes on every engine (#398)' {
    $bin = New-CheckFakeDotnet -Mode 'warn'
    try {
        Assert-CheckFakeResolves $bin
        foreach ($engine in (Get-CheckEngines)) {
            $r = Invoke-CheckFormatChild -Engine $engine -FakeBin $bin
            Assert-Equal 0 $r.Exit "$engine exited $($r.Exit): $($r.Lines -join ' | ')"
            Assert-Match ($r.Lines -join "`n") '^ok   format' "$engine did not report ok"
        }
    }
    finally {
        Remove-CheckFakeDotnet $bin
    }
}

Test-Case 'check format: genuine violations still fail on every engine' {
    $bin = New-CheckFakeDotnet -Mode 'fail'
    try {
        Assert-CheckFakeResolves $bin
        foreach ($engine in (Get-CheckEngines)) {
            $r = Invoke-CheckFormatChild -Engine $engine -FakeBin $bin
            Assert-Equal 1 $r.Exit "$engine exited $($r.Exit), want 1: $($r.Lines -join ' | ')"
            Assert-Match ($r.Lines -join "`n") 'FAIL format' "$engine did not report FAIL"
        }
    }
    finally {
        Remove-CheckFakeDotnet $bin
    }
}

# ---- log folders: one per checkout ----------------------------------------------------------------------------------
# Agents run the gate at the same time in different worktrees of this repo on one PC. Their logs must not meet: a run
# that reads another run's format.log reports the other worktree's violations as its own.

function Get-CheckBash {
    <# Git for Windows' bash or the system bash; never Windows' own bash.exe (System32), which starts WSL. #>
    $candidates = @()
    if ($env:ProgramFiles) { $candidates += (Join-Path $env:ProgramFiles 'Git/bin/bash.exe'), (Join-Path $env:ProgramFiles 'Git/usr/bin/bash.exe') }
    $candidates += '/bin/bash', '/usr/bin/bash'
    $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
}

function ConvertTo-CheckPosixPath {
    <# Git for Windows' bash wants /c/Users/... where PowerShell has C:\Users\...; on Linux the path is already right. #>
    param([Parameter(Mandatory)][string]$Path)
    $p = $Path -replace '\\', '/'
    if ($p -match '^([A-Za-z]):(.*)$') { $p = '/' + $Matches[1].ToLowerInvariant() + $Matches[2] }
    $p
}

function ConvertFrom-CheckPosixPath {
    param([Parameter(Mandatory)][string]$Path)
    if ($IsWindows -and $Path -match '^/([A-Za-z])(/.*)?$') { return ($Matches[1].ToUpperInvariant() + ':' + ($Matches[2] -replace '/', '\')) }
    $Path
}

function Get-CheckRunners {
    <# Every way the gate can run here: each PowerShell engine for check.ps1, and bash for check.sh. #>
    $runners = @(Get-CheckEngines | ForEach-Object { [pscustomobject]@{ Name = $_; Exe = $_; Posix = $false } })
    $bash = Get-CheckBash
    if ($bash) { $runners += [pscustomobject]@{ Name = "$bash (check.sh)"; Exe = $bash; Posix = $true } }
    $runners
}

function New-CheckFakeCheckout {
    <#
      A throwaway checkout: the real gate scripts, a fake source and test project for the format phase and an app folder
      for the app phase. Parents differ between checkouts but the folder name stays 'repo', as two worktrees can end in
      the same name.
    #>
    param([Parameter(Mandatory)][string]$Sandbox, [Parameter(Mandatory)][string]$Parent)
    $root = Join-Path $Sandbox "$Parent/repo"
    New-Item -ItemType Directory -Force -Path (Join-Path $root 'scripts'), (Join-Path $root 'src/Fake'), (Join-Path $root 'tests/Fake'), (Join-Path $root 'app') | Out-Null
    Copy-Item -LiteralPath $script:CheckScript, $script:CheckShell -Destination (Join-Path $root 'scripts')
    Set-Content -LiteralPath (Join-Path $root 'src/Fake/Fake.csproj') -Value '<Project />'
    Set-Content -LiteralPath (Join-Path $root 'tests/Fake/Fake.Tests.csproj') -Value '<Project />'
    Set-Content -LiteralPath (Join-Path $root 'app/package.json') -Value '{}'
    $root
}

function Set-CheckEnvVar {
    <#
      Sets an environment variable of this process, or removes it for a null value. [Environment]::SetEnvironmentVariable
      with a null from PowerShell leaves the variable behind as an empty string, and an empty TMPDIR or GITHUB_ACTIONS
      left in this process would reach the bash suites that run after these cases.
    #>
    param([Parameter(Mandatory)][string]$Name, $Value)
    if ($null -eq $Value) { Remove-Item -LiteralPath "Env:$Name" -ErrorAction SilentlyContinue }
    else { Set-Item -LiteralPath "Env:$Name" -Value $Value }
}

function Invoke-CheckIn {
    <#
      Runs one phase of the gate script that lives in $Root. The temp folder is a throwaway one, so nothing lands in
      the real shared temp; -Env adds (or, with a null value, removes) environment variables for the child.
    #>
    param(
        [Parameter(Mandatory)]$Runner, [Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$Phase,
        [Parameter(Mandatory)][string]$FakeBin, [Parameter(Mandatory)][string]$TempBase, [hashtable]$Env = @{}
    )
    $vars = @{
        PATH                 = $FakeBin + [IO.Path]::PathSeparator + $env:PATH
        TMP                  = $TempBase
        TEMP                 = $TempBase
        TMPDIR               = (ConvertTo-CheckPosixPath $TempBase)
        CM_APP_CHECK_LOG_DIR = $null
        GITHUB_ACTIONS       = $null
    }
    foreach ($name in $Env.Keys) { $vars[$name] = $Env[$name] }
    $saved = @{}
    foreach ($name in $vars.Keys) {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name)
        Set-CheckEnvVar $name $vars[$name]
    }
    try {
        if ($Runner.Posix) {
            $lines = @(& $Runner.Exe (ConvertTo-CheckPosixPath (Join-Path $Root 'scripts/check.sh')) $Phase 2>&1 | ForEach-Object { "$_" })
        }
        else {
            $lines = @(& $Runner.Exe -NoProfile -File (Join-Path $Root 'scripts/check.ps1') -Phase $Phase 2>&1 | ForEach-Object { "$_" })
        }
        [pscustomobject]@{ Exit = $LASTEXITCODE; Lines = @($lines); Text = ($lines -join "`n") }
    }
    finally {
        foreach ($name in $saved.Keys) { Set-CheckEnvVar $name $saved[$name] }
    }
}

function Get-CheckLogPath {
    <# The log file a failed gate run names in its "(full output: ...)" line, as a path this process can read. #>
    param([Parameter(Mandatory)]$Run)
    $m = [regex]::Match($Run.Text, '(?m)\(full output: (.+)\)\s*$')
    if (-not $m.Success) { throw "the gate named no log file: $($Run.Text)" }
    ConvertFrom-CheckPosixPath $m.Groups[1].Value
}

function Get-CheckAppLogDir {
    <# The log dir the fake npm saw in CM_APP_CHECK_LOG_DIR, as printed by the failed app step. #>
    param([Parameter(Mandatory)]$Run)
    $m = [regex]::Match($Run.Text, '(?m)app log dir=(.*?)\s*$')
    if (-not $m.Success) { throw "the app step did not report its log dir: $($Run.Text)" }
    $m.Groups[1].Value
}

function Format-CheckSlashes { param([string]$Path) ($Path -replace '\\', '/').TrimEnd('/') }

function Use-CheckSandbox {
    <# Runs $Body with a throwaway sandbox (and the fake tools it creates) and deletes only what this file created. #>
    param([Parameter(Mandatory)][scriptblock]$Body)
    $sandbox = Join-Path ([IO.Path]::GetTempPath()) ('cm-check-test-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force -Path $sandbox | Out-Null
    $bins = [System.Collections.Generic.List[string]]::new()
    try {
        & $Body $sandbox $bins
    }
    finally {
        foreach ($bin in $bins) { Remove-CheckFakeDotnet $bin }
        if ($sandbox -like '*cm-check-test-*' -and (Test-Path -LiteralPath $sandbox)) { Remove-Item -LiteralPath $sandbox -Recurse -Force }
    }
}

Test-Case 'check logs: two checkouts keep their logs in different folders, and one never reads the other''s' {
    Use-CheckSandbox {
        param($sandbox, $bins)
        foreach ($runner in (Get-CheckRunners)) {
            $label = $runner.Name
            $bin = New-CheckFakeDotnet -Mode 'fail' -Posix:$runner.Posix
            $bins.Add($bin)
            $tmp = Join-Path $sandbox ('tmp-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
            New-Item -ItemType Directory -Force -Path $tmp | Out-Null
            $one = New-CheckFakeCheckout -Sandbox $sandbox -Parent ('checkout-one-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
            $two = New-CheckFakeCheckout -Sandbox $sandbox -Parent ('checkout-two-' + [guid]::NewGuid().ToString('N').Substring(0, 6))

            $first = Invoke-CheckIn -Runner $runner -Root $one -Phase 'format' -FakeBin $bin -TempBase $tmp
            $other = Invoke-CheckIn -Runner $runner -Root $two -Phase 'format' -FakeBin $bin -TempBase $tmp
            $again = Invoke-CheckIn -Runner $runner -Root $one -Phase 'format' -FakeBin $bin -TempBase $tmp
            foreach ($r in $first, $other, $again) { Assert-Equal 1 $r.Exit "$label exited $($r.Exit), want 1: $($r.Text)" }

            $logOne = Get-CheckLogPath $first
            $logTwo = Get-CheckLogPath $other
            Assert-True ($logOne -ne $logTwo) "${label}: both checkouts wrote $logOne"
            Assert-True ((Split-Path -Parent $logOne) -ne (Split-Path -Parent $logTwo)) "${label}: both checkouts share the folder of $logOne"
            Assert-Equal $logOne (Get-CheckLogPath $again) "${label}: a checkout must reuse its own folder between runs"

            # What each run reported came from its own log, and each log still holds only its own checkout's output
            # even though the other checkout ran in between.
            Assert-Match $first.Text 'checkout-one-' "${label}: the first run did not report its own violation"
            Assert-False ($first.Text -match 'checkout-two-') "${label}: the first run reported the other checkout's output: $($first.Text)"
            Assert-Match $other.Text 'checkout-two-' "${label}: the second run did not report its own violation"
            Assert-False ($other.Text -match 'checkout-one-') "${label}: the second run reported the other checkout's output: $($other.Text)"
            $contentOne = Get-Content -Raw -LiteralPath $logOne
            $contentTwo = Get-Content -Raw -LiteralPath $logTwo
            Assert-True ($contentOne -match 'checkout-one-' -and $contentOne -notmatch 'checkout-two-') "${label}: the first checkout's log holds the other's output: $contentOne"
            Assert-True ($contentTwo -match 'checkout-two-' -and $contentTwo -notmatch 'checkout-one-') "${label}: the second checkout's log holds the other's output: $contentTwo"
        }
    }
}

Test-Case 'check logs: the app gate logs inside the checkout''s own folder, so its logs are separate too' {
    Use-CheckSandbox {
        param($sandbox, $bins)
        foreach ($runner in (Get-CheckRunners)) {
            $label = $runner.Name
            $bin = New-CheckFakeDotnet -Mode 'fail' -Posix:$runner.Posix
            Add-CheckFakeNpm -Bin $bin -Posix:$runner.Posix
            $bins.Add($bin)
            $tmp = Join-Path $sandbox ('tmp-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
            New-Item -ItemType Directory -Force -Path $tmp | Out-Null
            $one = New-CheckFakeCheckout -Sandbox $sandbox -Parent ('checkout-one-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
            $two = New-CheckFakeCheckout -Sandbox $sandbox -Parent ('checkout-two-' + [guid]::NewGuid().ToString('N').Substring(0, 6))

            $runOne = Invoke-CheckIn -Runner $runner -Root $one -Phase 'app' -FakeBin $bin -TempBase $tmp
            $runTwo = Invoke-CheckIn -Runner $runner -Root $two -Phase 'app' -FakeBin $bin -TempBase $tmp
            $appOne = Get-CheckAppLogDir $runOne
            $appTwo = Get-CheckAppLogDir $runTwo
            Assert-True ((Format-CheckSlashes $appOne) -ne (Format-CheckSlashes $appTwo)) "${label}: both checkouts' app gates log to $appOne"
            $folderOne = Format-CheckSlashes (Split-Path -Parent (Get-CheckLogPath $runOne))
            Assert-Equal "$folderOne/app" (Format-CheckSlashes (ConvertFrom-CheckPosixPath $appOne)) "${label}: the app logs belong beside the checkout's other logs"
        }
    }
}

Test-Case 'check logs: on CI the app gate keeps the log dir the workflow uploads; a stale value elsewhere is ignored' {
    Use-CheckSandbox {
        param($sandbox, $bins)
        foreach ($runner in (Get-CheckRunners)) {
            $label = $runner.Name
            $bin = New-CheckFakeDotnet -Mode 'fail' -Posix:$runner.Posix
            Add-CheckFakeNpm -Bin $bin -Posix:$runner.Posix
            $bins.Add($bin)
            $tmp = Join-Path $sandbox ('tmp-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
            New-Item -ItemType Directory -Force -Path $tmp | Out-Null
            $root = New-CheckFakeCheckout -Sandbox $sandbox -Parent ('checkout-one-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
            $uploaded = Join-Path $sandbox 'runner-temp/cm-app-check'
            if ($runner.Posix) { $uploaded = ConvertTo-CheckPosixPath $uploaded }

            $ci = Invoke-CheckIn -Runner $runner -Root $root -Phase 'app' -FakeBin $bin -TempBase $tmp -Env @{ GITHUB_ACTIONS = 'true'; CM_APP_CHECK_LOG_DIR = $uploaded }
            Assert-Equal $uploaded (Get-CheckAppLogDir $ci) "${label}: CI's upload folder was replaced"

            # Outside CI a value left over from an earlier in-process run would point every checkout at one folder again.
            $stale = Join-Path $sandbox 'stale-from-another-run'
            $local = Invoke-CheckIn -Runner $runner -Root $root -Phase 'app' -FakeBin $bin -TempBase $tmp -Env @{ CM_APP_CHECK_LOG_DIR = $stale }
            Assert-False ((Get-CheckAppLogDir $local) -like '*stale-from-another-run*') "${label}: a stale CM_APP_CHECK_LOG_DIR outside CI was honoured: $(Get-CheckAppLogDir $local)"
        }
    }
}
