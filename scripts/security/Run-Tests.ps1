<#
.SYNOPSIS
  Tests for the scripts in scripts/security:  pwsh -NoProfile -File scripts/security/Run-Tests.ps1

.DESCRIPTION
  Every docker/dotnet/npm call goes through Invoke-Sec, which these tests replace with a recorder, so nothing is scanned and
  no stack is started. Two tests use the real Docker daemon, and only to make and remove two containers that are never started
  (named cm-sec-test-*): they prove Stop-Security removes the labelled one and leaves the other alone.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$env:CM_SEC_TEST = '1'
$timer = [System.Diagnostics.Stopwatch]::StartNew()
. (Join-Path $PSScriptRoot 'Security.Common.ps1')
. (Join-Path $PSScriptRoot 'Security.Stack.ps1')
. (Join-Path $PSScriptRoot 'Security.Zap.ps1')

$passed = 0
$failures = [System.Collections.Generic.List[string]]::new()
function Test-Case {
    param([string]$Name, [scriptblock]$Body)
    $global:Calls = [System.Collections.Generic.List[object]]::new()
    $global:SecRunner = $null
    $global:SecSleep = { param($s) }
    try { & $Body; $script:passed++ }
    catch { $script:failures.Add("FAIL $Name`n     $($_.Exception.Message)") }
    finally { $global:SecRunner = $null }
}
function Assert-True { param($Condition, [string]$Message = 'expected true') if (-not $Condition) { throw $Message } }
function Assert-Equal { param($Expected, $Actual, [string]$Message = '') if ("$Expected" -ne "$Actual") { throw "expected [$Expected] but got [$Actual] $Message" } }
function Assert-Throws {
    param([scriptblock]$Body, [string]$Pattern)
    $text = $null
    try { & $Body } catch { $text = $_.Exception.Message }
    if ($null -eq $text) { throw "expected an error matching /$Pattern/ but nothing was thrown" }
    if ($text -notmatch $Pattern) { throw "expected /$Pattern/ in [$text]" }
}
function Use-Recorder {
    param([scriptblock]$Answer = { param($c, $a) [pscustomobject]@{ ExitCode = 0; Output = @() } })
    $global:SecAnswer = $Answer
    $global:SecRunner = {
        param($Command, $Arguments, $Env)
        $global:Calls.Add([pscustomobject]@{ Command = $Command; Display = "$Command $($Arguments -join ' ')"; Arguments = $Arguments; Env = $Env })
        & $global:SecAnswer $Command $Arguments $Env
    }
}
$scriptsDir = $PSScriptRoot
function Use-StackRecorder {
    # Answers like a healthy Docker for Start-SecStack: the migration script exists, the fake Auth0 says it is listening, a port is published.
    Use-Recorder {
        param($c, $a)
        if ($c -eq 'dotnet') { Set-Content -LiteralPath $a[$a.IndexOf('--output') + 1] -Value 'select 1;' }
        if ($a[0] -eq 'port') { return [pscustomobject]@{ ExitCode = 0; Output = @('127.0.0.1:49321') } }
        if ($a[0] -eq 'logs') { return [pscustomobject]@{ ExitCode = 0; Output = @('fake auth0 listening on 8443') } }
        [pscustomobject]@{ ExitCode = 0; Output = @() }
    }
}
function Invoke-ScriptProcess {
    param([string]$Script, [string[]]$Arguments)
    $saved = $env:CM_SEC_TEST
    $env:CM_SEC_TEST = ''   # the child process uses the real commands (the cases here never reach a scan)
    try {
        $out = & pwsh -NoProfile -File (Join-Path $scriptsDir $Script) @Arguments 2>&1 | ForEach-Object { "$_" }
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Text = ($out -join "`n") }
    }
    finally { $env:CM_SEC_TEST = $saved }
}

# ---- ports and Docker ---------------------------------------------------------------------------------------------------

foreach ($port in 8080, 8409, 32400, 8096) {
    foreach ($script in 'Run-ZapBaseline.ps1', 'Run-ZapApi.ps1') {
        Test-Case "$script refuses port $port and says what to do" {
            $r = Invoke-ScriptProcess $script @('-Port', "$port")
            Assert-True ($r.ExitCode -ne 0) 'should fail'
            Assert-True ($r.Text -match "Refusing to use host port $port") "got: $($r.Text)"
            Assert-True ($r.Text -match 'random loopback port') 'should say what to do'
        }
    }
}

Test-Case 'Assert-PortAllowed accepts 0 and ordinary ports' {
    Assert-PortAllowed 0
    Assert-PortAllowed 49152
}

Test-Case 'The published port Docker picks is checked too' {
    Assert-Throws { Assert-PortAllowed 8096 'published port' } 'published port 8096'
}

Test-Case 'Every script refuses to run when Docker is off and says to start Docker Desktop' {
    Use-Recorder { param($c, $a) if ($a[0] -eq 'info') { [pscustomobject]@{ ExitCode = 1; Output = @('Cannot connect') } } else { [pscustomobject]@{ ExitCode = 0; Output = @() } } }
    Assert-Throws { Assert-DockerUp } 'Docker is not running\. Start Docker Desktop'
    $code = Invoke-ZapRun -Kind baseline -Port 0 -Threads 2 -MaxMinutes 10 -WebDist 'deploy/docker/no-web'
    Assert-Equal 1 $code
    Assert-True (@($global:Calls | Where-Object { $_.Arguments[0] -in 'build', 'run', 'create' -or $_.Display -like 'docker network create*' }).Count -eq 0) 'nothing may start when Docker is off'
}

