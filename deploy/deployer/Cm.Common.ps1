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

function Hide-CmSecrets {
    <# Masks every known secret (database password, heartbeat URL) in a piece of text. #>
    param([string]$Text)
    foreach ($secret in @($global:CmSecrets)) {
        if ($secret) { $Text = $Text.Replace($secret, '***') }
    }
    $Text
}

function Write-CmLog {
    <# Prints a line and, in apply mode, appends it to the rotating log file. Known secrets are masked. #>
    param([Parameter(Mandatory)][string]$Message, [string]$Level = 'INFO')
    $Message = Hide-CmSecrets $Message
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
    param([string]$Command, [string[]]$Arguments, [hashtable]$Env, [string]$InputText = '')
    $saved = @{}
    foreach ($key in $Env.Keys) {
        $saved[$key] = [Environment]::GetEnvironmentVariable($key)
        [Environment]::SetEnvironmentVariable($key, [string]$Env[$key])
    }
    try {
        $global:LASTEXITCODE = 0
        $output = if ($InputText) { @($InputText | & $Command @Arguments 2>&1 | ForEach-Object { "$_" }) }
        else { @(& $Command @Arguments 2>&1 | ForEach-Object { "$_" }) }
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
      -InputText goes to the command's standard input, so a secret never appears on a command line or in the log.
    #>
    param(
        [Parameter(Mandatory)][string]$Command,
        [string[]]$Arguments = @(),
        [hashtable]$Env = @{},
        [switch]$Mutating,
        [string]$InputText = ''
    )
    $display = Format-CmCommand -Command $Command -Arguments $Arguments -Env $Env
    if ($Mutating -and -not $global:CmApply) {
        Write-CmLog "dry run, would run: $display"
        return [pscustomobject]@{ ExitCode = 0; Output = @(); DryRun = $true; Display = $display }
    }
    if ($global:CmRunner) {
        return & $global:CmRunner $Command $Arguments $Env $Mutating.IsPresent $InputText
    }
    if ($env:CM_TEST -eq '1') {
        throw "CM_TEST=1 but no fake runner is installed; refusing to run '$display' for real."
    }
    Invoke-CmReal -Command $Command -Arguments $Arguments -Env $Env -InputText $InputText
}

function Use-CmPortableTools {
    <#
      Puts a portable gh in <Root>\tools\gh\bin first on this process's PATH, if it is there. `gh attestation` needs gh
      2.49+, and a machine-wide gh can only be upgraded with admin rights. The machine and user PATH are not touched.
    #>
    param([Parameter(Mandatory)][string]$Root)
    $bin = [IO.Path]::Combine($Root, 'tools', 'gh', 'bin')   # not Join-Path: a C:\ root throws on Linux
    if (-not (Test-Path -LiteralPath $bin -PathType Container)) { return }
    $sep = [IO.Path]::PathSeparator
    $rest = @($env:PATH -split [regex]::Escape($sep) | Where-Object { $_ -and $_ -ne $bin })
    $env:PATH = (@($bin) + $rest) -join $sep
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

function ConvertTo-CmPathKey {
    <#
      A path in a form that can be compared as text: forward slashes, no '.', '..' or empty parts, lower case, no
      trailing slash. Purely lexical (it never touches the disk and works the same on Windows and Linux paths), so
      'F:\Backups\X\' and 'f:/backups/x' are the same key, and 'F:\Backups\X\..\Y' is 'f:/backups/y'. A leading '//'
      (the Docker Desktop way of writing '/var/run/...', or a UNC share) collapses to one '/'. A '..' never climbs
      above a drive letter or the root. A relative path stays relative.
    #>
    param([string]$Path)
    $p = ("$Path").Trim() -replace '\\', '/'
    if (-not $p) { return '' }
    $rooted = $p.StartsWith('/')
    $parts = [System.Collections.Generic.List[string]]::new()
    foreach ($part in ($p -split '/')) {
        if ($part -eq '' -or $part -eq '.') { continue }
        if ($part -eq '..') {
            if ($parts.Count -gt 0 -and $parts[$parts.Count - 1] -ne '..') {
                if ($parts[$parts.Count - 1] -notmatch '^[a-z]:$') { $parts.RemoveAt($parts.Count - 1) }
            }
            elseif (-not $rooted) { $parts.Add('..') }
            continue
        }
        $parts.Add($part.ToLowerInvariant())
    }
    $prefix = if ($rooted) { '/' } else { '' }
    $prefix + ($parts -join '/')
}

function Test-CmPathUnder {
    <# True when Path is Root or inside it. Both are compared as ConvertTo-CmPathKey keys; a relative Path or Root never matches. #>
    param([string]$Path, [string]$Root)
    $p = ConvertTo-CmPathKey $Path
    $r = ConvertTo-CmPathKey $Root
    if (-not $p -or -not $r) { return $false }
    $absolute = { param($k) $k.StartsWith('/') -or $k -match '^[a-z]:' }
    if (-not (& $absolute $p) -or -not (& $absolute $r)) { return $false }
    if ($p -eq $r) { return $true }
    $p.StartsWith($r.TrimEnd('/') + '/')
}

function Get-CmAllowedMountRoots {
    <#
      The host folders a compose file may bind into a container: the backup, secrets, recognition and evals folders
      named in .env (a relative one is read from the compose file's own folder, the way compose reads it), and the
      places the compose file itself reaches by default: its own folder (./recognition), the clone's evals folder
      (../evals/baseline.json) and the evals folder beside the clone (../../evals). Nothing else, so not the drive,
      not C:\CollectionMaxxing itself (which holds .env, the state and the log), and not the home folder.
    #>
    param([hashtable]$EnvMap = @{}, [string]$ComposeFile = '')
    $roots = [System.Collections.Generic.List[string]]::new()
    $composeDir = if ($ComposeFile) { (ConvertTo-CmPathKey $ComposeFile) -replace '/[^/]*$', '' } else { '' }
    foreach ($name in 'CM_BACKUP_DIR', 'CM_SECRETS_DIR', 'CM_RECOGNITION_DIR', 'CM_EVALS_DIR') {
        $value = if ($EnvMap.ContainsKey($name)) { "$($EnvMap[$name])".Trim() } else { '' }
        if (-not $value) { continue }
        $key = ConvertTo-CmPathKey $value
        if (-not ($key.StartsWith('/') -or $key -match '^[a-z]:')) {
            if (-not $composeDir) { continue }
            $key = ConvertTo-CmPathKey "$composeDir/$value"
        }
        $roots.Add($key)
    }
    if ($composeDir -and ($composeDir.StartsWith('/') -or $composeDir -match '^[a-z]:')) {
        $roots.Add($composeDir)
        $roots.Add((ConvertTo-CmPathKey "$composeDir/../evals"))
        $roots.Add((ConvertTo-CmPathKey "$composeDir/../../evals"))
    }
    $roots.ToArray()
}

function Test-CmConfig {
    <#
      The config guard. Takes the parsed .env and the output of `docker compose config --format json`.
      Returns a list of problems; an empty list means the config may be used.
      Refuses: the four ports other apps use, any bind that is not 127.0.0.1, host networking, privileged containers,
      anything published by postgres, added Linux capabilities (cap_add), the host process or IPC namespace
      (pid: host, ipc: host), host devices, the Docker socket or engine pipe in any spelling, any bind mount of a host
      folder that is not on the allow-list (Get-CmAllowedMountRoots; pass the compose file's path so its own default
      folders count), and a named volume that is really a bind of a host folder (driver_opts). Messages name the
      service and the container path, never a host path, because a host path may come from .env.
    #>
    param([hashtable]$EnvMap = @{}, $ComposeConfig = $null, [string]$ComposeFile = '')
    $problems = [System.Collections.Generic.List[string]]::new()
    foreach ($name in 'CM_API_PORT', 'CM_DASHBOARD_PORT') {
        if ($EnvMap.ContainsKey($name)) {
            $err = Test-CmPort -Name $name -Value $EnvMap[$name]
            if ($err) { $problems.Add($err) }
        }
    }
    if ($null -ne $ComposeConfig -and $ComposeConfig.services) {
        $allowed = @(Get-CmAllowedMountRoots -EnvMap $EnvMap -ComposeFile $ComposeFile)
        foreach ($service in $ComposeConfig.services.PSObject.Properties) {
            $name = $service.Name
            $def = $service.Value
            if ($def.network_mode -eq 'host') { $problems.Add("service '$name' uses host networking") }
            if ($def.privileged -eq $true) { $problems.Add("service '$name' is privileged") }
            if (@($def.cap_add | Where-Object { $_ }).Count -gt 0) { $problems.Add("service '$name' adds Linux capabilities (cap_add)") }
            if ("$($def.pid)" -eq 'host') { $problems.Add("service '$name' shares the host process namespace (pid: host)") }
            if ("$($def.ipc)" -eq 'host') { $problems.Add("service '$name' shares the host IPC namespace (ipc: host)") }
            if (@($def.devices | Where-Object { $_ }).Count -gt 0) { $problems.Add("service '$name' maps host devices (devices)") }
            foreach ($v in @($def.volumes | Where-Object { $_ })) {
                $type = "$($v.type)"
                $target = "$($v.target)"
                if ($type -in 'volume', 'tmpfs') { continue }
                $source = "$($v.source)"
                if ($source -match '(?i)docker[._]?sock|docker_engine|[\\/]var[\\/]run[\\/]docker' -or $target -match '(?i)docker[._]?sock') {
                    $problems.Add("service '$name' mounts the Docker socket or engine pipe (container path $target)")
                    continue
                }
                if ($type -ne 'bind') { $problems.Add("service '$name' has a '$type' mount at $target, a kind of mount the guard does not allow"); continue }
                $inside = $false
                foreach ($root in $allowed) { if (Test-CmPathUnder -Path $source -Root $root) { $inside = $true; break } }
                if (-not $inside) {
                    $problems.Add("service '$name' bind-mounts a host folder outside the folders it may use (container path $target); allowed are the backup, secrets, recognition and evals folders named in .env and the folders beside the compose file")
                }
            }
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
    if ($null -ne $ComposeConfig -and $ComposeConfig.volumes) {
        foreach ($volume in $ComposeConfig.volumes.PSObject.Properties) {
            $opts = $volume.Value.driver_opts
            if ($null -eq $opts) { continue }
            $names = @($opts.PSObject.Properties.Name)
            if ($names -contains 'device' -or "$($opts.o)" -match '(?i)\bbind\b') {
                $problems.Add("volume '$($volume.Name)' is a bind of a host folder in disguise (driver_opts)")
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
    <#
      Opens the lock file exclusively and stamps it with the holder (pid and UTC start), so a later run that finds
      the lock held can tell a live holder from a stuck one (see CmStaleLockMinutes in Cm.Deploy.ps1). Returns the
      open stream, or $null when another run already holds the lock.
    #>
    param([string]$Path)
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    try { $lock = [System.IO.File]::Open($Path, 'OpenOrCreate', 'ReadWrite', 'None') }
    catch [System.IO.IOException] { return $null }
    try {
        # Written through the open stream: the stamp (and with it the file's modified time) is what a later run
        # judges the holder by, and no other handle can be opened while this one is held.
        $stamp = [Text.Encoding]::UTF8.GetBytes("pid=$PID started=$([DateTime]::UtcNow.ToString('o'))")
        $lock.SetLength(0)
        $lock.Write($stamp, 0, $stamp.Length)
        $lock.Flush()
    }
    catch { $lock.Dispose(); throw }
    $lock
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
