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
        [Parameter(Mandatory)]$Runner, [Parameter(Mandatory)][string]$Root, [string]$Phase = '',
        [Parameter(Mandatory)][string]$FakeBin, [Parameter(Mandatory)][string]$TempBase, [hashtable]$Env = @{},
        [string[]]$ExtraArgs = @()
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
            $runArgs = @(ConvertTo-CheckPosixPath (Join-Path $Root 'scripts/check.sh'))
            if ($Phase) { $runArgs += $Phase }
            $lines = @(& $Runner.Exe @runArgs @ExtraArgs 2>&1 | ForEach-Object { "$_" })
        }
        else {
            $runArgs = @('-NoProfile', '-File', (Join-Path $Root 'scripts/check.ps1'))
            if ($Phase) { $runArgs += @('-Phase', $Phase) }
            $lines = @(& $Runner.Exe @runArgs @ExtraArgs 2>&1 | ForEach-Object { "$_" })
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

# ---- -Changed / --changed: the gate runs only what a change can affect, and format runs side by side ---------------------
# Each case runs the real script in a throwaway git checkout (a small copy of this repo's project layout, with origin/main
# at the base commit) with a recording dotnet and npm first on PATH: what the gate ran is what the fakes were called with.

function New-CheckRecordingTools {
    <#
      A fake dotnet and npm that record every call as one file in $LogDir (call-*.txt) and exit 0; a test call prints the
      passing summary line the gate looks for. With CM_FAKE_FORMAT_SLEEP set a format call also records when it started and
      ended (fmt-<id>-a.txt, fmt-<id>-b.txt), a second apart, so a test can see whether two of them overlapped.
      Returns the bin dir.
    #>
    param([switch]$Posix)
    $bin = Join-Path ([IO.Path]::GetTempPath()) ('cm-check-test-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force -Path $bin | Out-Null
    if ($IsWindows -and -not $Posix) {
        Set-Content -LiteralPath (Join-Path $bin 'dotnet.cmd') -Value @(
            '@echo off',
            'echo %* > "%CM_FAKE_DOTNET_LOG%\call-%1-%~n2-%RANDOM%.txt"',
            'if "%1"=="test" echo Passed!  - Failed: 0, Passed: 1, Skipped: 0, Total: 1',
            'if not "%1"=="format" exit /b 0',
            'if not defined CM_FAKE_FORMAT_SLEEP exit /b 0',
            'set "id=%~n2"',
            'echo %TIME% > "%CM_FAKE_DOTNET_LOG%\fmt-%id%-a.txt"',
            'ping -n 2 127.0.0.1 > nul',
            'echo %TIME% > "%CM_FAKE_DOTNET_LOG%\fmt-%id%-b.txt"',
            'exit /b 0')
        Set-Content -LiteralPath (Join-Path $bin 'npm.cmd') -Value @('@echo off', 'echo npm %* > "%CM_FAKE_DOTNET_LOG%\call-npm-%RANDOM%.txt"', 'exit /b 0')
    }
    else {
        Write-CheckFakeShellScript -Path (Join-Path $bin 'dotnet') -Lines @(
            '#!/bin/sh',
            'echo "$@" > "$(mktemp "$CM_FAKE_DOTNET_LOG/call-XXXXXX.txt")"',
            'if [ "$1" = test ]; then echo "Passed!  - Failed: 0, Passed: 1, Skipped: 0, Total: 1"; fi',
            'if [ "$1" = format ] && [ -n "$CM_FAKE_FORMAT_SLEEP" ]; then',
            '  id=$$',
            '  date +%s.%N > "$CM_FAKE_DOTNET_LOG/fmt-$id-a.txt"',
            '  sleep 1',
            '  date +%s.%N > "$CM_FAKE_DOTNET_LOG/fmt-$id-b.txt"',
            'fi',
            'exit 0')
        Write-CheckFakeShellScript -Path (Join-Path $bin 'npm') -Lines @('#!/bin/sh', 'echo "npm $@" > "$(mktemp "$CM_FAKE_DOTNET_LOG/call-XXXXXX.txt")"', 'exit 0')
    }
    $bin
}

# The layout of the throwaway checkout: project -> the projects it references (the names are this repo's own, so the gate's
# two support libraries and the Architecture tests are recognised).
$script:CheckChangedProjects = [ordered]@{
    'src/CollectionMaxxing.Domain'             = @()
    'src/CollectionMaxxing.Application'        = @('src/CollectionMaxxing.Domain')
    'src/CollectionMaxxing.Infrastructure'     = @('src/CollectionMaxxing.Application', 'src/CollectionMaxxing.Domain')
    'src/CollectionMaxxing.Api'                = @('src/CollectionMaxxing.Application', 'src/CollectionMaxxing.Infrastructure')
    'src/CollectionMaxxing.AppHost'            = @('src/CollectionMaxxing.Api')
    'tests/CollectionMaxxing.TestSupport'      = @('src/CollectionMaxxing.Infrastructure')
    'tests/CollectionMaxxing.Api.TestKit'      = @('src/CollectionMaxxing.Api', 'tests/CollectionMaxxing.TestSupport')
    'tests/CollectionMaxxing.Domain.Tests'     = @('src/CollectionMaxxing.Domain')
    'tests/CollectionMaxxing.Application.Tests' = @('src/CollectionMaxxing.Application', 'tests/CollectionMaxxing.TestSupport')
    'tests/CollectionMaxxing.Architecture.Tests' = @('src/CollectionMaxxing.Domain', 'src/CollectionMaxxing.Application', 'src/CollectionMaxxing.Infrastructure', 'src/CollectionMaxxing.Api')
    'tests/CollectionMaxxing.Api.Tests'        = @('src/CollectionMaxxing.Api', 'tests/CollectionMaxxing.TestSupport', 'tests/CollectionMaxxing.Api.TestKit')
    'tests/CollectionMaxxing.Recognition.Evals' = @('src/CollectionMaxxing.Application', 'src/CollectionMaxxing.Infrastructure', 'tests/CollectionMaxxing.TestSupport')
}
$script:CheckChangedLanes = @('Api.Tests', 'Application.Tests', 'Architecture.Tests', 'Domain.Tests', 'Recognition.Evals')
$script:CheckChangedProjectNames = @($script:CheckChangedProjects.Keys | ForEach-Object { ($_ -split '/')[1] -replace '^CollectionMaxxing\.', '' } | Sort-Object)

function Invoke-CheckGit {
    <# Real git, on the throwaway checkout only; it fails loudly. #>
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory, ValueFromRemainingArguments)][string[]]$GitArgs)
    if ($Root -notlike '*cm-check-test-*') { throw "refusing to run git outside a throwaway checkout: $Root" }
    $out = & git -C $Root -c user.name=cm-check-test -c user.email=cm-check-test@example.test -c commit.gpgsign=false -c core.autocrlf=false @GitArgs 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git $($GitArgs -join ' ') failed in ${Root}: $($out -join ' ')" }
}

