<#
.SYNOPSIS
  Dependency audit: `dotnet list package --vulnerable --include-transitive` and `npm audit --audit-level=high`. Counts only.

.DESCRIPTION
  Needs no Docker. Reads the lock files; installs nothing and changes no file. Raw output goes to the temp folder.
  A finding of any severity in a project that ships (src/**) fails the run, and so does a HIGH or CRITICAL npm advisory.
  A .NET finding that is only in a test project (tests/**) or the Aspire AppHost (a local dev tool) is counted and listed as
  "test only" but does not fail the run: neither is ever published. The decision for each one is recorded in docs/security/asvs-l2-checklist.md (V14).

  -DryRun  print the plan and run nothing.
#>
[CmdletBinding()]
param([switch]$DryRun, [string]$ReportDir)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Security.Common.ps1')

try {
    $runId = New-RunId
    $reports = Get-ReportDir $runId $ReportDir
    if ($DryRun) {
        Write-Sec 'plan  dotnet list CollectionMaxxing.slnx package --vulnerable --include-transitive'
        Write-Sec '      npm audit --audit-level=high --package-lock-only (in app/)'
        Write-Sec "      reports $reports"
        Write-Sec 'dry run: nothing was run'
        exit 0
    }
    New-Item -ItemType Directory -Force -Path $reports | Out-Null
    Push-Location $script:RepoRoot
    try {
        $net = Invoke-Sec dotnet @('list', 'CollectionMaxxing.slnx', 'package', '--vulnerable', '--include-transitive')
        Set-Content -LiteralPath (Join-Path $reports 'dotnet-vulnerable.txt') -Value $net.Output
        $found = @(ConvertFrom-DotnetVulnerable $net.Output)
        $shipping = @($found | Where-Object { -not $_.TestOnly })
        $testOnly = @($found | Where-Object { $_.TestOnly })
        Write-Sec ("dotnet: shipping projects {0}; test projects only {1}" -f $shipping.Count, (($testOnly | Select-Object -Unique Package, Resolved, Severity | ForEach-Object { "$($_.Package) $($_.Resolved) $($_.Severity)" }) -join '; '))

        $npmCommand = if ($IsWindows) { 'npm.cmd' } else { 'npm' }
        $npm = Invoke-Sec $npmCommand @('audit', '--json', '--package-lock-only', '--prefix', 'app')
        Set-Content -LiteralPath (Join-Path $reports 'npm-audit.json') -Value $npm.Output
        $audit = $null
        try { $audit = $npm.Output -join "`n" | ConvertFrom-Json -Depth 30 } catch { }
        if ($null -eq $audit -or $null -eq $audit.metadata) { Stop-SecFailure "npm audit gave no result (offline?); see $reports" }
        $meta = $audit.metadata.vulnerabilities
        # The counts above count every package on a chain; the advisories themselves are the objects in "via".
        $all = foreach ($p in $audit.vulnerabilities.PSObject.Properties) {
            foreach ($v in @($p.Value.via)) { if ($v -isnot [string]) { [pscustomobject]@{ Package = $p.Name; Severity = $v.severity; Id = ($v.url -replace '^.*/', ''); Title = $v.title } } }
        }
        $advisories = @($all | Sort-Object Id -Unique)
        $accepted = @(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'accepted-advisories.txt') -ErrorAction SilentlyContinue | ForEach-Object { if ($_ -match '^(GHSA-[0-9a-z-]+)\s') { $Matches[1] } })
        $open = @($advisories | Where-Object { $_.Severity -in 'high', 'critical' -and $_.Id -notin $accepted })
        Write-Sec "npm: packages on a vulnerable chain critical=$($meta.critical) high=$($meta.high) moderate=$($meta.moderate) low=$($meta.low); advisories $($advisories.Count) (high or critical: $(@($advisories | Where-Object { $_.Severity -in 'high', 'critical' }).Count), accepted in accepted-advisories.txt: $(@($advisories | Where-Object { $_.Id -in $accepted }).Count))"
        foreach ($a in $open) { Write-Sec "      npm advisory: $($a.Id) $($a.Package) $($a.Severity): $($a.Title)" }

        $bad = $shipping.Count + $open.Count
        if ($bad -gt 0) {
            foreach ($f in $shipping) { Write-Sec "      shipping: $($f.Package) $($f.Resolved) $($f.Severity) in $($f.Project)" }
            Write-Sec "FAIL $bad finding(s) in shipping code (raw output: $reports)"
            exit 1
        }
        Write-Sec "ok   dependency audit (raw output: $reports)"
        exit 0
    }
    finally { Pop-Location }
}
catch { Write-Host "FAIL $($_.Exception.Message)"; exit 1 }