Test-Case 'Run-ImageScan and Stop-Security stop with the Docker message when the daemon is off' {
    # No docker CLI answer is possible in a child process we cannot fake, so use PATH without docker.
    $dir = Join-Path ([IO.Path]::GetTempPath()) "cm-sec-test-path-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $pwsh = (Get-Command pwsh).Source
    $saved = $env:PATH
    try {
        $env:PATH = "$(Split-Path $pwsh)$([IO.Path]::PathSeparator)$dir"
        foreach ($s in 'Run-ImageScan.ps1', 'Stop-Security.ps1') {
            $r = Invoke-ScriptProcess $s @()
            Assert-True ($r.ExitCode -ne 0) "$s should fail"
            Assert-True ($r.Text -match 'Start Docker Desktop') "$s said: $($r.Text)"
        }
    }
    finally { $env:PATH = $saved; Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
}

# ---- dry runs ---------------------------------------------------------------------------------------------------------------

foreach ($script in 'Run-ZapBaseline.ps1', 'Run-ZapApi.ps1', 'Run-ImageScan.ps1') {
    Test-Case "$script -DryRun prints the plan and starts nothing" {
        $r = Invoke-ScriptProcess $script @('-DryRun')
        Assert-Equal 0 $r.ExitCode $r.Text
        foreach ($needle in 'images', 'cm.agent=qa-1c', 'thread cap', 'dry run: nothing was started') { Assert-True ($r.Text -match [regex]::Escape($needle)) "missing [$needle] in: $($r.Text)" }
        if ($script -ne 'Run-ImageScan.ps1') { foreach ($needle in 'network', 'cm-sec-net-', 'zap mode', '2 per host') { Assert-True ($r.Text -match [regex]::Escape($needle)) "missing [$needle]" } }
    }
}

Test-Case 'A dry run makes no external call at all' {
    Use-Recorder
    $code = Invoke-ZapRun -Kind api -DryRun -Port 0 -Threads 2 -MaxMinutes 10 -WebDist 'x' 6>$null
    Assert-Equal 0 $code
    Assert-Equal 0 $global:Calls.Count 'a dry run must not call docker, dotnet or npm'
}

Test-Case 'The plan names the ZAP mode for each scan' {
    Assert-True ((Get-SecPlan -Mode zap-baseline -RunId 'r1').ZapMode -match 'baseline') 'baseline'
    Assert-True ((Get-SecPlan -Mode zap-api -RunId 'r1').ZapMode -match 'api scan') 'api'
}

# ---- Stop-Security ----------------------------------------------------------------------------------------------------------

Test-Case 'Stop-Security asks Docker only for the labelled things and removes only those ids' {
    Use-Recorder {
        param($c, $a)
        $text = $a -join ' '
        if ($text -eq 'ps -a -q --filter label=cm.agent=qa-1c') { return [pscustomobject]@{ ExitCode = 0; Output = @('aaaaaaaaaaaa', 'bbbbbbbbbbbb') } }
        [pscustomobject]@{ ExitCode = 0; Output = @() }
    }
    $r = Remove-SecResources
    Assert-Equal 2 $r.Containers
    $removes = @($global:Calls | Where-Object { $_.Display -like 'docker rm -f *' } | ForEach-Object { $_.Arguments[2] })
    Assert-Equal 'aaaaaaaaaaaa,bbbbbbbbbbbb' ($removes -join ',')
    Assert-True (@($global:Calls | Where-Object { $_.Display -match 'prune|down -v|volume' }).Count -eq 0) 'no prune, no volumes'
    foreach ($c in $global:Calls) { if ($c.Arguments[0] -in 'ps', 'network', 'image') { Assert-True ($c.Display -match 'label=cm\.agent=qa-1c') "unfiltered listing: $($c.Display)" } }
}

Test-Case 'Stop-Security removes the labelled container and leaves an unlabelled one alone (real Docker, nothing is started)' {
    $labelled = "cm-sec-test-labelled-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
    $other = "cm-sec-test-other-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
    try {
        & docker create --name $labelled --label cm.agent=qa-1c postgres:17 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'docker create failed (is Docker running and postgres:17 pulled?)' }
        & docker create --name $other --label cm.agent=someone-else postgres:17 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'docker create (other) failed' }
        $r = Invoke-ScriptProcess 'Stop-Security.ps1' @()
        Assert-Equal 0 $r.ExitCode $r.Text
        $names = @(& docker ps -a --format '{{.Names}}' 2>$null)
        Assert-True ($names -notcontains $labelled) 'the labelled container should be gone'
        Assert-True ($names -contains $other) 'the other container must still be there'
    }
    finally { & docker rm -f $labelled $other 2>&1 | Out-Null }
}

# ---- reports stay out of the repository ---------------------------------------------------------------------------------------

Test-Case 'The default report folder is in the temp folder, outside the working tree' {
    $dir = Get-ReportDir 'run1' $null
    Assert-True ($dir.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) $dir
    Assert-True (-not $dir.StartsWith($script:RepoRoot, [StringComparison]::OrdinalIgnoreCase)) 'inside the repo'
}

Test-Case 'A report folder inside the repository is refused' {
    Assert-Throws { Get-ReportDir 'run1' (Join-Path $script:RepoRoot 'reports') } 'inside the repository'
    Assert-Throws { Get-ReportDir 'run1' $script:RepoRoot } 'inside the repository'
}

Test-Case 'Work and secrets folders are checked the same way' {
    Assert-Throws { Assert-OutsideRepo (Join-Path $script:RepoRoot 'scripts/security/secrets') } 'inside the repository'
    Assert-OutsideRepo (Join-Path ([IO.Path]::GetTempPath()) 'cm-sec-work-x')
}

Test-Case 'The scripts never name a path in the repository for output' {
    foreach ($f in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1') {
        if ($f.Name -eq 'Run-Tests.ps1') { continue }
        $text = Get-Content -LiteralPath $f.FullName -Raw
        Assert-True ($text -notmatch 'Join-Path \$script:RepoRoot .(reports|zap-out|out)') "$($f.Name) writes into the repo"
    }
}

# ---- ZAP limits ---------------------------------------------------------------------------------------------------------------