function Set-CheckFile {
    <# Appends a line to a file of the throwaway checkout, making the file and its folders when it is new. #>
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$Relative, [string]$Text = 'x')
    $path = Join-Path $Root $Relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
    [IO.File]::AppendAllText($path, "$Text`n")
}

function New-CheckGitCheckout {
    <# The layout above with the real gate scripts, a git repository and origin/main at the base commit. #>
    param([Parameter(Mandatory)][string]$Sandbox, [Parameter(Mandatory)][string]$Parent)
    $root = Join-Path $Sandbox "$Parent/repo"
    New-Item -ItemType Directory -Force -Path (Join-Path $root 'scripts') | Out-Null
    Copy-Item -LiteralPath $script:CheckScript, $script:CheckShell -Destination (Join-Path $root 'scripts')
    Set-Content -LiteralPath (Join-Path $root 'scripts/check-comments.mjs') -Value "console.log('comments: ok');"
    Set-Content -LiteralPath (Join-Path $root 'CollectionMaxxing.slnx') -Value '<Solution />'
    foreach ($dir in $script:CheckChangedProjects.Keys) {
        $name = ($dir -split '/')[1]
        $refs = @($script:CheckChangedProjects[$dir] | ForEach-Object { "    <ProjectReference Include=""..\..\$($_ -replace '/', '\')\$(($_ -split '/')[1]).csproj"" />" })
        New-Item -ItemType Directory -Force -Path (Join-Path $root $dir) | Out-Null
        Set-Content -LiteralPath (Join-Path $root "$dir/$name.csproj") -Value (@('<Project Sdk="Microsoft.NET.Sdk">', '  <ItemGroup>') + $refs + @('  </ItemGroup>', '</Project>'))
        Set-CheckFile $root "$dir/Class.cs" 'class C {}'
    }
    foreach ($file in 'app/package.json', 'app/src/index.ts', 'app/playwright/serve.mjs', 'docs/readme.md', 'deploy/x.txt', 'evals/baseline.json', 'Directory.Build.props', 'tests/Directory.Build.props') {
        Set-CheckFile $root $file 'base'
    }
    Invoke-CheckGit $root init -q
    Invoke-CheckGit $root add -A
    Invoke-CheckGit $root commit -q -m base
    Invoke-CheckGit $root update-ref refs/remotes/origin/main HEAD
    $root
}

