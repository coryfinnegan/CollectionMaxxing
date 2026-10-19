# Shared by the scripts in scripts/security. Dot-sourced, never run on its own.
#
# The rules every script follows (the QA-1c brief and CLAUDE.md "Hard rules"):
#   - Every container, network and image this folder makes is named cm-sec-* and labelled cm.agent=qa-1c. Stop-Security.ps1 removes
#     exactly the things that carry the label and nothing else. No prune, no volumes (database data lives on tmpfs).
#   - Nothing ever binds 8080, 8409, 32400 or 8096. The only published port is 127.0.0.1:<random>, and only for the API of the throwaway stack.
#   - Raw reports go to the temp folder, never into the working tree.
#   - Every external command goes through Invoke-Sec, which the tests replace with a recorder ($global:SecRunner).

$script:ReservedPorts = @(8080, 8409, 32400, 8096)
$script:LabelKey = 'cm.agent'
$script:LabelValue = 'qa-1c'
$script:Label = "$($script:LabelKey)=$($script:LabelValue)"
$script:NamePrefix = 'cm-sec'
$script:ZapImage = 'ghcr.io/zaproxy/zaproxy:stable'
$script:TrivyImage = 'aquasec/trivy:latest'
$script:PostgresImage = 'postgres:17'
$script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$script:ApiPort = 5480

function Invoke-Sec {
    <# Runs one external command and returns @{ ExitCode; Output }. Tests install $global:SecRunner. #>
    param([Parameter(Mandatory)][string]$Command, [string[]]$Arguments = @(), [hashtable]$Env = @{})
    if ($global:SecRunner) { return & $global:SecRunner $Command $Arguments $Env }
    if ($env:CM_SEC_TEST -eq '1') { throw "CM_SEC_TEST=1 but no fake runner is installed; refusing to run '$Command $($Arguments -join ' ')' for real." }
    $saved = @{}
    foreach ($key in $Env.Keys) { $saved[$key] = [Environment]::GetEnvironmentVariable($key); [Environment]::SetEnvironmentVariable($key, [string]$Env[$key]) }
    try {
        $global:LASTEXITCODE = 0
        $output = @(& $Command @Arguments 2>&1 | ForEach-Object { "$_" })
        [pscustomobject]@{ ExitCode = $(if ($null -eq $LASTEXITCODE) { 0 } else { $LASTEXITCODE }); Output = $output }
    }
    catch { [pscustomobject]@{ ExitCode = 127; Output = @("$($_.Exception.Message)") } }
    finally { foreach ($key in $Env.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) } }
}

function Test-SecUnixHost {
    <# True on Linux and macOS (tests set $global:SecIsUnix to choose). #>
    if ($null -ne $global:SecIsUnix) { return [bool]$global:SecIsUnix }
    -not $IsWindows
}

