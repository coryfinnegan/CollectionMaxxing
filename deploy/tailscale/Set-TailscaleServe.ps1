<#
.SYNOPSIS
  Makes https://pc.tailnet.ts.net (this PC's own name) serve 127.0.0.1:5480 and nothing else. OWNER CHECKPOINT: nobody has run this yet.

.DESCRIPTION
  Without -Apply (or with -WhatIf) it reads `tailscale serve status`, says whether it is safe to proceed and prints the
  exact command it would run. With -Apply it runs that command only when Tailscale Serve has no configuration yet or
  already has exactly ours. It never overwrites a configuration it did not create, and refuses any with Funnel (public
  internet) turned on. Undo: tailscale serve --https=443 off
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [int]$Port = 5480,
    [int]$HttpsPort = 443,
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../deployer/Cm.Common.ps1')

function Get-CmServeVerdict {
    <# 'free' (no config), 'ours' (exactly our one proxy), or 'foreign'. #>
    param([string]$StatusJson, [int]$Port, [int]$HttpsPort)
    if ([string]::IsNullOrWhiteSpace($StatusJson)) { return 'free' }
    try { $status = $StatusJson | ConvertFrom-Json -AsHashtable } catch { return 'foreign' }
    if ($null -eq $status -or $status.Count -eq 0) { return 'free' }
    $allowed = @('TCP', 'Web', 'AllowFunnel')
    if (@($status.Keys | Where-Object { $_ -notin $allowed }).Count -gt 0) { return 'foreign' }
    if ($status.ContainsKey('AllowFunnel') -and @($status['AllowFunnel'].Values | Where-Object { $_ }).Count -gt 0) { return 'foreign' }
    if (-not $status.ContainsKey('Web') -or $status['Web'].Count -ne 1) { return 'foreign' }
    if ($status.ContainsKey('TCP') -and (@($status['TCP'].Keys).Count -ne 1 -or "$HttpsPort" -notin @($status['TCP'].Keys))) { return 'foreign' }
    $site = $status['Web'].GetEnumerator() | Select-Object -First 1
    if (-not "$($site.Key)".EndsWith(":$HttpsPort")) { return 'foreign' }
    $handlers = $site.Value['Handlers']
    if ($null -eq $handlers -or $handlers.Count -ne 1 -or -not $handlers.ContainsKey('/')) { return 'foreign' }
    if ($handlers['/']['Proxy'] -ne "http://127.0.0.1:$Port") { return 'foreign' }
    'ours'
}

$doApply = $Apply.IsPresent -and -not $WhatIfPreference
Initialize-Cm -Apply $doApply

$portProblem = Test-CmPort -Name 'Port' -Value $Port
if ($portProblem) { Write-CmLog "refusing: $portProblem" 'ERROR'; exit 2 }
if ($HttpsPort -lt 1 -or $HttpsPort -gt 65535) { Write-CmLog "refusing: HttpsPort $HttpsPort is not a port" 'ERROR'; exit 2 }

$serve = @('serve', '--bg', "--https=$HttpsPort", "http://127.0.0.1:$Port")
$status = Invoke-Cm -Command 'tailscale' -Arguments @('serve', 'status', '--json')
if ($status.ExitCode -ne 0) { Write-CmLog 'could not read `tailscale serve status` (is Tailscale running and signed in?)' 'ERROR'; exit 3 }
$verdict = Get-CmServeVerdict -StatusJson ($status.Output -join "`n") -Port $Port -HttpsPort $HttpsPort

switch ($verdict) {
    'foreign' {
        Write-CmLog 'Tailscale Serve already has a configuration that is not ours; not touching it. Look at: tailscale serve status' 'ERROR'
        exit 4
    }
    'ours' {
        Write-CmLog "already configured: HTTPS $HttpsPort proxies to 127.0.0.1:$Port. Nothing to do."
        exit 0
    }
}
Write-CmLog "Tailscale Serve is empty. The command is: $(Format-CmCommand 'tailscale' $serve)"
$r = Invoke-Cm -Mutating -Command 'tailscale' -Arguments $serve
if ($r.ExitCode -ne 0) { Write-CmLog 'tailscale serve failed' 'ERROR'; exit 1 }
Write-CmLog "undo: tailscale serve --https=$HttpsPort off"
exit 0
