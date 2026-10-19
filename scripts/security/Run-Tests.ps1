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

Test-Case 'On Linux the API secrets folder is opened for the API user (it writes the setup code there); on Windows nothing is changed' {
    foreach ($unix in $true, $false) {
        $global:Calls.Clear()
        $global:SecIsUnix = $unix
        $dir = Join-Path ([IO.Path]::GetTempPath()) "cm-sec-test-$([guid]::NewGuid().ToString('N'))"
        Use-Recorder {
            param($c, $a)
            if ($c -eq 'dotnet') { Set-Content -LiteralPath $a[$a.IndexOf('--output') + 1] -Value 'select 1;' }
            if ($a[0] -eq 'port') { return [pscustomobject]@{ ExitCode = 0; Output = @('127.0.0.1:49321') } }
            [pscustomobject]@{ ExitCode = 0; Output = @() }
        }
        try { try { Start-SecStack -RunId 'r4' -Port 0 -ApiImage 'cm-sec-api:r4' -WorkDir $dir -ReadyAttempts 1 | Out-Null } catch { if ($_.Exception.Message -notmatch 'did not become ready') { throw } } }
        finally { $global:SecIsUnix = $null; Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
        $order = @($global:Calls | ForEach-Object { $_.Command + ' ' + $_.Arguments[0] })
        $chmod = @($global:Calls | Where-Object { $_.Command -eq 'chmod' })
        if ($unix) {
            Assert-Equal 1 $chmod.Count 'chmod calls on unix'
            Assert-True ($chmod[0].Arguments[2] -like '*secrets') 'the secrets folder'
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

# ---- the software passkey and the throwaway stack -----------------------------------------------------------------------------------------

Test-Case 'The software passkey makes a "none" attestation the server can read' {
    $auth = New-SoftwareAuthenticator
    $options = [pscustomobject]@{ challenge = (ConvertTo-Base64Url ([byte[]](1..32))); user = [pscustomobject]@{ id = (ConvertTo-Base64Url ([byte[]](9, 9, 9))) } }
    $cred = New-PasskeyCredential $auth $options
    Assert-Equal 'public-key' $cred.type
    $att = ConvertFrom-Base64Url $cred.response.attestationObject
    Assert-Equal 0xA3 $att[0]
    $client = [Text.Encoding]::UTF8.GetString((ConvertFrom-Base64Url $cred.response.clientDataJSON)) | ConvertFrom-Json
    Assert-Equal 'webauthn.create' $client.type
    Assert-Equal 'https://localhost' $client.origin
    Assert-Equal $options.challenge $client.challenge
    Assert-Equal $options.user.id $auth.UserHandle
}

Test-Case 'Only the API is published, only on loopback; Postgres is on tmpfs with no volume; the label is on everything' {
    $dir = Join-Path ([IO.Path]::GetTempPath()) "cm-sec-test-$([guid]::NewGuid().ToString('N'))"
    Use-Recorder {
        param($c, $a)
        if ($c -eq 'dotnet') { Set-Content -LiteralPath $a[$a.IndexOf('--output') + 1] -Value 'select 1;' }
        if ($a[0] -eq 'port') { return [pscustomobject]@{ ExitCode = 0; Output = @('127.0.0.1:49321') } }
        [pscustomobject]@{ ExitCode = 0; Output = @() }
    }
    try {
        try { Start-SecStack -RunId 'r5' -Port 0 -ApiImage 'cm-sec-api:r5' -WorkDir $dir -ReadyAttempts 1 | Out-Null } catch { if ($_.Exception.Message -notmatch 'did not become ready') { throw } }
    }
    finally { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue }
    $runs = @($global:Calls | Where-Object { $_.Arguments[0] -eq 'run' })
    Assert-Equal 2 $runs.Count
    $pg = $runs | Where-Object { $_.Display -match '--name cm-sec-pg-r5' }
    Assert-True ($pg.Display -match '--tmpfs /var/lib/postgresql/data') 'tmpfs'
    Assert-True ($pg.Display -notmatch ' -v ' -and $pg.Display -notmatch ' -p ') 'no volume, no port'
    $api = $runs | Where-Object { $_.Display -match '--name cm-sec-api-r5' }
    Assert-True ($api.Display -match ' -p 127\.0\.0\.1::5480') 'loopback random port only'
    foreach ($r in $runs) { Assert-True ($r.Display -match '--label cm\.agent=qa-1c') "label missing: $($r.Display)" }
    Assert-True (@($global:Calls | Where-Object { $_.Arguments[0] -eq 'network' -and $_.Display -notmatch '--label cm\.agent=qa-1c' }).Count -eq 0) 'network label'
}

# ---- summary ---------------------------------------------------------------------------------------------------------------------------

foreach ($f in $failures) { Write-Host $f }
$seconds = [math]::Round($timer.Elapsed.TotalSeconds, 1)
if ($failures.Count -eq 0) { Write-Host "security script tests: $passed passed, 0 failed (${seconds}s)"; exit 0 }
Write-Host "security script tests: $passed passed, $($failures.Count) FAILED (${seconds}s)"
exit 1