function Open-SecFolderForContainer {
    <#
      A folder bind-mounted into a container keeps the host's owner and mode. On Linux the container's user (ZAP's zap, uid 1000;
      the API's app user) is not the user who made the folder (the runner, another uid), so it could not write into it: no
      report.json, no setup-code.txt. Docker Desktop on Windows/macOS hides this. These are per-run temp folders holding only
      throwaway material, so opening the mode (not --user, which would leave ZAP without a writable home) is enough.
      Not done on Windows, where chmod does not exist and is not needed.
    #>
    param([string]$Path, [string]$For)
    if (-not (Test-SecUnixHost)) { return }
    $r = Invoke-Sec chmod @('-R', 'a+rwX', $Path)
    if ($r.ExitCode -ne 0) { Stop-SecFailure "Could not make the folder writable for $For ($Path): $($r.Output -join ' ')" }
}

function Write-Sec { param([string]$Message) Write-Host $Message }

function Stop-SecFailure {
    <# Prints what is wrong and what to do, then stops with a non-zero exit through an exception the caller turns into exit 1. #>
    param([string]$Message)
    throw $Message
}

# ---- guards ---------------------------------------------------------------------------------------------------------

function Assert-PortAllowed {
    <# Refuses the ports that belong to other apps on this PC. 0 means "pick a random one". #>
    param([int]$Port, [string]$What = 'host port')
    if ($Port -in $script:ReservedPorts) {
        Stop-SecFailure "Refusing to use $What ${Port}: it belongs to another app on this PC (8080 SABnzbd, 8409 ErsatzTV, 32400 Plex, 8096 Jellyfin). Leave -Port at 0 for a random loopback port, or choose another."
    }
    if ($Port -lt 0 -or $Port -gt 65535) { Stop-SecFailure "Refusing $What ${Port}: not a port number." }
}

function Assert-DockerUp {
    $r = Invoke-Sec docker @('info', '--format', '{{.ServerVersion}}')
    if ($r.ExitCode -ne 0) {
        Stop-SecFailure 'Docker is not running. Start Docker Desktop, wait until it says it is running, then run this again. (Use -DryRun to see the plan without Docker.)'
    }
}

function Get-ReportDir {
    <# Reports always go to the temp folder, in a folder per run. A folder inside the repository is refused. #>
    param([string]$RunId, [string]$Requested)
    $dir = if ($Requested) { $Requested } else { Join-Path ([IO.Path]::GetTempPath()) "cm-sec-reports/$RunId" }
    $full = [IO.Path]::GetFullPath($dir)
    Assert-OutsideRepo $full
    $full
}

function Assert-OutsideRepo {
    param([string]$Path)
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $root = $script:RepoRoot.TrimEnd('\', '/')
    if ($full.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or $full.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or $full.StartsWith($root + '/', [StringComparison]::OrdinalIgnoreCase)) {
        Stop-SecFailure "Refusing to write reports inside the repository ($full). Raw scanner reports belong in the temp folder."
    }
}

function New-RunId { (Get-Date -Format 'yyMMddHHmmss') + '-' + (Get-Random -Minimum 100 -Maximum 999) }

function Test-ZapLimits {
    <# ZAP is limited because the PC may be busy: at most 2 threads per host and a wall-clock cap. #>
    param([int]$Threads, [int]$MaxMinutes)
    if ($Threads -lt 1 -or $Threads -gt 2) { Stop-SecFailure "ZAP is capped at 2 threads per host (asked for $Threads): the PC may be busy." }
    if ($MaxMinutes -lt 1 -or $MaxMinutes -gt 30) { Stop-SecFailure "The wall-clock cap must be 1 to 30 minutes (asked for $MaxMinutes)." }
}

# ---- the plan ---------------------------------------------------------------------------------------------------------

function Get-SecPlan {
    <# What a run would do. A dry run prints this and starts nothing. #>
    param(
        [Parameter(Mandatory)][ValidateSet('zap-baseline', 'zap-api', 'image-scan', 'dependency-audit')][string]$Mode,
        [string]$RunId, [int]$Threads = 2, [int]$MaxMinutes = 10, [int]$Port = 0, [string]$ReportDir
    )
    $network = "$($script:NamePrefix)-net-$RunId"
    $images = switch ($Mode) {
        'zap-baseline' { @($script:PostgresImage, "$($script:NamePrefix)-api:$RunId (built from deploy/docker/Dockerfile.api)", $script:ZapImage) }
        'zap-api' { @($script:PostgresImage, "$($script:NamePrefix)-api:$RunId (built from deploy/docker/Dockerfile.api)", $script:ZapImage) }
        'image-scan' { @("$($script:NamePrefix)-api:$RunId (built from deploy/docker/Dockerfile.api)", $script:TrivyImage) }
        default { @() }
    }
    [pscustomobject]@{
        Mode = $Mode
        RunId = $RunId
        Network = $(if ($Mode -in 'zap-baseline', 'zap-api') { $network } else { '(none)' })
        Images = $images
        Containers = $(if ($Mode -in 'zap-baseline', 'zap-api') { @("$($script:NamePrefix)-pg-$RunId", "$($script:NamePrefix)-api-$RunId", "$($script:NamePrefix)-zap-$RunId") } elseif ($Mode -eq 'image-scan') { @("$($script:NamePrefix)-trivy-$RunId") } else { @() })
        Label = $script:Label
        ZapMode = $(switch ($Mode) { 'zap-baseline' { 'baseline (passive: spider and passive rules only)' } 'zap-api' { 'api scan (active, from contracts/openapi.json, as the throwaway owner)' } default { 'not used' } })
        ThreadsPerHost = $Threads
        WallClockMinutes = $MaxMinutes
        PublishedPort = $(if ($Mode -in 'zap-baseline', 'zap-api') { if ($Port -gt 0) { "127.0.0.1:$Port" } else { '127.0.0.1:<random>' } } else { '(none)' })
        ReportDir = $ReportDir
    }
}

function Write-SecPlan {
    param($Plan, [switch]$Dry)
    Write-Sec "plan  mode       $($Plan.Mode)"
    Write-Sec "      network     $($Plan.Network)"
    Write-Sec "      images      $($Plan.Images -join '; ')"
    Write-Sec "      containers  $($Plan.Containers -join ', ')"
    Write-Sec "      label       $($Plan.Label) on every container, network and image"
    Write-Sec "      zap mode    $($Plan.ZapMode)"
    Write-Sec "      thread cap  $($Plan.ThreadsPerHost) per host; wall-clock cap $($Plan.WallClockMinutes) min"
    Write-Sec "      port        $($Plan.PublishedPort) (API only; never 8080, 8409, 32400, 8096)"
    Write-Sec "      reports     $($Plan.ReportDir)"
    if ($Dry) { Write-Sec 'dry run: nothing was started' }
}

# ---- cleanup (used by Stop-Security.ps1 and by the scripts' own finally blocks) --------------------------------------------

function Remove-SecResources {
    <# Removes exactly what carries the label: containers, networks, images. Never anything else. Returns counts. #>
    $counts = [ordered]@{ Containers = 0; Networks = 0; Images = 0 }
    $ids = @((Invoke-Sec docker @('ps', '-a', '-q', '--filter', "label=$($script:Label)")).Output | Where-Object { $_ -match '^[0-9a-f]{6,}$' })
    foreach ($id in $ids) { if ((Invoke-Sec docker @('rm', '-f', $id)).ExitCode -eq 0) { $counts.Containers++ } }
    $nets = @((Invoke-Sec docker @('network', 'ls', '-q', '--filter', "label=$($script:Label)")).Output | Where-Object { $_ -match '^[0-9a-f]{6,}$' })
    foreach ($id in $nets) { if ((Invoke-Sec docker @('network', 'rm', $id)).ExitCode -eq 0) { $counts.Networks++ } }
    $imgs = @((Invoke-Sec docker @('image', 'ls', '-q', '--filter', "label=$($script:Label)")).Output | Where-Object { $_ -match '^[0-9a-f]{6,}$' } | Select-Object -Unique)
    foreach ($id in $imgs) { if ((Invoke-Sec docker @('image', 'rm', $id)).ExitCode -eq 0) { $counts.Images++ } }
    [pscustomobject]$counts
}

# ---- docker helpers ---------------------------------------------------------------------------------------------------------

function Get-LabelArgs { @('--label', $script:Label) }

function Wait-SecContainer {
    <# Waits for a detached container to stop; stops it when the wall-clock cap passes. Returns @{ ExitCode; TimedOut }. #>
    param([string]$Name, [int]$MaxMinutes, [int]$PollSeconds = 5)
    $deadline = (Get-Date).AddMinutes($MaxMinutes)
    while ($true) {
        $r = Invoke-Sec docker @('inspect', '--format', '{{.State.Running}} {{.State.ExitCode}}', $Name)
        $text = ($r.Output -join ' ').Trim()
        if ($r.ExitCode -ne 0) { return [pscustomobject]@{ ExitCode = 127; TimedOut = $false } }
        if ($text -match '^false (\d+)') { return [pscustomobject]@{ ExitCode = [int]$Matches[1]; TimedOut = $false } }
        if ((Get-Date) -gt $deadline) {
            Invoke-Sec docker @('stop', '-t', '10', $Name) | Out-Null
            return [pscustomobject]@{ ExitCode = 124; TimedOut = $true }
        }
        if ($global:SecSleep) { & $global:SecSleep $PollSeconds } else { Start-Sleep -Seconds $PollSeconds }
    }
}

function New-RandomPassword { -join ((48..57) + (97..122) | Get-Random -Count 24 | ForEach-Object { [char]$_ }) }

function Build-ApiImage {
    param([string]$Tag, [string]$WebDist = 'deploy/docker/no-web')
    $r = Invoke-Sec docker @('build', '--quiet', '--label', $script:Label, '-t', $Tag, '-f', 'deploy/docker/Dockerfile.api', '--build-arg', "WEB_DIST=$WebDist", $script:RepoRoot)
    if ($r.ExitCode -ne 0) { Stop-SecFailure "Building the API image failed:`n$(($r.Output | Select-Object -Last 15) -join "`n")" }
}

function Get-PublishedPort {
    param([string]$Container)
    $r = Invoke-Sec docker @('port', $Container, '5480/tcp')
    foreach ($line in $r.Output) { if ($line -match '127\.0\.0\.1:(\d+)') { return [int]$Matches[1] } }
    Stop-SecFailure "Could not read the published port of $Container."
}

function ConvertFrom-DotnetVulnerable {
    <# Parses the text of `dotnet list package --vulnerable --include-transitive` into one object per (project, package, severity). #>
    param([string[]]$Lines)
    $project = $null
    foreach ($line in $Lines) {
        if ($line -match "^Project [`'``]([^`'``]+)[`'``]") { $project = $Matches[1]; continue }
        # Top-level rows have a "Requested" column, transitive rows do not; the severity word is what anchors the match.
        if ($line -match '^\s+>\s+(\S+)\s+(?:\S+\s+)?(\S+)\s+(Low|Moderate|High|Critical)\s+(\S+)') {
            [pscustomobject]@{ Project = $project; Package = $Matches[1]; Resolved = $Matches[2]; Severity = $Matches[3]; Advisory = $Matches[4]; TestOnly = ($project -match '[\\/]tests[\\/]|\.Tests$|\.TestSupport$|\.Evals$|\.AppHost$') }
        }
    }
}

function Write-Tally {
    param([string]$Title, [hashtable]$Counts)
    Write-Sec ("{0}: {1}" -f $Title, (($Counts.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ' '))
}
