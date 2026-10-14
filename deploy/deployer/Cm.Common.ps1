# Shared helpers for the CollectionMaxxing deploy scripts. Dot-source this file; it has no side effects when loaded.
#
# SAFETY MODEL (read this before changing anything)
#   * Every external command (docker, gh, tailscale, schtasks, curl) goes through Invoke-Cm. Tests replace the real
#     runner with a recorder by setting $global:CmRunner, so a test can prove which commands would have run.
#   * Calls marked -Mutating (anything that changes the machine: pull, up, migrate, schtasks, tailscale serve, file
#     copies) only run when the script was started with -Apply. Without -Apply (or with -WhatIf) they are printed, not run.
#   * When CM_TEST=1 and no runner is installed, Invoke-Cm throws instead of touching the real machine.
#Requires -Version 7.0

# Ports other apps on this PC already use. The API is 5480; the Aspire dashboard is 18888.
$script:CmForbiddenPorts = @(8080, 8409, 32400, 8096)

function Initialize-Cm {
    <# Sets the run mode. Call once at the top of an entry script. #>
    param(
        [bool]$Apply = $false,
        [string]$LogFile = '',
        [string[]]$Secrets = @()
    )
    $global:CmApply = $Apply
    $global:CmLogFile = $LogFile
    $global:CmSecrets = @($Secrets | Where-Object { $_ })
}

# ---- logging ----------------------------------------------------------------------------------------------------

function Add-CmLogLine {
    param([string]$Path, [string]$Line, [int]$MaxBytes = 1MB, [int]$Keep = 5)
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    if ((Test-Path -LiteralPath $Path) -and (Get-Item -LiteralPath $Path).Length -gt $MaxBytes) {
        # deployer.log -> deployer.log.1 -> ... -> deployer.log.<Keep>, the oldest is dropped.
        for ($i = $Keep; $i -ge 1; $i--) {
            $from = if ($i -eq 1) { $Path } else { "$Path.$($i - 1)" }
            $to = "$Path.$i"
            if (Test-Path -LiteralPath $from) { Move-Item -LiteralPath $from -Destination $to -Force }
        }
    }
    Add-Content -LiteralPath $Path -Value $Line -Encoding utf8
}

function Write-CmLog {
    <# Prints a line and, in apply mode, appends it to the rotating log file. Known secrets are masked. #>
    param([Parameter(Mandatory)][string]$Message, [string]$Level = 'INFO')
    foreach ($secret in @($global:CmSecrets)) {
        if ($secret) { $Message = $Message.Replace($secret, '***') }
    }
    $line = '{0:u} {1,-5} {2}' -f [DateTime]::UtcNow, $Level, $Message
    Write-Host $line
    if ($global:CmApply -and $global:CmLogFile) { Add-CmLogLine -Path $global:CmLogFile -Line $line }
}

# ---- the one seam for external commands ------------------------------------------------------------------------

function Format-CmCommand {
    param([string]$Command, [string[]]$Arguments = @(), [hashtable]$Env = @{})
    $envPart = (@($Env.Keys | Sort-Object) | ForEach-Object { "$_=$($Env[$_])" }) -join ' '
    $argPart = (@($Arguments) | ForEach-Object { if ("$_" -match '[\s"]') { '"' + ("$_" -replace '"', '\"') + '"' } else { "$_" } }) -join ' '
    (@($envPart, $Command, $argPart) | Where-Object { $_ }) -join ' '
}

function Invoke-CmReal {
    param([string]$Command, [string[]]$Arguments, [hashtable]$Env)
    $saved = @{}
    foreach ($key in $Env.Keys) {
        $saved[$key] = [Environment]::GetEnvironmentVariable($key)
        [Environment]::SetEnvironmentVariable($key, [string]$Env[$key])
    }
    try {
        $global:LASTEXITCODE = 0
        $output = @(& $Command @Arguments 2>&1 | ForEach-Object { "$_" })
        $code = if ($null -eq $LASTEXITCODE) { 0 } else { $LASTEXITCODE }
        [pscustomobject]@{ ExitCode = $code; Output = $output }
    }
    catch {
        [pscustomobject]@{ ExitCode = 127; Output = @("$($_.Exception.Message)") }
    }
    finally {
        foreach ($key in $Env.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) }
    }
}

function Invoke-Cm {
    <#
      Runs one external command and returns @{ ExitCode; Output }.
      -Mutating marks a command that changes the machine. It is only run in apply mode; otherwise it is printed and a
      fake success is returned, and it never reaches the runner.
    #>
    param(
        [Parameter(Mandatory)][string]$Command,
        [string[]]$Arguments = @(),
        [hashtable]$Env = @{},
        [switch]$Mutating
    )
    $display = Format-CmCommand -Command $Command -Arguments $Arguments -Env $Env
    if ($Mutating -and -not $global:CmApply) {
        Write-CmLog "dry run, would run: $display"
        return [pscustomobject]@{ ExitCode = 0; Output = @(); DryRun = $true; Display = $display }
    }
    if ($global:CmRunner) {
        return & $global:CmRunner $Command $Arguments $Env $Mutating.IsPresent
    }
    if ($env:CM_TEST -eq '1') {
        throw "CM_TEST=1 but no fake runner is installed; refusing to run '$display' for real."
    }
    Invoke-CmReal -Command $Command -Arguments $Arguments -Env $Env
}