Test-Case 'ZAP is limited to 2 threads per host and a wall-clock cap, in the arguments and in the guard' {
    Assert-Throws { Test-ZapLimits 3 10 } 'capped at 2 threads'
    Assert-Throws { Test-ZapLimits 0 10 } 'capped at 2 threads'
    Assert-Throws { Test-ZapLimits 2 0 } 'wall-clock cap'
    Assert-Throws { Test-ZapLimits 2 31 } 'wall-clock cap'
    Test-ZapLimits 2 10
    foreach ($kind in 'baseline', 'api') {
        $text = (Get-ZapArguments -Kind $kind -Target 'http://api:5480' -Threads 2 -MaxMinutes 10) -join ' '
        Assert-True ($text -match 'scanner\.threadPerHost=2') "$kind threadPerHost"
        Assert-True ($text -match 'spider\.thread=2') "$kind spider thread"
        Assert-True ($text -match 'scanner\.hostPerScan=1') "$kind hostPerScan"
        Assert-True ($text -match '-T 10') "$kind wall clock"
        Assert-True ($text -match 'maxScanDurationInMins=10') "$kind scan duration"
    }
}

Test-Case 'The default wall-clock cap is 10 minutes and the default threads are 2' {
    foreach ($s in 'Run-ZapBaseline.ps1', 'Run-ZapApi.ps1') {
        $text = Get-Content -LiteralPath (Join-Path $PSScriptRoot $s) -Raw
        Assert-True ($text -match '\[int\]\$Threads = 2') "$s threads"
        Assert-True ($text -match '\[int\]\$MaxMinutes = 10') "$s minutes"
    }
}

Test-Case 'A ZAP container is capped in CPU and memory, labelled, named cm-sec-* and never publishes a port' {
    Use-Recorder { param($c, $a) if ($a[0] -eq 'inspect') { [pscustomobject]@{ ExitCode = 0; Output = @('false 0') } } else { [pscustomobject]@{ ExitCode = 0; Output = @() } } }
    $reports = Join-Path ([IO.Path]::GetTempPath()) "cm-sec-test-$([guid]::NewGuid().ToString('N'))"
    try { Invoke-ZapScan -Kind api -RunId 'r9' -Network 'cm-sec-net-r9' -Target 'http://api:5480' -Threads 2 -MaxMinutes 10 -ReportDir $reports -Token 'SECRET-TOKEN-VALUE' | Out-Null }
    finally { Remove-Item -LiteralPath $reports -Recurse -Force -ErrorAction SilentlyContinue }
    $create = $global:Calls | Where-Object { $_.Arguments[0] -eq 'create' } | Select-Object -First 1
    Assert-True ($create.Display -match '--name cm-sec-zap-api-r9') 'name'
    Assert-True ($create.Display -match '--label cm\.agent=qa-1c') 'label'
    Assert-True ($create.Display -match '--cpus 2') 'cpus'
    Assert-True ($create.Display -match '--memory 2g') 'memory'
    Assert-True ($create.Display -notmatch ' -p ') 'no published port'
    Assert-True ($create.Display -match '--network cm-sec-net-r9') 'isolated network'
}

Test-Case 'The bearer token reaches ZAP through the environment, never a command line or a file' {
    Use-Recorder { param($c, $a) if ($a[0] -eq 'inspect') { [pscustomobject]@{ ExitCode = 0; Output = @('false 0') } } else { [pscustomobject]@{ ExitCode = 0; Output = @() } } }
    $reports = Join-Path ([IO.Path]::GetTempPath()) "cm-sec-test-$([guid]::NewGuid().ToString('N'))"
    try { Invoke-ZapScan -Kind api -RunId 'r8' -Network 'cm-sec-net-r8' -Target 'http://api:5480' -Threads 2 -MaxMinutes 10 -ReportDir $reports -Token 'SECRET-TOKEN-VALUE' | Out-Null }
    finally { Remove-Item -LiteralPath $reports -Recurse -Force -ErrorAction SilentlyContinue }
    foreach ($c in $global:Calls) { Assert-True ($c.Display -notmatch 'SECRET-TOKEN-VALUE') "token on a command line: $($c.Command) $($c.Arguments[0])" }
    $create = $global:Calls | Where-Object { $_.Arguments[0] -eq 'create' } | Select-Object -First 1
    Assert-Equal 'SECRET-TOKEN-VALUE' $create.Env['CM_ZAP_TOKEN']
    Assert-True ($create.Display -match '-e CM_ZAP_TOKEN( |$)') 'passes the variable by name only'
    $baselineCreate = $null
    $global:Calls.Clear()
    $reports = Join-Path ([IO.Path]::GetTempPath()) "cm-sec-test-$([guid]::NewGuid().ToString('N'))"
    try { Invoke-ZapScan -Kind baseline -RunId 'r7' -Network 'n' -Target 'http://api:5480' -Threads 2 -MaxMinutes 10 -ReportDir $reports | Out-Null }
    finally { Remove-Item -LiteralPath $reports -Recurse -Force -ErrorAction SilentlyContinue }
    $baselineCreate = $global:Calls | Where-Object { $_.Arguments[0] -eq 'create' } | Select-Object -First 1
    Assert-True ($baselineCreate.Env.Count -eq 0) 'the baseline scan carries no token'
}

Test-Case 'On Linux the report folder is opened for the ZAP user before the container is created; on Windows nothing is changed' {
    foreach ($unix in $true, $false) {
        $global:Calls.Clear()
        $global:SecIsUnix = $unix
        Use-Recorder { param($c, $a) if ($a[0] -eq 'inspect') { [pscustomobject]@{ ExitCode = 0; Output = @('false 0') } } else { [pscustomobject]@{ ExitCode = 0; Output = @() } } }
        $reports = Join-Path ([IO.Path]::GetTempPath()) "cm-sec-test-$([guid]::NewGuid().ToString('N'))"
        try { Invoke-ZapScan -Kind baseline -RunId 'r6' -Network 'n' -Target 'http://api:5480' -Threads 2 -MaxMinutes 10 -ReportDir $reports | Out-Null }
        finally { $global:SecIsUnix = $null; Remove-Item -LiteralPath $reports -Recurse -Force -ErrorAction SilentlyContinue }
        $order = @($global:Calls | ForEach-Object { $_.Command + ' ' + $_.Arguments[0] })
        $chmod = @($global:Calls | Where-Object { $_.Command -eq 'chmod' })
        if ($unix) {
            Assert-Equal 1 $chmod.Count 'chmod calls on unix'
            Assert-Equal 'a+rwX' $chmod[0].Arguments[1]
            Assert-Equal $reports $chmod[0].Arguments[2]
            Assert-True ($order.IndexOf('chmod -R') -lt $order.IndexOf('docker create')) 'chmod must come before docker create'
        }
        else { Assert-Equal 0 $chmod.Count 'no chmod on Windows' }
    }
}

