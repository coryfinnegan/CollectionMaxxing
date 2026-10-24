# Runs one ZAP scan (baseline or API) as a container on the throwaway stack's network. Dot-sourced after Security.Common.ps1.
# The bearer token (API scan only) goes into ZAP through CM_ZAP_TOKEN (read by zap-hooks.py), taken from this process's environment by
# `docker create -e NAME`: it is never on a command line, in a file or in a log.

function Get-ZapArguments {
    <# The ZAP command line (pure, so a test can read it). The limits come first: threads per host and the wall-clock cap. #>
    param([ValidateSet('baseline', 'api')][string]$Kind, [string]$Target, [int]$Threads, [int]$MaxMinutes)
    $options = "-config scanner.threadPerHost=$Threads -config scanner.hostPerScan=1 -config spider.thread=$Threads -config scanner.maxScanDurationInMins=$MaxMinutes -config spider.maxDuration=$([math]::Max(1, [math]::Min(5, $MaxMinutes)))"
    if ($Kind -eq 'baseline') {
        return @('zap-baseline.py', '-t', $Target, '-J', 'report.json', '-c', 'rules.tsv', '-m', "$([math]::Max(1, [math]::Min(5, $MaxMinutes)))", '-T', "$MaxMinutes", '-z', $options)
    }
    @('zap-api-scan.py', '-t', '/zap/wrk/openapi.json', '-f', 'openapi', '-O', $Target, '-J', 'report.json', '-c', 'rules.tsv', '-T', "$MaxMinutes", '--hook', '/zap/wrk/zap-hooks.py', '-z', $options)
}

function Get-ZapCounts {
    <# Alerts by level from a ZAP JSON report: riskcode 3 High, 2 Medium, 1 Low, 0 Informational. #>
    param([string]$ReportPath)
    $counts = [ordered]@{ High = 0; Medium = 0; Low = 0; Informational = 0 }
    $names = [System.Collections.Generic.List[string]]::new()
    if (-not (Test-Path -LiteralPath $ReportPath)) { return [pscustomobject]@{ Counts = $counts; Names = @(); Found = $false } }
    $report = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json -Depth 30
    foreach ($site in @($report.site)) {
        foreach ($a in @($site.alerts)) {
            switch ([int]$a.riskcode) { 3 { $counts.High++ } 2 { $counts.Medium++ } 1 { $counts.Low++ } default { $counts.Informational++ } }
            $level = ($a.riskdesc -replace ' \(.*', '')
            $names.Add("$($a.pluginid) $($a.name) [$level]")
            # Where it was found (first three; evidence cut short) so a new alert can be triaged from the log alone.
            foreach ($i in @($a.instances | Select-Object -First 3)) {
                $evidence = "$($i.evidence)"; if ($evidence.Length -gt 60) { $evidence = $evidence.Substring(0, 60) }
                $names.Add("  at $($i.uri) $evidence")
            }
        }
    }
    [pscustomobject]@{ Counts = $counts; Names = @($names | Select-Object -Unique); Found = $true }
}

function New-ScanSpec {
    <#
      Copies contracts/openapi.json with a usable example on every uuid path parameter: the throwaway library's real id for
      {libraryId}, a made-up one for the rest. Without it ZAP sends "id" or "libraryId", no route matches, and the whole scan
      only ever sees the 401 of the fallback. The copy goes to the temp report folder; the contract in the repo is untouched.
    #>
    param([string]$Source, [string]$Destination, [string]$LibraryId)
    $spec = Get-Content -LiteralPath $Source -Raw | ConvertFrom-Json -Depth 100
    $other = [guid]::NewGuid().ToString()
    foreach ($path in $spec.paths.PSObject.Properties) {
        foreach ($op in $path.Value.PSObject.Properties) {
            foreach ($p in @($op.Value.parameters)) {
                if ($p -and $p.in -eq 'path' -and $p.schema.format -eq 'uuid') {
                    $value = if ($p.name -eq 'libraryId' -and $LibraryId) { $LibraryId } else { $other }
                    $p | Add-Member -NotePropertyName example -NotePropertyValue $value -Force
                }
            }
        }
    }
    $spec | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $Destination
}