function Start-CmSleep {
    param([int]$Seconds)
    if ($global:CmSleep) { & $global:CmSleep $Seconds } else { Start-Sleep -Seconds $Seconds }
}

# ---- config, state, lock ----------------------------------------------------------------------------------------

function Read-CmEnvFile {
    <# Parses KEY=VALUE lines. Returns an empty table if the file is missing. Never logs values. #>
    param([string]$Path)
    $result = @{}
    if (-not (Test-Path -LiteralPath $Path)) { return $result }
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -match '^\s*#' -or $line -notmatch '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=(.*)$') { continue }
        $value = $Matches[2].Trim()
        if ($value.Length -ge 2 -and (($value[0] -eq '"' -and $value[-1] -eq '"') -or ($value[0] -eq "'" -and $value[-1] -eq "'"))) {
            $value = $value.Substring(1, $value.Length - 2)
        }
        $result[$Matches[1]] = $value
    }
    $result
}

function Test-CmPort {
    <# Returns an error string for a port that is invalid or used by another app, else $null. #>
    param([string]$Name, $Value)
    $port = 0
    if (-not [int]::TryParse("$Value", [ref]$port) -or $port -lt 1024 -or $port -gt 65535) {
        return "$Name='$Value' is not a port between 1024 and 65535"
    }
    if ($script:CmForbiddenPorts -contains $port) {
        return "$Name=$port is used by another app on this PC (8080, 8409, 32400 and 8096 are taken)"
    }
    $null
}

function Test-CmConfig {
    <#
      The config guard. Takes the parsed .env and the output of `docker compose config --format json`.
      Returns a list of problems; an empty list means the config may be used.
      Refuses: the four ports other apps use, any bind that is not 127.0.0.1, host networking, privileged containers,
      and anything published by postgres.
    #>
    param([hashtable]$EnvMap = @{}, $ComposeConfig = $null)
    $problems = [System.Collections.Generic.List[string]]::new()
    foreach ($name in 'CM_API_PORT', 'CM_DASHBOARD_PORT') {
        if ($EnvMap.ContainsKey($name)) {
            $err = Test-CmPort -Name $name -Value $EnvMap[$name]
            if ($err) { $problems.Add($err) }
        }
    }
    if ($null -ne $ComposeConfig -and $ComposeConfig.services) {
        foreach ($service in $ComposeConfig.services.PSObject.Properties) {
            $name = $service.Name
            $def = $service.Value
            if ($def.network_mode -eq 'host') { $problems.Add("service '$name' uses host networking") }
            if ($def.privileged -eq $true) { $problems.Add("service '$name' is privileged") }
            $ports = @($def.ports | Where-Object { $_ })
            if ($name -eq 'postgres' -and $ports.Count -gt 0) { $problems.Add("service 'postgres' must not publish a port") }
            foreach ($p in $ports) {
                if ($p.host_ip -ne '127.0.0.1') {
                    $problems.Add("service '$name' publishes port $($p.published) on '$($p.host_ip)' instead of 127.0.0.1")
                }
                $err = Test-CmPort -Name "service '$name' published port" -Value $p.published
                if ($err) { $problems.Add($err) }
            }
        }
    }
    $problems.ToArray()
}

function ConvertTo-CmUtc {
    <# Docker reports build times with nanoseconds; ConvertFrom-Json may hand back a DateTime or a string. #>
    param($Value)
    if ($null -eq $Value -or "$Value" -eq '') { return $null }
    if ($Value -is [DateTime]) { return $Value.ToUniversalTime() }
    if ($Value -is [DateTimeOffset]) { return $Value.UtcDateTime }
    $text = "$Value" -replace '(\.\d{7})\d+', '$1'
    [DateTimeOffset]::Parse($text, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal).UtcDateTime
}

function Read-CmState {
    param([string]$Path)
    if (Test-Path -LiteralPath $Path) {
        try { return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable } catch { }
    }
    @{}
}

function Write-CmState {
    <# Atomic write (temp file, then move). Apply mode only. #>
    param([string]$Path, [hashtable]$State)
    if (-not $global:CmApply) { return }
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $tmp = "$Path.tmp"
    $State | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $tmp -Encoding utf8
    Move-Item -LiteralPath $tmp -Destination $Path -Force
}

function Enter-CmLock {
    <# Returns an open exclusive stream, or $null when another run already holds the lock. #>
    param([string]$Path)
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    try { [System.IO.File]::Open($Path, 'OpenOrCreate', 'ReadWrite', 'None') } catch [System.IO.IOException] { $null }
}

function Exit-CmLock {
    param($Lock)
    if ($Lock) { $Lock.Dispose() }
}

function Get-CmComposeArgs {
    <# The fixed front of every docker compose call. The deployer never edits .env; CM_TAG is passed in the environment. #>
    param($Ctx, [string[]]$Rest)
    @('compose', '--env-file', $Ctx.EnvFile, '-f', $Ctx.ComposeFile, '-p', 'collectionmaxxing') + $Rest
}