Test-Case 'On Linux the fake Auth0 folder is opened for the containers'' users (the runner is another uid); on Windows nothing is changed' {
    foreach ($unix in $true, $false) {
        $global:Calls.Clear()
        $global:SecIsUnix = $unix
        $dir = Join-Path ([IO.Path]::GetTempPath()) "cm-sec-test-$([guid]::NewGuid().ToString('N'))"
        Use-StackRecorder
        try { try { Start-SecStack -RunId 'r4' -Port 0 -ApiImage 'cm-sec-api:r4' -WorkDir $dir -ReadyAttempts 1 | Out-Null } catch { if ($_.Exception.Message -notmatch 'did not become ready') { throw } } }
        finally { $global:SecIsUnix = $null; Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
        $order = @($global:Calls | ForEach-Object { $_.Command + ' ' + $_.Arguments[0] })
        $chmod = @($global:Calls | Where-Object { $_.Command -eq 'chmod' })
        if ($unix) {
            Assert-Equal 1 $chmod.Count 'chmod calls on unix'
            Assert-True ($chmod[0].Arguments[2] -like '*fake-auth0') 'the fake Auth0 folder'
            Assert-True ($order.IndexOf('chmod -R') -lt $order.IndexOf('docker run')) 'chmod must come before the API starts'
        }
        else { Assert-Equal 0 $chmod.Count 'no chmod on Windows' }
    }
}

Test-Case 'ZAP exit 2 (warnings) with a report fails the run; exit 0 passes; exit 2 without a report points at the folder permissions' {
    $counts = [ordered]@{ High = 0; Medium = 0; Low = 1; Informational = 0 }
    $warn = [pscustomobject]@{ ExitCode = 2; TimedOut = $false; Counts = $counts; Names = @('10096 Timestamp Disclosure - Unix [Low]'); ReportFound = $true; Summary = @() }
    $pass = [pscustomobject]@{ ExitCode = 0; TimedOut = $false; Counts = $counts; Names = @(); ReportFound = $true; Summary = @() }
    $none = [pscustomobject]@{ ExitCode = 2; TimedOut = $false; Counts = $counts; Names = @(); ReportFound = $false; Summary = @() }
    Assert-True (-not (Write-ZapResult baseline $warn 'x' 6>$null)) 'exit 2 with unexplained warnings fails'
    Assert-True (Write-ZapResult baseline $pass 'x' 6>$null) 'exit 0 passes'
    Assert-True (-not (Write-ZapResult baseline $none 'x' 6>$null)) 'no report fails'
    $text = (Write-ZapResult baseline $none 'x' 6>&1 | Out-String)
    Assert-True ($text -match 'not writable') 'the message points at the folder permissions'
}

Test-Case 'A scan that runs past the cap is stopped and reported as partial' {
    Use-Recorder { param($c, $a) if ($a[0] -eq 'inspect') { [pscustomobject]@{ ExitCode = 0; Output = @('true 0') } } else { [pscustomobject]@{ ExitCode = 0; Output = @() } } }
    $global:SecSleep = { param($s) Start-Sleep -Milliseconds 400 }
    $r = Wait-SecContainer -Name 'cm-sec-zap-x' -MaxMinutes 0 -PollSeconds 1
    Assert-True $r.TimedOut 'should time out'
    Assert-True (@($global:Calls | Where-Object { $_.Display -like 'docker stop*' }).Count -eq 1) 'should stop the container'
}

Test-Case 'ZAP alerts are counted by level from a report' {
    $file = Join-Path ([IO.Path]::GetTempPath()) "cm-sec-test-$([guid]::NewGuid().ToString('N')).json"
    try {
        @{ site = @(@{ alerts = @(@{ pluginid = '1'; name = 'a'; riskcode = '3'; riskdesc = 'High (Medium)' }, @{ pluginid = '2'; name = 'b'; riskcode = '1'; riskdesc = 'Low (High)' }, @{ pluginid = '3'; name = 'c'; riskcode = '0'; riskdesc = 'Informational (Low)' }) }) } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $file
        $c = Get-ZapCounts $file
        Assert-Equal '1 0 1 1' "$($c.Counts.High) $($c.Counts.Medium) $($c.Counts.Low) $($c.Counts.Informational)"
    }
    finally { Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue }
}

# ---- the rules file ---------------------------------------------------------------------------------------------------------------

Test-Case '.zap/rules.tsv explains every ignored alert with a reason and uses only known actions' {
    $path = Join-Path $script:RepoRoot '.zap/rules.tsv'
    Assert-True (Test-Path -LiteralPath $path) '.zap/rules.tsv is missing'
    $lines = Get-Content -LiteralPath $path
    $previousComment = $null
    $rules = 0
    foreach ($line in $lines) {
        if ($line -match '^\s*#') { $previousComment = $line; continue }
        if ($line.Trim() -eq '') { $previousComment = $null; continue }
        $cols = $line -split "`t"
        Assert-True ($cols.Count -ge 3) "need id<TAB>ACTION<TAB>name: [$line]"
        Assert-True ($cols[0] -match '^\d+$') "alert id is a number: [$line]"
        Assert-True ($cols[1] -in 'IGNORE', 'INFO', 'WARN', 'FAIL', 'OUTOFSCOPE') "unknown action: [$line]"
        if ($cols[1] -in 'IGNORE', 'OUTOFSCOPE') {
            Assert-True ($previousComment -match '^\s*#\s*Reason:\s*\S.{30,}') "an ignored alert needs a '# Reason: ...' comment right above it: [$line]"
        }
        $rules++
        $previousComment = $null
    }
}

# ---- dependency audit parsing -----------------------------------------------------------------------------------------------------

Test-Case 'The dotnet vulnerability list is parsed, and test-only findings are told apart from shipping ones' {
    $lines = @(
        "Project 'CollectionMaxxing.Api' has the following vulnerable packages",
        "   [net10.0]:",
        "   Top-level Package      Requested   Resolved   Severity   Advisory URL",
        "   > Some.Package         1.0.0       1.0.0      High       https://github.com/advisories/GHSA-aaaa",
        "Project 'CollectionMaxxing.Api.Tests' has the following vulnerable packages",
        "   Transitive Package                       Resolved   Severity   Advisory URL",
        "   > System.Security.Cryptography.Pkcs      10.0.11    Moderate   https://github.com/advisories/GHSA-bbbb")
    $found = @(ConvertFrom-DotnetVulnerable $lines)
    Assert-Equal 2 $found.Count
    Assert-Equal 'False' $found[0].TestOnly
    Assert-Equal 'True' $found[1].TestOnly
    Assert-Equal 'System.Security.Cryptography.Pkcs' $found[1].Package
}

# ---- the fake Auth0, the tokens and the throwaway stack ----------------------------------------------------------------------------------

function ConvertFrom-TestBase64Url {
    param([string]$Text)
    $s = $Text.Replace('-', '+').Replace('_', '/')
    switch ($s.Length % 4) { 2 { $s += '==' } 3 { $s += '=' } }
    , [Convert]::FromBase64String($s)
}
function ConvertFrom-TestJwt {
    param([string]$Token)
    $parts = $Token.Split('.')
    [pscustomobject]@{
        Parts = $parts
        Header = [Text.Encoding]::UTF8.GetString((ConvertFrom-TestBase64Url $parts[0])) | ConvertFrom-Json
        Payload = [Text.Encoding]::UTF8.GetString((ConvertFrom-TestBase64Url $parts[1])) | ConvertFrom-Json
        Signature = ConvertFrom-TestBase64Url $parts[2]
        SigningInput = [Text.Encoding]::ASCII.GetBytes("$($parts[0]).$($parts[1])")
    }
}
function New-TestAuth0 {
    param([string]$Dir)
    New-FakeAuth0Material -Directory $Dir -Users @($script:SecOwner, $script:SecEditor)
}
function Remove-TestDir { param([string]$Dir) Remove-Item -LiteralPath $Dir -Recurse -Force -ErrorAction SilentlyContinue }
function New-TestDirPath { Join-Path ([IO.Path]::GetTempPath()) "cm-sec-test-$([guid]::NewGuid().ToString('N'))" }

Test-Case 'A minted token is an RS256 JWT from the fake for the API audience, and its signature checks against the published key' {
    $dir = New-TestDirPath
    try {
        $auth0 = New-TestAuth0 $dir
        $now = [DateTimeOffset]::new(2026, 10, 23, 12, 0, 0, [TimeSpan]::Zero)
        $jwt = ConvertFrom-TestJwt (New-FakeAuth0Token $auth0 'auth0|cm-sec-owner' -Now $now)
        Assert-Equal 3 $jwt.Parts.Count
        Assert-Equal 'RS256' $jwt.Header.alg
        Assert-Equal 'https://fake-auth0:8443/' $jwt.Payload.iss
        Assert-Equal 'urn:collectionmaxxing:api' $jwt.Payload.aud
        Assert-Equal 'auth0|cm-sec-owner' $jwt.Payload.sub
        Assert-Equal $now.ToUnixTimeSeconds() $jwt.Payload.iat
        Assert-Equal ($now.ToUnixTimeSeconds() + 3600) $jwt.Payload.exp

        $jwks = Get-Content -LiteralPath (Join-Path $dir 'jwks.json') -Raw | ConvertFrom-Json
        Assert-Equal $jwt.Header.kid $jwks.keys[0].kid
        $rsa = [System.Security.Cryptography.RSA]::Create()
        $rsa.ImportParameters([System.Security.Cryptography.RSAParameters]@{ Modulus = (ConvertFrom-TestBase64Url $jwks.keys[0].n); Exponent = (ConvertFrom-TestBase64Url $jwks.keys[0].e) })
        $sha = [System.Security.Cryptography.HashAlgorithmName]::SHA256; $pkcs1 = [System.Security.Cryptography.RSASignaturePadding]::Pkcs1
        Assert-True ($rsa.VerifyData($jwt.SigningInput, $jwt.Signature, $sha, $pkcs1)) 'the signature should verify with the published key'
        $tampered = [Text.Encoding]::ASCII.GetBytes($jwt.Parts[0] + '.' + $jwt.Parts[1] + 'x')
        Assert-True (-not $rsa.VerifyData($tampered, $jwt.Signature, $sha, $pkcs1)) 'a changed token must not verify'
    }
    finally { Remove-TestDir $dir }
}

Test-Case 'Each run gets its own signing key and its own CA' {
    $a = New-TestDirPath; $b = New-TestDirPath
    try {
        $one = New-TestAuth0 $a; $two = New-TestAuth0 $b
        Assert-True ($one.KeyId -ne $two.KeyId) 'the key id differs between runs'
        Assert-True ((Get-Content -LiteralPath (Join-Path $a 'ca/ca.pem') -Raw) -ne (Get-Content -LiteralPath (Join-Path $b 'ca/ca.pem') -Raw)) 'the CA differs between runs'
        Assert-True ((Get-Content -LiteralPath (Join-Path $a 'jwks.json') -Raw) -ne (Get-Content -LiteralPath (Join-Path $b 'jwks.json') -Raw)) 'the JWKS differs between runs'
    }
    finally { Remove-TestDir $a; Remove-TestDir $b }
}

Test-Case 'The discovery document, the JWKS and the server certificate agree, and the certificate chains to the per-run CA only' {
    $dir = New-TestDirPath
    try {
        $auth0 = New-TestAuth0 $dir
        $doc = Get-Content -LiteralPath (Join-Path $dir 'openid-configuration.json') -Raw | ConvertFrom-Json
        Assert-Equal $auth0.Issuer $doc.issuer
        Assert-Equal 'https://fake-auth0:8443/userinfo' $doc.userinfo_endpoint
        Assert-Equal 'https://fake-auth0:8443/.well-known/jwks.json' $doc.jwks_uri
        Assert-True ($doc.id_token_signing_alg_values_supported -contains 'RS256') 'RS256'

        $ca = [System.Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPem((Get-Content -LiteralPath (Join-Path $dir 'ca/ca.pem') -Raw))
        $leaf = [System.Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPem((Get-Content -LiteralPath (Join-Path $dir 'server.pem') -Raw))
        $san = $leaf.Extensions | Where-Object { $_ -is [System.Security.Cryptography.X509Certificates.X509SubjectAlternativeNameExtension] }
        Assert-True (@($san.EnumerateDnsNames()) -contains 'fake-auth0') 'the certificate names the fake'
        $chain = [System.Security.Cryptography.X509Certificates.X509Chain]::new()
        $chain.ChainPolicy.TrustMode = [System.Security.Cryptography.X509Certificates.X509ChainTrustMode]::CustomRootTrust
        $chain.ChainPolicy.CustomTrustStore.Add($ca) | Out-Null
        $chain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
        Assert-True $chain.Build($leaf) "the certificate should chain to the CA: $($chain.ChainStatus.StatusInformation -join ' ')"

        $other = [System.Security.Cryptography.X509Certificates.X509Chain]::new()
        $other.ChainPolicy.TrustMode = [System.Security.Cryptography.X509Certificates.X509ChainTrustMode]::CustomRootTrust
        $other.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
        Assert-True (-not $other.Build($leaf)) 'without the per-run CA the certificate is not trusted'
    }
    finally { Remove-TestDir $dir }
}

Test-Case 'Only the CA certificate goes to the API; the server key and the signing key stay with the fake and the harness' {
    $dir = New-TestDirPath
    try {
        New-TestAuth0 $dir | Out-Null
        $files = @(Get-ChildItem -LiteralPath (Join-Path $dir 'ca') -Recurse -File | ForEach-Object Name)
        Assert-Equal 'ca.pem' ($files -join ',')
        Assert-True ((Get-Content -LiteralPath (Join-Path $dir 'ca/ca.pem') -Raw) -notmatch 'PRIVATE KEY') 'no private key in the CA folder'
        Assert-True ((Get-Content -LiteralPath (Join-Path $dir 'server.key') -Raw) -match 'PRIVATE KEY') 'the fake has its own server key'
        $mount = (Get-FakeAuth0ApiMount -Directory $dir) -join ' '
        Assert-True ($mount -match [regex]::Escape("source=$(Join-Path $dir 'ca'),target=/fake-auth0-ca,readonly")) "mount: $mount"
        $public = (Get-Content -LiteralPath (Join-Path $dir 'jwks.json') -Raw), (Get-Content -LiteralPath (Join-Path $dir 'openid-configuration.json') -Raw) -join "`n"
        Assert-True ($public -notmatch '"d"\s*:|"p"\s*:|"q"\s*:') 'no private RSA parameters in the JWKS or the discovery document'
        Assert-True ((Get-Content -LiteralPath (Join-Path $dir 'signing.json') -Raw) -match '"d"') 'the fake has the signing key to sign the web ID token'
    }
    finally { Remove-TestDir $dir }
}

Test-Case '/userinfo knows exactly the harness users, with a name and an email each' {
    $dir = New-TestDirPath
    try {
        New-TestAuth0 $dir | Out-Null
        $users = Get-Content -LiteralPath (Join-Path $dir 'users.json') -Raw | ConvertFrom-Json
        Assert-Equal 'auth0|cm-sec-editor,auth0|cm-sec-owner' ((@($users.PSObject.Properties.Name) | Sort-Object) -join ',')
        Assert-Equal 'Scan owner' $users.'auth0|cm-sec-owner'.name
        Assert-Equal 'editor@example.test' $users.'auth0|cm-sec-editor'.email
    }
    finally { Remove-TestDir $dir }
}

Test-Case 'The API is configured like production against the fake, with no setting that weakens TLS or issuer checks' {
    $vars = Get-FakeAuth0ApiEnvironment
    Assert-Equal 'fake-auth0:8443' $vars['Auth__Auth0__Domain']
    Assert-Equal 'urn:collectionmaxxing:api' $vars['Auth__Auth0__Audience']
    Assert-Equal '/fake-auth0-ca/ca.pem' $vars['SSL_CERT_FILE']
    Assert-True ($vars['Auth__PublicOrigin'] -like 'https://*') 'production wants an https public origin'
    foreach ($key in $vars.Keys) { Assert-True ($key -notmatch 'Insecure|DisableHttps|RequireHttps|ValidateIssuer|ValidateAudience|SkipCert|Unsafe|TLS|DANGEROUS') "a weakening setting: $key" }
}

Test-Case 'The fake runs on the labelled network with the alias the certificate names, hardened, before the API; the API gets only the CA and the fake settings' {
    $dir = New-TestDirPath
    Use-StackRecorder
    try { Start-SecStack -RunId 'r3' -Port 0 -ApiImage 'cm-sec-api:r3' -WorkDir $dir -ReadyAttempts 1 | Out-Null } catch { if ($_.Exception.Message -notmatch 'did not become ready') { throw } }
    finally { Remove-TestDir $dir }
    $runs = @($global:Calls | Where-Object { $_.Arguments[0] -eq 'run' })
    $fakeIndex = [array]::IndexOf(@($runs | ForEach-Object { $_.Display }), @($runs | Where-Object { $_.Display -match '--name cm-sec-auth0-r3' })[0].Display)
    $apiIndex = [array]::IndexOf(@($runs | ForEach-Object { $_.Display }), @($runs | Where-Object { $_.Display -match '--name cm-sec-api-r3' })[0].Display)
    Assert-True ($fakeIndex -ge 0 -and $fakeIndex -lt $apiIndex) 'the fake starts before the API'
    $fake = $runs[$fakeIndex]; $api = $runs[$apiIndex]
    Assert-True ($fake.Display -match '--network cm-sec-net-r3') 'isolated network'
    Assert-True ($fake.Display -match '--network-alias fake-auth0') 'the alias the certificate names'
    Assert-True ($fake.Display -match '--label cm\.agent=qa-1c') 'label'
    Assert-True ($fake.Display -match '--read-only' -and $fake.Display -match '--cap-drop ALL' -and $fake.Display -match '--user 65534') 'hardened'
    Assert-True ($fake.Display -notmatch ' -p |--publish') 'the fake publishes no port'
    Assert-True ($fake.Display -match 'python:3\.13-alpine python -u /fake/server\.py') 'a public image running our server.py'
    Assert-True ($api.Display -match '-e Auth__Auth0__Domain=fake-auth0:8443') 'the API names the fake as its tenant'
    Assert-True ($api.Display -match '-e SSL_CERT_FILE=/fake-auth0-ca/ca\.pem') 'the API trusts the per-run CA'
    Assert-True ($api.Display -match 'target=/fake-auth0-ca,readonly') 'the CA is mounted read-only'
    Assert-True ($api.Display -notmatch 'server\.key|/fake,|/secrets') 'the API gets no key and no secrets folder'
    Assert-True ($api.Display -match '-e ASPNETCORE_ENVIRONMENT=Production') 'production settings'
    Assert-True ($api.Display -notmatch 'setup-code|SetupCode|Passkey|OpenIddict|Auth__Keys') 'none of the removed sign-in settings'
}

Test-Case 'The web sign-in plays the browser: starts the login, answers the callback with a code for the owner and the nonce Auth0 was given, and keeps the cookies' {
    $global:SeenNav = [System.Collections.Generic.List[string]]::new()
    function Invoke-Navigation {
        param([string]$Base, [string]$Path, [hashtable]$Jar)
        $global:SeenNav.Add($Path)
        if ($Path -like '/account/login*') { $Jar['__Host-intent'] = 'i'; return [pscustomobject]@{ Status = 302; Location = 'https://fake-auth0:8443/authorize?client_id=c&state=STATE%2B1&nonce=NONCE-9&redirect_uri=x' } }
        $Jar['__Host-cm'] = 'session'
        [pscustomobject]@{ Status = 302; Location = '/' }
    }
    $jar = Invoke-WebSignIn -Base 'http://127.0.0.1:1' -Subject 'auth0|cm-sec-owner'
    Assert-Equal 2 $global:SeenNav.Count
    Assert-True ($global:SeenNav[1] -match '^/signin-oidc\?code=([^&]+)&state=STATE%2B1$') "the callback: $($global:SeenNav[1])"
    $code = [Uri]::UnescapeDataString($Matches[1])
    $grant = [Text.Encoding]::UTF8.GetString((ConvertFrom-TestBase64Url $code)) | ConvertFrom-Json
    Assert-Equal 'auth0|cm-sec-owner' $grant.sub
    Assert-Equal 'NONCE-9' $grant.nonce
    Assert-Equal 'session' $jar['__Host-cm']
}

Test-Case 'The web sign-in says what happened when the API sends the callback back to the login page' {
    function Invoke-Navigation {
        param([string]$Base, [string]$Path, [hashtable]$Jar)
        if ($Path -like '/account/login*') { return [pscustomobject]@{ Status = 302; Location = 'https://fake-auth0:8443/authorize?state=s&nonce=n' } }
        [pscustomobject]@{ Status = 302; Location = '/login?error=signin_failed' }
    }
    Assert-Throws { Invoke-WebSignIn -Base 'http://127.0.0.1:1' -Subject 'auth0|x' } 'callback answered HTTP 302 to ./login\?error=signin_failed'
    function Invoke-Navigation { param([string]$Base, [string]$Path, [hashtable]$Jar) [pscustomobject]@{ Status = 200; Location = $null } }
    Assert-Throws { Invoke-WebSignIn -Base 'http://127.0.0.1:1' -Subject 'auth0|x' } 'not a redirect to Auth0'
}

Test-Case 'The seed: the owner signs in on the web and makes the library and the invite; the editor comes in with a token, first call then accept; the editor is returned' {
    function Invoke-WebSignIn { param([string]$Base, [string]$Subject) $global:Calls.Add([pscustomobject]@{ Method = 'WEB'; Path = $Subject; Body = $null; Bearer = $null; Cookie = $null }); @{ '__Host-cm' = 'owner-session' } }
    function Invoke-Json {
        param([string]$Base, [string]$Method, [string]$Path, $Body, [string]$Bearer, [hashtable]$Jar = @{})
        $global:Calls.Add([pscustomobject]@{ Method = $Method; Path = $Path; Body = $Body; Bearer = $Bearer; Cookie = $Jar['__Host-cm'] })
        switch ("$Method $Path") {
            'GET /api/v1/me' { return [pscustomobject]@{ Status = 200; Json = [pscustomobject]@{} } }
            'POST /api/v1/libraries' { return [pscustomobject]@{ Status = 201; Json = [pscustomobject]@{ id = 'lib-1'; role = 'Owner' } } }
            'POST /api/v1/libraries/lib-1/invites' { return [pscustomobject]@{ Status = 201; Json = [pscustomobject]@{ code = 'ABCD-EFGH-JK'; link = 'x' } } }
            'POST /api/v1/invites:accept' { return [pscustomobject]@{ Status = 200; Json = [pscustomobject]@{ id = 'lib-1'; role = 'Editor' } } }
        }
        [pscustomobject]@{ Status = 404; Json = $null }
    }
    $dir = New-TestDirPath
    try {
        $auth0 = New-TestAuth0 $dir
        $seed = New-ThrowawayEditorToken -Base 'http://127.0.0.1:1' -Auth0 $auth0
    }
    finally { Remove-TestDir $dir }
    Assert-Equal 'WEB auth0|cm-sec-owner|POST /api/v1/libraries|POST /api/v1/libraries/lib-1/invites|GET /api/v1/me|POST /api/v1/invites:accept' (($global:Calls | ForEach-Object { "$($_.Method) $($_.Path)" }) -join '|')
    Assert-Equal 'owner-session,owner-session' (($global:Calls[1], $global:Calls[2] | ForEach-Object { $_.Cookie }) -join ',')
    Assert-True (-not $global:Calls[1].Bearer -and -not $global:Calls[2].Bearer) 'the owner uses the web session, not a token'
    Assert-Equal 'auth0|cm-sec-editor,auth0|cm-sec-editor' ((ConvertFrom-TestJwt $global:Calls[3].Bearer).Payload.sub + ',' + (ConvertFrom-TestJwt $global:Calls[4].Bearer).Payload.sub)
    Assert-Equal 'ABCD-EFGH-JK' $global:Calls[4].Body.invite
    Assert-Equal 'editor' $global:Calls[2].Body.role
    Assert-Equal 'lib-1' $seed.LibraryId
    Assert-Equal 'auth0|cm-sec-editor' (ConvertFrom-TestJwt $seed.Token).Payload.sub
}

Test-Case 'The seed stops with a clear message when the invite is not accepted, and says nothing of the token' {
    function Invoke-WebSignIn { param([string]$Base, [string]$Subject) @{ '__Host-cm' = 'owner-session' } }
    function Invoke-Json {
        param([string]$Base, [string]$Method, [string]$Path, $Body, [string]$Bearer, [hashtable]$Jar = @{})
        switch ("$Method $Path") {
            'GET /api/v1/me' { return [pscustomobject]@{ Status = 200; Json = $null } }
            'POST /api/v1/libraries' { return [pscustomobject]@{ Status = 201; Json = [pscustomobject]@{ id = 'lib-1' } } }
            'POST /api/v1/libraries/lib-1/invites' { return [pscustomobject]@{ Status = 201; Json = [pscustomobject]@{ code = 'ABCD-EFGH-JK' } } }
        }
        [pscustomobject]@{ Status = 404; Json = [pscustomobject]@{ code = 'invite.invalid' } }
    }
    $dir = New-TestDirPath
    try {
        $auth0 = New-TestAuth0 $dir
        $text = $null
        try { New-ThrowawayEditorToken -Base 'http://127.0.0.1:1' -Auth0 $auth0 | Out-Null } catch { $text = $_.Exception.Message }
    }
    finally { Remove-TestDir $dir }
    Assert-True ($text -match 'Accepting the invite failed \(HTTP 404: invite\.invalid') "got: $text"
    Assert-True ($text -notmatch 'eyJ') 'no token in the message'
}

$python = @('python3', 'python') | ForEach-Object { Get-Command $_ -ErrorAction SilentlyContinue } | Where-Object { $_.Source -notmatch 'WindowsApps' } | Select-Object -First 1
if ($python) {
    Test-Case 'The fake''s own RS256 signing (plain integer maths) makes ID tokens that verify with the published key' {
        $dir = New-TestDirPath
        try {
            New-TestAuth0 $dir | Out-Null
            $script = "import sys, json; sys.path.insert(0, sys.argv[1]); import server; server.ROOT = sys.argv[1]; print(server.make_id_token('auth0|cm-sec-owner', 'NONCE-1', {'name': 'Scan owner', 'email': 'owner@example.test'}))"
            $token = (& $python.Source -I -c $script $dir) -join ''
            Assert-True ($LASTEXITCODE -eq 0 -and $token -like 'eyJ*') "the fake could not make an ID token: $token"
            $jwt = ConvertFrom-TestJwt $token
            Assert-Equal 'NONCE-1' $jwt.Payload.nonce
            Assert-Equal 'cm-fake-web-client' $jwt.Payload.aud
            Assert-Equal 'https://fake-auth0:8443/' $jwt.Payload.iss
            Assert-Equal 'owner@example.test' $jwt.Payload.email
            Assert-True ($jwt.Payload.auth_time -ge [DateTimeOffset]::UtcNow.ToUnixTimeSeconds() - 5) 'auth_time is now, so the sign-in counts as recent'
            $jwks = Get-Content -LiteralPath (Join-Path $dir 'jwks.json') -Raw | ConvertFrom-Json
            $rsa = [System.Security.Cryptography.RSA]::Create()
            $rsa.ImportParameters([System.Security.Cryptography.RSAParameters]@{ Modulus = (ConvertFrom-TestBase64Url $jwks.keys[0].n); Exponent = (ConvertFrom-TestBase64Url $jwks.keys[0].e) })
            Assert-Equal $jwks.keys[0].kid $jwt.Header.kid
            Assert-True ($rsa.VerifyData($jwt.SigningInput, $jwt.Signature, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)) 'the ID token verifies with the JWKS key'
        }
        finally { Remove-TestDir $dir }
    }
}

Test-Case 'The ZAP hook still puts the bearer token on every request and the run passes the editor token to ZAP' {
    $hook = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'zap-hooks.py') -Raw
    Assert-True ($hook -match "CM_ZAP_TOKEN" -and $hook -match "'Bearer ' \+ token") 'the hook sets Authorization: Bearer from CM_ZAP_TOKEN'
    $zap = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Security.Zap.ps1') -Raw
    Assert-True ($zap -match 'New-ThrowawayEditorToken[^\r\n]*-Auth0 \$stack\.Auth0') 'the API scan seeds through the fake Auth0'
    Assert-True ($zap -match '-Token \$token') 'the token goes to the scan'
}

# ---- summary ---------------------------------------------------------------------------------------------------------------------------

foreach ($f in $failures) { Write-Host $f }
$seconds = [math]::Round($timer.Elapsed.TotalSeconds, 1)
if ($failures.Count -eq 0) { Write-Host "security script tests: $passed passed, 0 failed (${seconds}s)"; exit 0 }
Write-Host "security script tests: $passed passed, $($failures.Count) FAILED (${seconds}s)"
exit 1