function Reset-CheckGitCheckout {
    param([Parameter(Mandatory)][string]$Root)
    Invoke-CheckGit $Root reset -q --hard refs/remotes/origin/main
    Invoke-CheckGit $Root clean -fdq
}

function Set-CheckChange {
    <# -Committed files are changed and committed on top of origin/main; -Dirty ones are changed (or made, untracked) and left uncommitted. #>
    param([Parameter(Mandatory)][string]$Root, [string[]]$Committed = @(), [string[]]$Dirty = @())
    $Committed = @($Committed | Where-Object { $_ })
    $Dirty = @($Dirty | Where-Object { $_ })
    foreach ($file in $Committed) { Set-CheckFile $Root $file 'committed' }
    if ($Committed.Count -gt 0) {
        Invoke-CheckGit $Root add -A
        Invoke-CheckGit $Root commit -q -m change
    }
    foreach ($file in $Dirty) { Set-CheckFile $Root $file 'dirty' }
}

function Invoke-CheckChanged {
    <# One gate run in a throwaway checkout. -Phase may be empty (the whole gate); -Changed adds -Changed (check.sh: --changed). #>
    param(
        [Parameter(Mandatory)]$Runner, [Parameter(Mandatory)][string]$Root, [string]$Phase = '', [switch]$Changed,
        [Parameter(Mandatory)][string]$FakeBin, [Parameter(Mandatory)][string]$TempBase, [Parameter(Mandatory)][string]$CallDir,
        [hashtable]$Env = @{}
    )
    Get-ChildItem -LiteralPath $CallDir -File -ErrorAction SilentlyContinue | Remove-Item -Force
    $callDirForChild = if ($Runner.Posix) { ConvertTo-CheckPosixPath $CallDir } else { $CallDir }
    $vars = @{ CM_FAKE_DOTNET_LOG = $callDirForChild; CM_FAKE_FORMAT_SLEEP = $null; CM_FORMAT_PARALLEL = $null; CM_TEST_ONLY = $null; CM_TEST_FILTER = $null }
    foreach ($name in $Env.Keys) { $vars[$name] = $Env[$name] }
    $runArgs = @()
    if ($Runner.Posix) {
        if ($Phase) { $runArgs += $Phase }
        if ($Changed) { $runArgs += '--changed' }
    }
    else {
        if ($Phase) { $runArgs += @('-Phase', $Phase) }
        if ($Changed) { $runArgs += '-Changed' }
    }
    $r = Invoke-CheckIn -Runner $Runner -Root $Root -Phase '' -FakeBin $FakeBin -TempBase $TempBase -Env $vars -ExtraArgs $runArgs
    $calls = @(Get-ChildItem -LiteralPath $CallDir -Filter 'call-*.txt' -File -ErrorAction SilentlyContinue | ForEach-Object { (Get-Content -LiteralPath $_.FullName -Raw).Trim() })
    $r | Add-Member -NotePropertyName Calls -NotePropertyValue $calls -PassThru
}

