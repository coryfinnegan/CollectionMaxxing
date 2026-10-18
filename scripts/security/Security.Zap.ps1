# Runs one ZAP scan (baseline or API) as a container on the throwaway stack's network. Dot-sourced after Security.Common.ps1.
# The bearer token (API scan only) goes into ZAP through ZAP_AUTH_HEADER_VALUE, taken from this process's environment by
# `docker create -e NAME`: it is never on a command line, in a file or in a log.

function Get-ZapArguments {
    <# The ZAP command line (pure, so a test can read it). The limits come first: threads per host and the wall-clock cap. #>
    param([ValidateSet('baseline', 'api')][string]$Kind, [string]$Target, [int]$Threads, [int]$MaxMinutes)
    $options = "-config scanner.threadPerHost=$Threads -config scanner.hostPerScan=1 -config spider.thread=$Threads -config scanner.maxScanDurationInMins=$MaxMinutes -config spider.maxDuration=$([math]::Max(1, [math]::Min(5, $MaxMinutes)))"
    if ($Kind -eq 'baseline') {
        return @('zap-baseline.py', '-t', $Target, '-J', 'report.json', '-c', 'rules.tsv', '-m', "$([math]::Max(1, [math]::Min(5, $MaxMinutes)))", '-T', "$MaxMinutes", '-z', $options)
    }
    @('zap-api-scan.py', '-t', '/zap/wrk/openapi.json', '-f', 'openapi', '-O', $Target, '-J', 'report.json', '-c', 'rules.tsv', '-T', "$MaxMinutes", '-z', $options)
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
        }
    }
    [pscustomobject]@{ Counts = $counts; Names = @($names | Select-Object -Unique); Found = $true }
}

function Invoke-ZapScan {
    param(
        [ValidateSet('baseline', 'api')][string]$Kind, [string]$RunId, [string]$Network, [string]$Target,
        [int]$Threads, [int]$MaxMinutes, [string]$ReportDir, [string]$Token
    )
    $name = "$($script:NamePrefix)-zap-$Kind-$RunId"
    $rules = Join-Path $script:RepoRoot '.zap/rules.tsv'
    New-Item -ItemType Directory -Force -Path $ReportDir | Out-Null
    $createEnv = @{}
    $envArgs = @()
    if ($Token) { $createEnv['ZAP_AUTH_HEADER'] = 'Authorization'; $createEnv['ZAP_AUTH_HEADER_VALUE'] = "Bearer $Token"; $envArgs = @('-e', 'ZAP_AUTH_HEADER', '-e', 'ZAP_AUTH_HEADER_VALUE') }
    $cmd = Get-ZapArguments -Kind $Kind -Target $Target -Threads $Threads -MaxMinutes $MaxMinutes
    $r = Invoke-Sec docker (@('create', '--name', $name) + (Get-LabelArgs) + @('--network', $Network, '--security-opt', 'no-new-privileges:true', '--cpus', '2', '--memory', '2g') + $envArgs + @($script:ZapImage) + $cmd) $createEnv
    if ($r.ExitCode -ne 0) { Stop-SecFailure "Could not create the ZAP container: $($r.Output -join ' ')" }
    Invoke-Sec docker @('cp', $rules, "${name}:/zap/wrk/rules.tsv") | Out-Null
    if ($Kind -eq 'api') { Invoke-Sec docker @('cp', (Join-Path $script:RepoRoot 'contracts/openapi.json'), "${name}:/zap/wrk/openapi.json") | Out-Null }
    $r = Invoke-Sec docker @('start', $name)
    if ($r.ExitCode -ne 0) { Stop-SecFailure "Could not start ZAP: $($r.Output -join ' ')" }
    # The scan has its own -T limit; this is the outer wall-clock cap (the scan's limit plus two minutes for start-up and the report).
    $done = Wait-SecContainer -Name $name -MaxMinutes ($MaxMinutes + 2)
    Invoke-Sec docker @('cp', "${name}:/zap/wrk/report.json", (Join-Path $ReportDir "zap-$Kind.json")) | Out-Null
    $log = (Invoke-Sec docker @('logs', $name)).Output
    Set-Content -LiteralPath (Join-Path $ReportDir "zap-$Kind.log") -Value $log
    Invoke-Sec docker @('rm', '-f', $name) | Out-Null
    $summary = @($log | Where-Object { $_ -match '^(FAIL-NEW|WARN-NEW|PASS|WARN-INPROG|FAIL-INPROG)' } | Select-Object -Last 3)
    $counts = Get-ZapCounts (Join-Path $ReportDir "zap-$Kind.json")
    [pscustomobject]@{ ExitCode = $done.ExitCode; TimedOut = $done.TimedOut; Counts = $counts.Counts; Names = $counts.Names; ReportFound = $counts.Found; Summary = $summary }
}

function Invoke-ZapRun {
    <# The whole run for one scan kind: guards, plan, stack, scan, tear-down. Returns the script's exit code. #>
    param([ValidateSet('baseline', 'api')][string]$Kind, [switch]$DryRun, [int]$Port, [int]$Threads, [int]$MaxMinutes, [string]$ReportDir, [string]$WebDist, [switch]$KeepStack)
    $exit = 1
    $runId = $null; $work = $null
    try {
        Assert-PortAllowed $Port
        Test-ZapLimits $Threads $MaxMinutes
        $runId = New-RunId
        $reports = Get-ReportDir $runId $ReportDir
        $plan = Get-SecPlan -Mode $(if ($Kind -eq 'api') { 'zap-api' } else { 'zap-baseline' }) -RunId $runId -Threads $Threads -MaxMinutes $MaxMinutes -Port $Port -ReportDir $reports
        if ($DryRun) { Write-SecPlan $plan; return 0 }
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
            $token = New-ThrowawayEditorToken -Base "http://127.0.0.1:$($stack.HostPort)" -SecretsDir $stack.Secrets
            Write-Sec 'ok   throwaway editor signed in (token kept in memory only)'
        }
        $result = Invoke-ZapScan -Kind $Kind -RunId $runId -Network $stack.Network -Target 'http://api:5480' -Threads $Threads -MaxMinutes $MaxMinutes -ReportDir $reports -Token $token
        if (Write-ZapResult $Kind $result $reports) { $exit = 0 }
    }
    catch { Write-Host "FAIL $($_.Exception.Message)" }
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
    if (-not $Result.ReportFound) { Write-Sec "FAIL zap $Kind produced no report (exit $($Result.ExitCode)); see $ReportDir"; return $false }
    Write-Tally "zap $Kind alerts by level" @{ High = $Result.Counts.High; Medium = $Result.Counts.Medium; Low = $Result.Counts.Low; Informational = $Result.Counts.Informational }
    foreach ($n in $Result.Names) { Write-Sec "      alert $n" }
    if ($Result.TimedOut) { Write-Sec "      the wall-clock cap stopped the scan early; the report is partial" }
    # zap-*.py exit codes: 0 pass, 1 a FAIL alert, 2 only WARN alerts, 3 something else went wrong.
    $ok = $Result.ExitCode -in 0, 2 -and -not $Result.TimedOut
    Write-Sec ($(if ($ok) { 'ok   ' } else { 'FAIL ' }) + "zap $Kind (exit $($Result.ExitCode); raw reports: $ReportDir)")
    $ok
}