function Invoke-ZapScan {
    param(
        [ValidateSet('baseline', 'api')][string]$Kind, [string]$RunId, [string]$Network, [string]$Target,
        [int]$Threads, [int]$MaxMinutes, [string]$ReportDir, [string]$Token, [string]$LibraryId
    )
    $name = "$($script:NamePrefix)-zap-$Kind-$RunId"
    $rules = Join-Path $script:RepoRoot '.zap/rules.tsv'
    New-Item -ItemType Directory -Force -Path $ReportDir | Out-Null
    Open-SecFolderForContainer $ReportDir 'ZAP'   # ZAP (uid 1000) must be able to write report.json on Linux
    $createEnv = @{}
    $envArgs = @()
    if ($Token) { $createEnv['CM_ZAP_TOKEN'] = $Token; $envArgs = @('-e', 'CM_ZAP_TOKEN') }
    $cmd = Get-ZapArguments -Kind $Kind -Target $Target -Threads $Threads -MaxMinutes $MaxMinutes
    # ZAP insists that /zap/wrk is a mount. It is the report folder in the temp directory (a bind mount, not a volume), so the
    # rules and the OpenAPI document go in there and the report comes out there.
    Copy-Item -LiteralPath $rules -Destination (Join-Path $ReportDir 'rules.tsv') -Force
    if ($Kind -eq 'api') { Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'zap-hooks.py') -Destination (Join-Path $ReportDir 'zap-hooks.py') -Force; New-ScanSpec (Join-Path $script:RepoRoot 'contracts/openapi.json') (Join-Path $ReportDir 'openapi.json') $LibraryId }
    $r = Invoke-Sec docker (@('create', '--name', $name) + (Get-LabelArgs) + @('--network', $Network, '--security-opt', 'no-new-privileges:true', '--cpus', '2', '--memory', '2g', '-v', "${ReportDir}:/zap/wrk") + $envArgs + @($script:ZapImage) + $cmd) $createEnv
    if ($r.ExitCode -ne 0) { Stop-SecFailure "Could not create the ZAP container: $($r.Output -join ' ')" }
    $r = Invoke-Sec docker @('start', $name)
    if ($r.ExitCode -ne 0) { Stop-SecFailure "Could not start ZAP: $($r.Output -join ' ')" }
    # The scan has its own -T limit; this is the outer wall-clock cap (the scan's limit plus two minutes for start-up and the report).
    $done = Wait-SecContainer -Name $name -MaxMinutes ($MaxMinutes + 2)
    if (Test-Path -LiteralPath (Join-Path $ReportDir 'report.json')) { Move-Item -LiteralPath (Join-Path $ReportDir 'report.json') -Destination (Join-Path $ReportDir "zap-$Kind.json") -Force }
    $log = (Invoke-Sec docker @('logs', $name)).Output
    Set-Content -LiteralPath (Join-Path $ReportDir "zap-$Kind.log") -Value $log
    Invoke-Sec docker @('rm', '-f', $name) | Out-Null
    $summary = @($log | Where-Object { $_ -match '^(FAIL-NEW|WARN-NEW|PASS|WARN-INPROG|FAIL-INPROG)' } | Select-Object -Last 3)
    $counts = Get-ZapCounts (Join-Path $ReportDir "zap-$Kind.json")
    [pscustomobject]@{ ExitCode = $done.ExitCode; TimedOut = $done.TimedOut; Counts = $counts.Counts; Names = $counts.Names; ReportFound = $counts.Found; Summary = $summary }
}

function Invoke-ZapRun {
    <# The whole run for one scan kind: guards, plan, stack, scan, tear-down. Returns the script's exit code. #>
    param([ValidateSet('baseline', 'api')][string]$Kind, [switch]$DryRun, [int]$Port, [int]$Threads, [int]$MaxMinutes, [string]$ReportDir, [string]$WebDist, [switch]$KeepStack, [string]$StartPath = '')
    $exit = 1
    $runId = $null; $work = $null
    try {
        Assert-PortAllowed $Port
        Test-ZapLimits $Threads $MaxMinutes
        $runId = New-RunId
        $reports = Get-ReportDir $runId $ReportDir
        $plan = Get-SecPlan -Mode $(if ($Kind -eq 'api') { 'zap-api' } else { 'zap-baseline' }) -RunId $runId -Threads $Threads -MaxMinutes $MaxMinutes -Port $Port -ReportDir $reports
        if ($DryRun) { Write-SecPlan $plan -Dry; return 0 }
        Assert-DockerUp
        Write-SecPlan $plan
        $work = Join-Path ([IO.Path]::GetTempPath()) "cm-sec-work-$runId"
        Assert-OutsideRepo $work
        New-Item -ItemType Directory -Force -Path $work | Out-Null
        $image = "$($script:NamePrefix)-api:$runId"
        Build-ApiImage $image $WebDist
        $stack = Start-SecStack -RunId $runId -Port $Port -ApiImage $image -WorkDir $work
        $token = $null
        if ($Kind -eq 'api') {
            $editor = New-ThrowawayEditorToken -Base "http://127.0.0.1:$($stack.HostPort)" -Auth0 $stack.Auth0
            $token = $editor.Token; $libraryId = $editor.LibraryId
            Write-Sec 'ok   throwaway editor signed in (token kept in memory only)'
        }
        $result = Invoke-ZapScan -Kind $Kind -RunId $runId -Network $stack.Network -Target "http://api:5480$StartPath" -Threads $Threads -MaxMinutes $MaxMinutes -ReportDir $reports -Token $token -LibraryId $libraryId
        if (Write-ZapResult $Kind $result $reports) { $exit = 0 }
    }
    catch {
        Write-Host "FAIL $($_.Exception.Message)"
        if ($env:CM_SEC_DEBUG) { Write-Host $_.ScriptStackTrace }
    }
    finally {
        if (-not $DryRun -and -not $KeepStack -and $runId) {
            Remove-SecResources | Out-Null
            if ($work -and (Test-Path -LiteralPath $work)) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }
    $exit
}

function Write-ZapResult {
    param([string]$Kind, $Result, [string]$ReportDir)
    foreach ($line in $Result.Summary) { Write-Sec "      zap: $line" }
    if (-not $Result.ReportFound) { Write-Sec "FAIL zap $Kind produced no report (exit $($Result.ExitCode)); see $ReportDir. Exit 2 only means warnings, so the report folder was probably not writable for ZAP."; return $false }
    Write-Tally "zap $Kind alerts by level" @{ High = $Result.Counts.High; Medium = $Result.Counts.Medium; Low = $Result.Counts.Low; Informational = $Result.Counts.Informational }
    foreach ($n in $Result.Names) { Write-Sec "      alert $n" }
    if ($Result.TimedOut) { Write-Sec "      the wall-clock cap stopped the scan early; the report is partial" }
    # zap-*.py exit codes: 0 pass, 1 a FAIL alert, 2 only WARN alerts, 3 something else went wrong.
    $ok = $Result.ExitCode -eq 0 -and -not $Result.TimedOut   # exit 2 is an unexplained WARN: fix it or explain it in .zap/rules.tsv
    Write-Sec ($(if ($ok) { 'ok   ' } else { 'FAIL ' }) + "zap $Kind (exit $($Result.ExitCode); raw reports: $ReportDir)")
    $ok
}