function ConvertTo-CheckSeconds {
    <# Seconds from "1730000000.123" (date +%s.%N) or from a cmd %TIME% such as " 9:05:01.23". #>
    param([Parameter(Mandatory)][string]$Text)
    $t = $Text.Trim()
    if ($t -match '^(\d+):(\d+):(\d+)[.,](\d+)$') { return ([int]$Matches[1] * 3600 + [int]$Matches[2] * 60 + [int]$Matches[3] + [double]("0.$($Matches[4])")) }
    [double]::Parse($t, [Globalization.CultureInfo]::InvariantCulture)
}

function Get-CheckCallProjects {
    <# The short project names (CollectionMaxxing. dropped) of the calls that start with $Verb, sorted. #>
    param([string[]]$Calls, [Parameter(Mandatory)][string]$Verb)
    @($Calls | Where-Object { $_ -match "^$Verb " } | ForEach-Object {
            if ($_ -match '([A-Za-z0-9.]+)\.csproj') { $Matches[1] -replace '^CollectionMaxxing\.', '' }
        } | Sort-Object)
}

function Get-CheckChangedLine {
    param([Parameter(Mandatory)]$Run)
    @($Run.Lines | Where-Object { $_ -like 'changed: *' })
}

function Assert-CheckRan {
    <#
      Compares what the gate ran with what the case expects. Restore, Build and App are booleans; Format and Test are lists of
      short project names (a missing key means none); Line is a regex the one `changed:` line must match.
    #>
    param([Parameter(Mandatory)]$Run, [Parameter(Mandatory)][string]$Label, [Parameter(Mandatory)][hashtable]$Expect)
    Assert-Equal 0 $Run.Exit "${Label}: exited $($Run.Exit): $($Run.Text)"
    $changedLines = @(Get-CheckChangedLine $Run)
    if ($Expect.ContainsKey('Line')) {
        Assert-Equal 1 $changedLines.Count "${Label}: want exactly one changed: line, got $($changedLines.Count): $($Run.Text)"
        Assert-Match ($changedLines -join "`n") $Expect.Line "${Label}: the changed: line"
    }
    else {
        Assert-Equal 0 $changedLines.Count "${Label}: no changed: line without -Changed: $($changedLines -join ' | ')"
    }
    if ($Expect.ContainsKey('Restore')) { Assert-Equal $Expect.Restore (@($Run.Calls | Where-Object { $_ -match '^restore ' }).Count -eq 1) "${Label}: restore ran" }
    if ($Expect.ContainsKey('Build')) { Assert-Equal $Expect.Build (@($Run.Calls | Where-Object { $_ -match '^build ' }).Count -eq 1) "${Label}: build ran" }
    if ($Expect.ContainsKey('App')) { Assert-Equal $Expect.App (@($Run.Calls | Where-Object { $_ -match '^npm ' }).Count -eq 1) "${Label}: the app phase ran" }
    if ($Expect.ContainsKey('Format')) {
        Assert-Equal (@($Expect.Format) -join ',') ((Get-CheckCallProjects $Run.Calls 'format') -join ',') "${Label}: formatted projects"
    }
    if ($Expect.ContainsKey('Test')) {
        Assert-Equal (@($Expect.Test) -join ',') ((Get-CheckCallProjects $Run.Calls 'test') -join ',') "${Label}: tested projects"
    }
}

# Windows PowerShell 5.1 has a case of its own; the table-driven ones run on pwsh and bash.
$script:CheckModernRunners = { $_.Exe -notlike '*powershell.exe' }

function Use-CheckChangedSandbox {
    <# Per runner: a fake toolbox, a throwaway checkout and the folders around them; $PerRunner gets (runner, label, root, bin, tmp, calls). #>
    param([Parameter(Mandatory)][scriptblock]$PerRunner, [scriptblock]$RunnerFilter = { $true })
    Use-CheckSandbox {
        param($sandbox, $bins)
        foreach ($runner in (Get-CheckRunners | Where-Object $RunnerFilter)) {
            $bin = New-CheckRecordingTools -Posix:$runner.Posix
            $bins.Add($bin)
            $tmp = Join-Path $sandbox ('tmp-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
            $calls = Join-Path $sandbox ('calls-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
            New-Item -ItemType Directory -Force -Path $tmp, $calls | Out-Null
            $root = New-CheckGitCheckout -Sandbox $sandbox -Parent ('checkout-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
            & $PerRunner $runner $runner.Name $root $bin $tmp $calls
        }
    }
}

# Classification, without running the phases: -Phase restore prints the changed: line and runs the one cheap fake call, so the
# line (and whether restore ran at all) is the whole answer. The same rule decides in CI.
Test-Case 'changed: the area is picked the way CI picks it (app-only, server-only, anything else or nothing is everything)' {
    $everything = '^changed: everything'
    $cases = @(
        @{ Name = 'app only'; Committed = @('app/src/index.ts'); Dirty = @('app/src/new.ts'); Restore = $false
            Line = '^changed: app-only, 2 files\. skipped: restore, build, format and test \(nothing outside app/ changed\)$' },
        @{ Name = 'app and serve.mjs'; Committed = @('app/playwright/serve.mjs'); Dirty = @('app/src/index.ts'); Restore = $true
            Line = '^changed: everything, 2 files\. skipped: nothing \(app/playwright/serve\.mjs is read by the backend tests\)$' },
        @{ Name = 'serve.mjs alone'; Dirty = @('app/playwright/serve.mjs'); Restore = $true
            Line = '^changed: everything, 1 file\. skipped: nothing \(app/playwright/serve\.mjs is read by the backend tests\)$' },
        @{ Name = 'app and src'; Committed = @('app/src/index.ts'); Dirty = @('src/CollectionMaxxing.Api/Class.cs'); Restore = $true
            Line = '^changed: everything, 2 files\. skipped: nothing \(the change touches both the app and the server\)$' },
        @{ Name = 'docs only'; Committed = @('docs/readme.md'); Restore = $true
            Line = '^changed: everything, 1 file\. skipped: nothing \(docs/readme\.md is outside app/ and the server folders\)$' },
        @{ Name = 'a script'; Dirty = @('scripts/new-tool.sh'); Restore = $true
            Line = '^changed: everything, 1 file\. skipped: nothing \(scripts/new-tool\.sh is outside app/ and the server folders\)$' },
        @{ Name = 'a file at the root'; Dirty = @('Directory.Build.props'); Restore = $true
            Line = '^changed: everything, 1 file\. skipped: nothing \(Directory\.Build\.props is outside app/ and the server folders\)$' },
        @{ Name = 'no change at all'; Restore = $true
            Line = '^changed: everything\. skipped: nothing \(no changed files found\)$' },
        @{ Name = 'a server change'; Dirty = @('deploy/x.txt'); Restore = $true
            Line = '^changed: server-only, 1 file\. skipped: app phase \(nothing under app/ changed\);' }
    )
    Use-CheckChangedSandbox -RunnerFilter $script:CheckModernRunners {
        param($runner, $label, $root, $bin, $tmp, $calls)
        foreach ($case in $cases) {
            Reset-CheckGitCheckout $root
            Set-CheckChange -Root $root -Committed @($case.Committed) -Dirty @($case.Dirty)
            $run = Invoke-CheckChanged -Runner $runner -Root $root -Phase 'restore' -Changed -FakeBin $bin -TempBase $tmp -CallDir $calls
            Assert-CheckRan $run "$label / $($case.Name)" @{ Line = $case.Line; Restore = $case.Restore }
        }
        # Without origin/main there is nothing to compare with: everything runs, and it says why.
        Reset-CheckGitCheckout $root
        Invoke-CheckGit $root update-ref '-d' refs/remotes/origin/main
        Set-CheckChange -Root $root -Dirty @('app/src/new.ts')
        $run = Invoke-CheckChanged -Runner $runner -Root $root -Phase 'restore' -Changed -FakeBin $bin -TempBase $tmp -CallDir $calls
        Assert-CheckRan $run "$label / no origin/main" @{ Line = '^changed: everything\. skipped: nothing \(cannot tell what changed: no origin/main to compare with\)$'; Restore = $true }
    }
}

# Which test and format projects a server-only change selects, again through the one line it prints.
Test-Case 'changed: a server change tests the projects that reference what changed, and formats only the projects that changed' {
    $skippedApp = 'skipped: app phase \(nothing under app/ changed\)'
    $cases = @(
        @{ Name = 'the Api project'; Dirty = @('src/CollectionMaxxing.Api/Class.cs')
            Line = "^changed: server-only, 1 file\. $skippedApp; 3 of 5 test projects \(not affected\); format of 11 of 12 projects \(unchanged\)\. testing: Api\.Tests, Architecture\.Tests\. formatting: Api\.$" },
        @{ Name = 'the Application project, committed'; Committed = @('src/CollectionMaxxing.Application/Class.cs')
            Line = "^changed: server-only, 1 file\. $skippedApp; 1 of 5 test projects \(not affected\); format of 11 of 12 projects \(unchanged\)\. testing: Api\.Tests, Application\.Tests, Architecture\.Tests, Recognition\.Evals\. formatting: Application\.$" },
        @{ Name = 'the Domain project'; Dirty = @('src/CollectionMaxxing.Domain/Class.cs')
            Line = "^changed: server-only, 1 file\. $skippedApp; format of 11 of 12 projects \(unchanged\)\. testing: Api\.Tests, Application\.Tests, Architecture\.Tests, Domain\.Tests, Recognition\.Evals\. formatting: Domain\.$" },
        @{ Name = 'the AppHost project, which no test project references'; Dirty = @('src/CollectionMaxxing.AppHost/Class.cs')
            Line = "^changed: server-only, 1 file\. $skippedApp; 4 of 5 test projects \(not affected\); format of 11 of 12 projects \(unchanged\)\. testing: Architecture\.Tests\. formatting: AppHost\.$" },
        @{ Name = 'one test project'; Dirty = @('tests/CollectionMaxxing.Domain.Tests/Class.cs')
            Line = "^changed: server-only, 1 file\. $skippedApp; 3 of 5 test projects \(not affected\); format of 11 of 12 projects \(unchanged\)\. testing: Architecture\.Tests, Domain\.Tests\. formatting: Domain\.Tests\.$" },
        @{ Name = 'a test support library'; Dirty = @('tests/CollectionMaxxing.Api.TestKit/Class.cs')
            Line = "^changed: server-only, 1 file\. $skippedApp; 3 of 5 test projects \(not affected\); format of 11 of 12 projects \(unchanged\)\. testing: Api\.Tests, Architecture\.Tests\. formatting: Api\.TestKit\.$" },
        @{ Name = 'two projects, one committed and one not'; Committed = @('src/CollectionMaxxing.Domain/Class.cs'); Dirty = @('tests/CollectionMaxxing.Domain.Tests/Class.cs')
            Line = "^changed: server-only, 2 files\. $skippedApp; format of 10 of 12 projects \(unchanged\)\. testing: Api\.Tests, Application\.Tests, Architecture\.Tests, Domain\.Tests, Recognition\.Evals\. formatting: Domain, Domain\.Tests\.$" },
        @{ Name = 'a shared test file'; Committed = @('tests/Directory.Build.props')
            Line = "^changed: server-only, 1 file\. $skippedApp\. Everything else runs: tests/Directory\.Build\.props is shared by the projects\.$" },
        @{ Name = 'a file in src/ that no project owns'; Dirty = @('src/shared.props')
            Line = "^changed: server-only, 1 file\. $skippedApp\. Everything else runs: src/shared\.props is shared by the projects\.$" },
        @{ Name = 'deploy only'; Dirty = @('deploy/x.txt')
            Line = "^changed: server-only, 1 file\. $skippedApp; all 5 test projects \(none is affected\); format of all 12 projects \(none changed\)\.$" },
        @{ Name = 'evals only'; Committed = @('evals/baseline.json')
            Line = "^changed: server-only, 1 file\. $skippedApp; 4 of 5 test projects \(not affected\); format of all 12 projects \(none changed\)\. testing: Recognition\.Evals\.$" }
    )
    Use-CheckChangedSandbox -RunnerFilter $script:CheckModernRunners {
        param($runner, $label, $root, $bin, $tmp, $calls)
        foreach ($case in $cases) {
            Reset-CheckGitCheckout $root
            Set-CheckChange -Root $root -Committed @($case.Committed) -Dirty @($case.Dirty)
            $run = Invoke-CheckChanged -Runner $runner -Root $root -Phase 'restore' -Changed -FakeBin $bin -TempBase $tmp -CallDir $calls
            Assert-CheckRan $run "$label / $($case.Name)" @{ Line = $case.Line; Restore = $true }
        }
    }
}

# What the phases then do with that scope, end to end: the fakes are called only for the selected projects.
Test-Case 'changed: an app-only change runs just the app phase' {
    Use-CheckChangedSandbox {
        param($runner, $label, $root, $bin, $tmp, $calls)
        Set-CheckChange -Root $root -Committed @('app/src/index.ts')
        $run = Invoke-CheckChanged -Runner $runner -Root $root -Changed -FakeBin $bin -TempBase $tmp -CallDir $calls
        Assert-CheckRan $run "$label / app only" @{ Line = '^changed: app-only, 1 file\.'; Restore = $false; Build = $false; App = $true; Format = @(); Test = @() }
        Assert-Equal 1 $run.Calls.Count "${label}: nothing but npm ran: $($run.Calls -join ' | ')"
        Assert-Match $run.Text 'all green \(\d+s, changed files only\)' "${label}: the summary says the run was narrowed"
    }
}

Test-Case 'changed: a server change runs restore, build and format and tests for the selected projects only, and never the app' {
    Use-CheckChangedSandbox {
        param($runner, $label, $root, $bin, $tmp, $calls)
        Set-CheckChange -Root $root -Dirty @('src/CollectionMaxxing.Api/Class.cs')
        $run = Invoke-CheckChanged -Runner $runner -Root $root -Changed -FakeBin $bin -TempBase $tmp -CallDir $calls
        Assert-CheckRan $run "$label / the Api project" @{
            Line = '^changed: server-only, 1 file\.'; Restore = $true; Build = $true; App = $false; Format = @('Api'); Test = @('Api.Tests', 'Architecture.Tests') }
        # One phase at a time, as CI and the agent run it, with the same narrowing.
        $testOnly = Invoke-CheckChanged -Runner $runner -Root $root -Phase 'test' -Changed -FakeBin $bin -TempBase $tmp -CallDir $calls
        Assert-CheckRan $testOnly "$label / test phase alone" @{ Restore = $false; Build = $false; Format = @(); Test = @('Api.Tests', 'Architecture.Tests'); Line = '^changed: server-only' }
    }
}

Test-Case 'changed: a server change that no test project can feel skips the test phase and says so' {
    Use-CheckChangedSandbox {
        param($runner, $label, $root, $bin, $tmp, $calls)
        Set-CheckChange -Root $root -Dirty @('deploy/x.txt')
        $run = Invoke-CheckChanged -Runner $runner -Root $root -Changed -FakeBin $bin -TempBase $tmp -CallDir $calls
        Assert-CheckRan $run "$label / deploy only" @{ Line = '^changed: server-only, 1 file\.'; Restore = $true; Build = $true; App = $false; Format = @(); Test = @() }
        Assert-Match $run.Text 'ok   test: skipped, no test project is affected by this change' "${label}: the skipped test phase is reported"
    }
}

Test-Case 'changed: without -Changed the whole gate runs, whatever changed' {
    Use-CheckChangedSandbox -RunnerFilter $script:CheckModernRunners {
        param($runner, $label, $root, $bin, $tmp, $calls)
        Set-CheckChange -Root $root -Committed @('app/src/index.ts')
        $run = Invoke-CheckChanged -Runner $runner -Root $root -FakeBin $bin -TempBase $tmp -CallDir $calls
        Assert-CheckRan $run "$label / no -Changed" @{ Restore = $true; Build = $true; App = $true; Format = $script:CheckChangedProjectNames; Test = $script:CheckChangedLanes }
        Assert-Match $run.Text 'all green \(\d+s\)' "${label}: the plain summary"
    }
}

Test-Case 'changed: Windows PowerShell 5.1 narrows the gate the same way' {
    $legacy = @(Get-CheckEngines | Where-Object { $_ -like '*powershell.exe' })
    if ($legacy.Count -gt 0) {
        Use-CheckChangedSandbox -RunnerFilter { $_.Exe -like '*powershell.exe' } {
            param($runner, $label, $root, $bin, $tmp, $calls)
            Set-CheckChange -Root $root -Dirty @('tests/CollectionMaxxing.Domain.Tests/Class.cs')
            $run = Invoke-CheckChanged -Runner $runner -Root $root -Changed -FakeBin $bin -TempBase $tmp -CallDir $calls
            Assert-CheckRan $run "$label / a test project" @{ Restore = $true; Build = $true; App = $false; Format = @('Domain.Tests'); Test = @('Architecture.Tests', 'Domain.Tests'); Line = '^changed: server-only, 1 file\.' }
        }
    }
}

Test-Case 'check format: projects are formatted side by side, not one after another' {
    Use-CheckChangedSandbox -RunnerFilter $script:CheckModernRunners {
        param($runner, $label, $root, $bin, $tmp, $calls)
        $run = Invoke-CheckChanged -Runner $runner -Root $root -Phase 'format' -FakeBin $bin -TempBase $tmp -CallDir $calls -Env @{ CM_FAKE_FORMAT_SLEEP = '1'; CM_FORMAT_PARALLEL = '4' }
        Assert-Equal 0 $run.Exit "${label}: exited $($run.Exit): $($run.Text)"
        Assert-Equal ($script:CheckChangedProjectNames -join ',') ((Get-CheckCallProjects $run.Calls 'format') -join ',') "${label}: every project is still formatted"
        $spans = @(Get-ChildItem -LiteralPath $calls -Filter 'fmt-*-a.txt' | ForEach-Object {
                $id = $_.Name -replace '^fmt-(.+)-a\.txt$', '$1'
                [pscustomobject]@{
                    Start = ConvertTo-CheckSeconds (Get-Content -LiteralPath $_.FullName -Raw)
                    End   = ConvertTo-CheckSeconds (Get-Content -LiteralPath (Join-Path $calls "fmt-$id-b.txt") -Raw)
                }
            })
        Assert-Equal 12 $spans.Count "${label}: every format call recorded when it ran"
        $overlapping = 0
        foreach ($a in $spans) { $overlapping = [math]::Max($overlapping, @($spans | Where-Object { $_.Start -lt $a.End -and $a.Start -lt $_.End }).Count) }
        Assert-True ($overlapping -ge 3) "${label}: at most $overlapping format runs overlapped; they should run side by side"
    }
}
