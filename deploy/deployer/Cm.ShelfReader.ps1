# The shelf reader on the PC's graphics card (ADR-033, #795). Dot-sourced by Cm.Deploy.ps1 (needs Cm.Common.ps1 and
# Cm.Alerts.ps1).
#
# The rule that outranks every other one here: the reader can never take the app down or cause a rollback.
#   * The step runs only on a quiet poll (Complete-CmQuietRun: nothing new to deploy), never in a pass that deployed,
#     so it can neither change a deploy's outcome nor stretch a deploy pass. It never throws.
#   * The deploy's own health check never looks at the reader (it is not in $Ctx.Services), and the api does not depend
#     on it. The api's reader client answers "unavailable" quickly when the endpoint points at nothing (#797), so
#     switching the reader OFF never needs the api: the reader is stopped, the state says off, and the next deploy brings
#     the api up with an empty endpoint (Invoke-CmUp reads shelfReader.on).
#   * The api is restarted outside a deploy for ONE reason only: the reader has just become healthy and the running api
#     does not have its endpoint yet (read from the running container). If the api then fails its smoke test it is
#     restarted once more without the endpoint, and if that fails too a real api alert (shelf-reader-api-down) is raised.
#     At most CmShelfReaderApiRestartsPerDay such restarts happen in a UTC day.
#   * A reader problem is an `ALERT shelf-reader ...` line and a `shelf-reader-failed` alert issue, never a deploy alert,
#     a heartbeat failure ping or a rollback. The reader is stopped and the next try waits (CmShelfReaderRetryHours).
#   * A missed card check (nvidia-smi failing or timing out) while the reader is in use changes nothing on that pass;
#     only CmShelfReaderCardMisses misses in a row count as "no card" (the failure path).
#   * The model files are downloaded in the background (a detached curl to <name>.partial, by the pinned revision,
#     resumable), never in the pass. A later pass checks the finished file's size and SHA-256 and only then renames it.
#     A download is alive while its .partial grows from one pass to the next; a second curl is never started on a file
#     a live one is writing (Test-CmDownloadRunning).
#   * CM_SHELF_READER=0 in .env, or a host where nvidia-smi lists no card: nothing is pulled, downloaded or started.
#   * The card inside Docker (#802): a healthy reader is not proof that llama.cpp uses the card (it falls back to the
#     processor). Its start log is read once per container start, when it is first seen healthy and before the api is
#     given the endpoint. It must show llama-server starting ("llama_server: initializing"); then "no usable GPU found" or
#     "failed to initialize CUDA" is a failure. A log that cannot be read (or lacks that line) waits a poll, but not past the
#     start window counted from the container's start.
#   * A changed definition reaches a running reader (#802): compose's config hash for shelf-reader at the deployed commit
#     against the com.docker.compose.config-hash label of the running container. Different: the same `up -d --no-deps
#     shelf-reader` recreates it, at most once per deployed commit; the start window and the card check apply to the new
#     start, and the api keeps its endpoint (no restart). Either hash unreadable skips only the recreation that poll.
#
# Lock budget: every external call here has a timeout, so one pass of this step is bounded (Get-CmShelfReaderWorstCaseSeconds:
# about 17 minutes on the start or recreate path, 13 on the switch-on path, plus hashing a finished 6 GB file once), well under the
# 30-minute stale-lock alert (CmStaleLockMinutes) even with the quiet poll's image clean-up (90 seconds) before it.
#Requires -Version 7.0

$script:CmShelfReaderService = 'shelf-reader'
$script:CmShelfReaderEndpointUrl = 'http://shelf-reader:8431'
# The image ($script:CmShelfReaderImage) is pinned in Cm.Common.ps1, where the config guard checks it.
$script:CmShelfReaderRepo = 'Qwen/Qwen3-VL-8B-Instruct-GGUF'
$script:CmShelfReaderRevision = 'f982a07559d4a2f6c8744d840bf6fccab30eea96'
$script:CmShelfReaderPins = @(
    @{ Name = 'Qwen3VL-8B-Instruct-Q4_K_M.gguf'; Size = [long]5027784800; Sha256 = '67d1659bfe71b89d50b45a4ad1a9e5b997e5bb16ce5da66a6a6167abd569e9e2' }
    @{ Name = 'mmproj-Qwen3VL-8B-Instruct-F16.gguf'; Size = [long]1159029824; Sha256 = 'ca524100ebf825c9a870db1c580d03879e0da0ab2541697e2458e64891cf9d38' }
)
$script:CmShelfReaderStartMinutes = 10          # a started reader has this long to become healthy (compose start_period: 600s)
$script:CmShelfReaderDownMinutes = 10           # a reader that was on may be unhealthy this long before it is stopped
$script:CmShelfReaderRetryHours = 6             # after a failed pull, start or health, the next try waits this long
$script:CmShelfReaderBadFileRetryHours = 24     # after a file fails its SHA-256 check, the next download waits this long
$script:CmShelfReaderStaleDownloadMinutes = 15  # a .partial that has not grown for this long belongs to a download that died
$script:CmShelfReaderFreeMarginBytes = 10GB     # never fill the disk: this much stays free after a download
$script:CmShelfReaderCardMisses = 15            # missed nvidia-smi checks in a row (about 30 minutes) before the card counts as gone
$script:CmShelfReaderApiRestartsPerDay = 4      # api restarts this step may make in a UTC day (2 switch-on attempts with their restore)
$script:CmShelfReaderCallSeconds = 30           # nvidia-smi, image inspect, compose ps, docker inspect
$script:CmShelfReaderPullSeconds = 600          # the 2.6 GB image pull (docker keeps the finished layers for the next try)
$script:CmShelfReaderUpSeconds = 120            # one `compose up -d --no-deps` (the reader or the api)
$script:CmShelfReaderStopSeconds = 60           # `compose stop shelf-reader`
# What llama.cpp logs at start when CUDA does not come up inside the container and it falls back to the processor (#802).
$script:CmShelfReaderNoCardPattern = '(?i)no usable GPU found|failed to initialize CUDA'
# The line llama-server prints as it starts; a start log without it does not cover the start, so it proves nothing.
$script:CmShelfReaderStartedPattern = 'llama_server: initializing'

function Get-CmShelfReaderFiles { <# The two pinned model files. Tests set $global:CmShelfReaderFiles to tiny ones. #> if ($global:CmShelfReaderFiles) { return @($global:CmShelfReaderFiles) } @($script:CmShelfReaderPins) }

function Get-CmShelfReaderFileUrl {
    <# The download URL of one file, by the pinned revision: never by main, so the bytes cannot change under the hash. #>
    param([string]$Name)
    "https://huggingface.co/$($script:CmShelfReaderRepo)/resolve/$($script:CmShelfReaderRevision)/$Name"
}

function Test-CmShelfReaderSwitchedOff {
    <# The owner's off switch: CM_SHELF_READER=0 (or false, off, no) in .env. #>
    param([hashtable]$EnvMap = @{})
    "$($EnvMap['CM_SHELF_READER'])".Trim() -in @('0', 'false', 'off', 'no')
}

function Get-CmModelsDir {
    <# The host folder of the model files: CM_MODELS_DIR from .env (relative to the compose file's folder), else <Root>\models. #>
    param($Ctx, [hashtable]$EnvMap = @{})
    $value = "$($EnvMap['CM_MODELS_DIR'])".Trim()
    if (-not $value) { return (Join-Path $Ctx.Root 'models') }
    if ([IO.Path]::IsPathRooted($value) -or $value -match '^[A-Za-z]:') { return $value }
    Join-Path (Split-Path -Parent $Ctx.ComposeFile) $value
}

function Get-CmShelfReaderState {
    param([hashtable]$State)
    $sr = $State['shelfReader']
    if ($sr -isnot [System.Collections.IDictionary]) { $sr = @{} }
    foreach ($key in 'files', 'downloads') { if ($sr[$key] -isnot [System.Collections.IDictionary]) { $sr[$key] = @{} } }
    $sr
}

function ConvertTo-CmCanonicalJson {
    <# A JSON text of a state value with every table's keys sorted, so two readings of the same state compare equal. #>
    param($Value)
    function Sort-CmValue($v) {
        if ($v -is [System.Collections.IDictionary]) {
            $o = [ordered]@{}
            foreach ($k in @($v.Keys | ForEach-Object { "$_" } | Sort-Object)) { $o[$k] = Sort-CmValue $v[$k] }
            return $o
        }
        if ($v -is [System.Collections.IList] -and $v -isnot [string]) { return , @($v | ForEach-Object { Sort-CmValue $_ }) }
        $v
    }
    ConvertTo-Json -InputObject (Sort-CmValue $Value) -Depth 10 -Compress
}

function Get-CmShelfReaderEndpoint {
    <# What a deploy gives the api as ShelfReader__Endpoint: the reader's address while the state says it is on, else empty. #>
    param($Ctx)
    try {
        $sr = (Read-CmState -Path $Ctx.StateFile)['shelfReader']
        if ($sr -is [System.Collections.IDictionary] -and $sr['on'] -eq $true) { return $script:CmShelfReaderEndpointUrl }
    }
    catch { }
    ''
}

function Get-CmShelfReaderWorstCaseSeconds {
    <#
      The longest one pass of the reader step can hold the lock, from its timeouts: the start path (card, the compose copy,
      the container's status, the image check, the pull, the start, a stop after a failure) or the switch-on path (card,
      copy, status, the running api's endpoint, the api restart and its smoke test, the restore restart and its smoke test,
      the stop). The two never happen in one pass. Hashing a finished file (once per file) and gh alert calls come on top.
    #>
    param($Ctx)
    $call = $script:CmShelfReaderCallSeconds
    $git = 60   # `git show` of the deployed compose file from the local clone
    $smoke = [math]::Max(1, $Ctx.SmokeRetries) * (5 + $Ctx.SmokeDelaySeconds)   # curl -m 5 per try
    # The start path also covers a recreation (#802): the container's start and hash and compose's hash come before it.
    $start = $call + $git + $call + (2 * $call) + $call + $script:CmShelfReaderPullSeconds + $script:CmShelfReaderUpSeconds + $script:CmShelfReaderStopSeconds
    # The switch-on path reads the container, compose's hash and the start log (the card check) before the api.
    $switch = $call + $git + $call + (3 * $call) + (2 * $call) + (2 * ($script:CmShelfReaderUpSeconds + $smoke)) + $script:CmShelfReaderStopSeconds
    [math]::Max($start, $switch)
}

function Get-CmShelfReaderPlan {
    <# The reader's calls, for the dry-run plan. #>
    param($Ctx)
    $dir = '<models folder>'
    foreach ($f in Get-CmShelfReaderFiles) { "curl.exe (in the background) -fsSL --connect-timeout 30 -C - -o $dir\$($f.Name).partial $(Get-CmShelfReaderFileUrl $f.Name)" }
    Format-CmCommand 'docker' @('pull', '--quiet', $script:CmShelfReaderImage)
    Format-CmCommand 'docker' (Get-CmGpuComposeArgs $Ctx @('up', '-d', '--no-deps', $script:CmShelfReaderService)) @{ CM_TAG = '<deployed sha>' }
    Format-CmCommand 'docker' (Get-CmGpuComposeArgs $Ctx @('ps', '-a', '--format', 'json', $script:CmShelfReaderService)) @{ CM_TAG = '<deployed sha>' }
    '(a running reader: its start and config hash, against compose''s; a different hash recreates it with the same up)'
    Format-CmCommand 'docker' @('inspect', '<reader container>', '--format', '{{.State.StartedAt}}|{{index .Config.Labels "com.docker.compose.config-hash"}}')
    Format-CmCommand 'docker' (Get-CmGpuComposeArgs $Ctx @('config', '--hash', $script:CmShelfReaderService)) @{ CM_TAG = '<deployed sha>' }
    '(once per container start, when it is first healthy: the start log, checked for a graphics card llama.cpp cannot use)'
    Format-CmCommand 'docker' @('logs', '--since', '<start>', '--until', '<start + 10 minutes>', '<reader container>')
    '(once the reader is healthy, and only when the running api does not have the endpoint yet:)'
    Format-CmCommand 'docker' (Get-CmComposeArgs $Ctx @('up', '-d', '--no-deps', 'api')) @{ CM_TAG = '<deployed sha>'; CM_SHELF_READER_ENDPOINT = $script:CmShelfReaderEndpointUrl }
    '(a reader that fails or is switched off is stopped; the api is left alone and the next deploy brings it up without the endpoint)'
    Format-CmCommand 'docker' (Get-CmGpuComposeArgs $Ctx @('stop', $script:CmShelfReaderService)) @{ CM_TAG = '<deployed sha>' }
}

# ---- seams: the background start, the running downloads, free space and hashing ------------------------------------------

function Start-CmDetached {
    <#
      Starts a process that outlives this run (the model download) and does not wait for it. Apply mode only. Tests set
      $global:CmStartDetached; with CM_TEST=1 and no fake it throws instead of starting anything.
    #>
    param([string]$File, [string[]]$ArgumentList)
    $line = Format-CmCommand -Command $File -Arguments $ArgumentList
    if (-not $global:CmApply) { Write-CmLog "dry run, would start in the background: $line"; return $null }
    if ($global:CmStartDetached) { return & $global:CmStartDetached $File $ArgumentList }
    if ($env:CM_TEST -eq '1') { throw "CM_TEST=1 but no fake background starter is installed; refusing to start '$line' for real." }
    $quoted = (@($ArgumentList) | ForEach-Object { if ("$_" -match '[\s"]') { '"' + ("$_" -replace '"', '\"') + '"' } else { "$_" } }) -join ' '
    $splat = @{ FilePath = $File; ArgumentList = $quoted; PassThru = $true }
    if ($IsWindows) { $splat.WindowStyle = 'Hidden' }
    Start-Process @splat
}

function Test-CmDownloadRunning {
    <#
      True when a curl process is already writing this .partial file, so a second one is never started on it. Tests set
      $global:CmFindDownload. When the process list cannot be read the answer is yes: a stalled download waits a poll,
      two writers would corrupt the file.
    #>
    param([string]$Partial)
    if ($global:CmFindDownload) { return [bool](& $global:CmFindDownload $Partial) }
    if ($env:CM_TEST -eq '1') { throw 'CM_TEST=1 but no fake download finder is installed; refusing to read the real process list.' }
    if (-not $IsWindows) { return $false }
    try {
        $leaf = Split-Path -Leaf $Partial
        @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'curl.exe'" -ErrorAction Stop | Where-Object { "$($_.CommandLine)" -like "*$leaf*" }).Count -gt 0
    }
    catch { $true }
}

function Get-CmFreeBytes {
    param([string]$Path)
    if ($global:CmFreeBytes) { return [long](& $global:CmFreeBytes $Path) }
    [long]([IO.DriveInfo]::new([IO.Path]::GetPathRoot([IO.Path]::GetFullPath($Path))).AvailableFreeSpace)
}

function Get-CmFileSha256 {
    param([string]$Path)
    if ($global:CmFileHash) { return "$(& $global:CmFileHash $Path)".ToLowerInvariant() }
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

# ---- one look at the card, the files and the containers ---------------------------------------------------------------------

function Test-CmNvidiaCard {
    <# True when nvidia-smi answers and lists at least one card. Read only, stopped after 30 seconds. #>
    $r = Invoke-Cm -Command 'nvidia-smi' -Arguments @('-L') -TimeoutSeconds $script:CmShelfReaderCallSeconds
    $r.ExitCode -eq 0 -and @(@($r.Output) | Where-Object { "$_" -match '^\s*GPU \d+:' }).Count -gt 0
}

function Write-CmShelfReaderAlert {
    <# An ALERT line (the deployer's alert convention) and its alert issue. Never a deploy alert, never a heartbeat ping. #>
    param($Ctx, [hashtable]$EnvMap, [string]$Why, [string]$Kind = 'shelf-reader-failed', [string]$Subject = 'shelf-reader')
    Write-CmLog "ALERT $Subject $Why" 'ERROR'
    try {
        if (Test-CmAlertIssuesEnabled -EnvMap $EnvMap -ProcessValue "$env:CM_ALERT_ISSUES") {
            Invoke-CmAlertRaise -Ctx $Ctx -Kind $Kind -Result (New-CmResult $Kind 0 $Why -Kind $Kind) -EnvMap $EnvMap
        }
    }
    catch { Write-CmLog "could not file the $Kind alert issue ($($_.Exception.Message)); the log line is still the alert" 'WARN' }
}

function Update-CmShelfReaderFiles {
    <#
      Brings the two model files into place, one step per pass and never waiting on a download. Returns @{ Ready; Note }:
      Ready when both are in place with the pinned size and SHA-256. A file already verified (same size and time as
      recorded) is not hashed again. A finished .partial is hashed and only then renamed; a bad one is deleted, alerted
      and not fetched again for a day. A download is alive while its .partial grows between passes (or it started less
      than CmShelfReaderStaleDownloadMinutes ago); one that stopped growing for that long is resumed, unless a curl is
      still writing the file.
    #>
    param($Ctx, [hashtable]$EnvMap, $Sr, [string]$Dir, [DateTime]$Now)
    $nowSec = ConvertTo-CmEpoch $Now
    $stale = $script:CmShelfReaderStaleDownloadMinutes * 60
    if (-not (Test-Path -LiteralPath $Dir -PathType Container)) { New-Item -ItemType Directory -Force -Path $Dir | Out-Null }
    $waiting = [System.Collections.Generic.List[string]]::new()
    $downloading = [System.Collections.Generic.List[string]]::new()
    foreach ($pin in Get-CmShelfReaderFiles) {
        $name = $pin.Name
        $final = Join-Path $Dir $name
        $partial = "$final.partial"
        $dl = $Sr['downloads'][$name]
        if ($dl -isnot [System.Collections.IDictionary]) { $dl = @{}; $Sr['downloads'][$name] = $dl }
        $bad = {
            param($path, $what)
            Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
            [void]$Sr['files'].Remove($name)
            $dl['badAt'] = $nowSec; $dl['startedAt'] = 0; $dl['lastSize'] = 0; $dl['grewAt'] = 0
            Write-CmShelfReaderAlert -Ctx $Ctx -EnvMap $EnvMap -Why "$name $what, so it was deleted and the reader stays off; the deployer fetches it again from the pinned revision in $($script:CmShelfReaderBadFileRetryHours) hours"
            $waiting.Add($name)
        }
        if (Test-Path -LiteralPath $final -PathType Leaf) {
            $item = Get-Item -LiteralPath $final
            $rec = $Sr['files'][$name]
            if ($item.Length -ne $pin.Size) { & $bad $final "is $($item.Length) bytes instead of $($pin.Size)"; continue }
            if ($rec -is [System.Collections.IDictionary] -and (ConvertTo-CmLong $rec['size']) -eq $item.Length -and
                (ConvertTo-CmLong $rec['ticks']) -eq $item.LastWriteTimeUtc.Ticks -and "$($rec['sha'])" -eq $pin.Sha256) { continue }
            if ((Get-CmFileSha256 $final) -ne $pin.Sha256) { & $bad $final 'does not match its pinned SHA-256'; continue }
            $Sr['files'][$name] = @{ size = $item.Length; ticks = $item.LastWriteTimeUtc.Ticks; sha = $pin.Sha256 }
            Write-CmLog "shelf reader: $name is in place and verified (SHA-256)"
            continue
        }
        $have = 0L
        if (Test-Path -LiteralPath $partial -PathType Leaf) {
            $have = (Get-Item -LiteralPath $partial).Length
            if ($have -eq $pin.Size) {
                if ((Get-CmFileSha256 $partial) -ne $pin.Sha256) { & $bad $partial 'was downloaded but does not match its pinned SHA-256'; continue }
                Move-Item -LiteralPath $partial -Destination $final -Force
                $item = Get-Item -LiteralPath $final
                $Sr['files'][$name] = @{ size = $item.Length; ticks = $item.LastWriteTimeUtc.Ticks; sha = $pin.Sha256 }
                $dl['startedAt'] = 0; $dl['lastSize'] = 0; $dl['grewAt'] = 0
                Write-CmLog "shelf reader: $name downloaded and verified (SHA-256), and renamed into place"
                continue
            }
            if ($have -gt $pin.Size) { Remove-Item -LiteralPath $partial -Force; $have = 0L }
            elseif ($have -ne (ConvertTo-CmLong $dl['lastSize'])) {
                # It grew (or this is the first look at it): a download is alive.
                $dl['lastSize'] = $have; $dl['grewAt'] = $nowSec
                $downloading.Add($name); continue
            }
            elseif (($nowSec - (ConvertTo-CmLong $dl['grewAt'])) -lt $stale) { $downloading.Add($name); continue }
        }
        $started = ConvertTo-CmLong $dl['startedAt']
        if ($started -gt 0 -and ($nowSec - $started) -lt $stale) { $downloading.Add($name); continue }
        $badAt = ConvertTo-CmLong $dl['badAt']
        if ($badAt -gt 0 -and ($nowSec - $badAt) -lt $script:CmShelfReaderBadFileRetryHours * 3600) { $waiting.Add($name); continue }
        if (Test-CmDownloadRunning -Partial $partial) { $downloading.Add($name); continue }
        $need = $pin.Size - $have
        $free = Get-CmFreeBytes $Dir
        if ($free -lt $need + $script:CmShelfReaderFreeMarginBytes) {
            $key = "low-space:$name"
            if ("$($Sr['lastAlert'])" -ne $key) {
                Write-CmShelfReaderAlert -Ctx $Ctx -EnvMap $EnvMap -Why "not enough free disk space to download $name ($([math]::Round($need / 1GB, 1)) GB needed plus $([math]::Round($script:CmShelfReaderFreeMarginBytes / 1GB)) GB kept free, $([math]::Round($free / 1GB, 1)) GB free in the models folder); the reader stays off until there is room"
                $Sr['lastAlert'] = $key
            }
            $waiting.Add($name)
            continue
        }
        $curl = if ($IsWindows) { 'curl.exe' } else { 'curl' }
        # -C - resumes a partial file; --connect-timeout and the bounded --retry end a download that cannot connect, and
        # --speed-limit/--speed-time one that stalls, so a dead download stops growing and a later pass resumes it.
        $arguments = @('-fsSL', '--connect-timeout', '30', '--retry', '5', '--retry-delay', '30', '--retry-max-time', '900',
            '--speed-limit', '1024', '--speed-time', '300', '-C', '-', '-o', $partial, (Get-CmShelfReaderFileUrl $name))
        try {
            Start-CmDetached -File $curl -ArgumentList $arguments | Out-Null
            $dl['startedAt'] = $nowSec; $dl['lastSize'] = $have; $dl['grewAt'] = $nowSec
            $resume = if ($have -gt 0) { " (resuming at $([math]::Round($have / 1MB)) MB)" } else { '' }
            Write-CmLog "shelf reader: started downloading $name in the background from revision $($script:CmShelfReaderRevision.Substring(0, 12))$resume"
            $downloading.Add($name)
        }
        catch {
            Write-CmShelfReaderAlert -Ctx $Ctx -EnvMap $EnvMap -Why "could not start the download of $name ($($_.Exception.Message)); the next poll tries again"
            $waiting.Add($name)
        }
    }
    if ($downloading.Count -eq 0 -and $waiting.Count -eq 0) { return [pscustomobject]@{ Ready = $true; Note = '' } }
    $note = if ($downloading.Count -gt 0) { "downloading $($downloading -join ' and ') in the background; the reader starts once the files verify" }
    else { "waiting for $($waiting -join ' and ') (see the ALERT shelf-reader line above)" }
    [pscustomobject]@{ Ready = $false; Note = $note }
}

function Get-CmShelfReaderStatus {
    <#
      The reader container as compose sees it: @{ Health; Id }. Health is healthy, starting, unhealthy, restarting,
      stopped, missing, or unknown (compose could not say); Id is the container's id (or its name), '' when there is none. Read only.
    #>
    param($RCtx, [string]$Tag)
    $status = { param($health, $id = '') [pscustomobject]@{ Health = $health; Id = "$id" } }
    $r = Invoke-Cm -Command 'docker' -Env @{ CM_TAG = $Tag } -Arguments (Get-CmGpuComposeArgs $RCtx @('ps', '-a', '--format', 'json', $script:CmShelfReaderService)) -TimeoutSeconds $script:CmShelfReaderCallSeconds
    if ($r.ExitCode -ne 0) { return & $status 'unknown' }
    $text = (@($r.Output) -join "`n").Trim()
    if (-not $text -or $text -eq '[]') { return & $status 'missing' }
    try {
        $rows = if ($text.StartsWith('[')) { @($text | ConvertFrom-Json) } else { @($text -split "`r?`n" | Where-Object { "$_".Trim() } | ForEach-Object { $_ | ConvertFrom-Json }) }
    }
    catch { return & $status 'unknown' }
    $row = @($rows | Where-Object { "$($_.Service)" -eq $script:CmShelfReaderService }) | Select-Object -First 1
    if (-not $row) { return & $status 'missing' }
    $id = if ("$($row.ID)".Trim()) { "$($row.ID)".Trim() } else { "$($row.Name)".Trim() }
    switch ("$($row.State)") {
        'running' {
            switch ("$($row.Health)") { 'healthy' { return & $status 'healthy' $id } 'unhealthy' { return & $status 'unhealthy' $id } default { return & $status 'starting' $id } }
        }
        'restarting' { return & $status 'restarting' $id }
        default { return & $status 'stopped' $id }
    }
}

function Get-CmShelfReaderHealth {
    <# The reader container's health alone (see Get-CmShelfReaderStatus). Read only. #>
    param($RCtx, [string]$Tag)
    (Get-CmShelfReaderStatus -RCtx $RCtx -Tag $Tag).Health
}

function Get-CmShelfReaderContainer {
    <#
      The running reader container's start time and the config hash compose stamped on it when it created it
      (com.docker.compose.config-hash): @{ StartedAt; ConfigHash }. $null when docker cannot say. Read only.
    #>
    param([string]$Id)
    if (-not $Id) { return $null }
    $r = Invoke-Cm -Command 'docker' -Arguments @('inspect', $Id, '--format', '{{.State.StartedAt}}|{{index .Config.Labels "com.docker.compose.config-hash"}}') -TimeoutSeconds $script:CmShelfReaderCallSeconds
    if ($r.ExitCode -ne 0) { return $null }
    $line = "$(@($r.Output) | Where-Object { "$_".Contains('|') } | Select-Object -First 1)".Trim()
    if (-not $line) { return $null }
    $parts = $line -split '\|', 2
    $hash = "$($parts[1])".Trim()
    if ($hash -eq '<no value>') { $hash = '' }
    [pscustomobject]@{ StartedAt = "$($parts[0])".Trim(); ConfigHash = $hash }
}

function Get-CmShelfReaderWantedHash {
    <#
      The config hash compose gives shelf-reader in the compose file at the deployed commit (`docker compose config
      --hash shelf-reader` prints "shelf-reader <hash>"), the same hash compose stamps on a container it creates from it.
      '' when compose cannot say. Read only.
    #>
    param($RCtx, [string]$Tag, [string]$Endpoint = '')
    $r = Invoke-Cm -Command 'docker' -Env @{ CM_TAG = $Tag; CM_SHELF_READER_ENDPOINT = $Endpoint } -Arguments (Get-CmGpuComposeArgs $RCtx @('config', '--hash', $script:CmShelfReaderService)) -TimeoutSeconds $script:CmShelfReaderCallSeconds
    if ($r.ExitCode -ne 0) { return '' }
    foreach ($l in @($r.Output)) { if ("$l".Trim() -match "^$([regex]::Escape($script:CmShelfReaderService))\s+(\S+)$") { return $Matches[1] } }
    ''
}

function Get-CmShelfReaderCardVerdict {
    <#
      Reads the start log of one container start (docker logs from its start time to the end of the start window, a few
      dozen lines) and says whether llama.cpp could use the graphics card: @{ Verdict = ok | bad | unknown; Line }. bad
      when the log has one of the "no card" markers (#802); unknown when the log cannot be read. Read only.
    #>
    param([string]$Id, [string]$StartedAt)
    $verdict = { param($v, $line = '') [pscustomobject]@{ Verdict = $v; Line = $line } }
    $start = $null
    try { $start = ConvertTo-CmUtc $StartedAt } catch { }
    if (-not $Id -or -not $start -or $start.Year -lt 2000) { return & $verdict 'unknown' }
    $since = ConvertTo-CmEpoch $start
    $until = $since + $script:CmShelfReaderStartMinutes * 60
    $r = Invoke-Cm -Command 'docker' -Arguments @('logs', '--since', "$since", '--until', "$until", $Id) -TimeoutSeconds $script:CmShelfReaderCallSeconds
    if ($r.ExitCode -ne 0) { return & $verdict 'unknown' }
    # Proof the window covers the start: llama-server prints this right after the point where the markers appear. A
    # reader that does use the card prints no positive line at this log level, so without it nothing is proven.
    if (-not (@($r.Output) | Where-Object { "$_" -match $script:CmShelfReaderStartedPattern })) { return & $verdict 'unknown' }
    $hit = @($r.Output) | Where-Object { "$_" -match $script:CmShelfReaderNoCardPattern } | Select-Object -First 1
    if ($null -ne $hit) {
        $line = ("$hit" -replace '[^\x20-\x7E]', ' ').Trim()
        if ($line.Length -gt 200) { $line = $line.Substring(0, 200) }
        return & $verdict 'bad' $line
    }
    & $verdict 'ok'
}

function Get-CmRunningApiEndpoint {
    <# The ShelfReader__Endpoint the running api container was started with ('' when it has none). $null when it cannot be read. Read only. #>
    param($RCtx, [string]$Tag)
    $ps = Invoke-Cm -Command 'docker' -Env @{ CM_TAG = $Tag } -Arguments (Get-CmComposeArgs $RCtx @('ps', '-q', 'api')) -TimeoutSeconds $script:CmShelfReaderCallSeconds
    if ($ps.ExitCode -ne 0) { return $null }
    $id = "$(@($ps.Output | Where-Object { "$_".Trim() } | Select-Object -First 1))".Trim()
    if (-not $id) { return $null }
    $r = Invoke-Cm -Command 'docker' -Arguments @('inspect', $id, '--format', '{{json .Config.Env}}') -TimeoutSeconds $script:CmShelfReaderCallSeconds
    if ($r.ExitCode -ne 0) { return $null }
    try { $vars = @((@($r.Output) -join "`n") | ConvertFrom-Json) } catch { return $null }
    foreach ($v in $vars) { if ("$v" -match '^ShelfReader__Endpoint=(.*)$') { return $Matches[1] } }
    ''
}

function Invoke-CmApiRestart {
    <# Restarts only the api (--no-deps: postgres, migrate and the reader are left alone) with this endpoint, then smoke-tests it. Counts toward the daily cap. True when it answers. #>
    param($Ctx, $RCtx, $Sr, [string]$Tag, [string]$Endpoint, [string]$Day)
    if ("$($Sr['apiRestartsDay'])" -ne $Day) { $Sr['apiRestartsDay'] = $Day; $Sr['apiRestarts'] = 0 }
    $Sr['apiRestarts'] = (ConvertTo-CmLong $Sr['apiRestarts']) + 1
    $up = Invoke-Cm -Mutating -Command 'docker' -Env @{ CM_TAG = $Tag; CM_SHELF_READER_ENDPOINT = $Endpoint } -Arguments (Get-CmComposeArgs $RCtx @('up', '-d', '--no-deps', 'api')) -TimeoutSeconds $script:CmShelfReaderUpSeconds
    $up.ExitCode -eq 0 -and (Invoke-CmSmokeTest -Ctx $Ctx)
}

function Invoke-CmShelfReader {
    <#
      One look at the shelf reader on a quiet poll (see the notes at the top of this file). Apply mode only. Never throws
      and never changes the pass's result. Status lines are logged when they change, not on every poll. The state file is
      read strictly and written back only when the reader's own part of it changed, and never when it cannot be read or
      holds no deployed commit, so this step can never leave the deployer a state it refuses to deploy from.
    #>
    param($Ctx)
    if (-not $global:CmApply) { return }
    $sr = $null
    $before = ''
    $script:CmReaderCtxCopy = $null
    try {
        $read = Read-CmStateStrict -Path $Ctx.StateFile
        if (-not $read.Ok) { return }
        $state = $read.State
        $tag = if ($state['current'] -is [System.Collections.IDictionary]) { "$($state['current']['sha'])" } else { '' }
        if ($tag -cnotmatch '^[0-9a-f]{40}$') { return }
        $envMap = Read-CmEnvFile -Path $Ctx.EnvFile
        $sr = Get-CmShelfReaderState -State $state
        $before = ConvertTo-CmCanonicalJson $state['shelfReader']
        $now = Get-CmNow
        $nowSec = ConvertTo-CmEpoch $now
        $day = $now.ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)

        $note = { param($text) if ("$($sr['note'])" -ne $text) { Write-CmLog "shelf reader: $text"; $sr['note'] = $text } }
        $inUse = { $sr['on'] -eq $true -or (ConvertTo-CmLong $sr['startedAt']) -gt 0 }
        $rctx = {
            if (-not $script:CmReaderCtxCopy) { $script:CmReaderCtxCopy = New-CmCtxAtCommit -Ctx $Ctx -Sha $tag -Purpose 'reader' }
            $script:CmReaderCtxCopy
        }
        $stop = {
            # Out of use: the container stopped (freeing the card) and the state off. The api is left alone: its
            # endpoint now points at nothing, it reports the reader unavailable, and the next deploy drops the endpoint.
            if (& $inUse) {
                $rc = & $rctx
                if (-not $rc) { $rc = $Ctx }
                Invoke-Cm -Mutating -Command 'docker' -Env @{ CM_TAG = $tag } -Arguments (Get-CmGpuComposeArgs $rc @('stop', $script:CmShelfReaderService)) -TimeoutSeconds $script:CmShelfReaderStopSeconds | Out-Null
            }
            $sr['on'] = $false; $sr['startedAt'] = 0; $sr['downSince'] = 0; $sr['cardMisses'] = 0
        }
        $fail = {
            param($why)
            $retry = $now.AddHours($script:CmShelfReaderRetryHours)
            Write-CmShelfReaderAlert -Ctx $Ctx -EnvMap $envMap -Why "$why; the reader was stopped, the app keeps running without it (nothing was rolled back), and the deployer tries again after $(Format-CmAlertTime $retry)"
            & $stop
            $sr['retryAt'] = ConvertTo-CmEpoch $retry
            $sr['note'] = "off after a failure until $(Format-CmAlertTime $retry)"
        }
        $clear = {
            param($why)
            try { if (Test-CmAlertIssuesEnabled -EnvMap $envMap -ProcessValue "$env:CM_ALERT_ISSUES") { Invoke-CmAlertClear -Ctx $Ctx -Why @{ 'shelf-reader-failed' = $why } } } catch { }
        }

        # 1. The owner's off switch. Nothing is pulled, downloaded or started; a running reader is stopped.
        if (Test-CmShelfReaderSwitchedOff -EnvMap $envMap) {
            if (& $inUse) { & $stop; Write-CmLog 'shelf reader: switched off; the reader is stopped, and the api reports it unavailable until the next deploy brings it up without the endpoint' }
            & $note 'off: CM_SHELF_READER=0 in .env, so nothing is pulled, downloaded or started'
            & $clear 'the reader was switched off on purpose'
            return
        }

        # 2. The card. A miss while the reader is in use changes nothing, until it has lasted CmShelfReaderCardMisses polls.
        if (-not (Test-CmNvidiaCard)) {
            if (& $inUse) {
                $sr['cardMisses'] = (ConvertTo-CmLong $sr['cardMisses']) + 1
                if ($sr['cardMisses'] -lt $script:CmShelfReaderCardMisses) { return }
                & $fail "nvidia-smi has not listed a graphics card for $($script:CmShelfReaderCardMisses) polls in a row"
                return
            }
            & $note 'off: no NVIDIA graphics card answered (nvidia-smi), so nothing is pulled, downloaded or started'
            return
        }
        $sr['cardMisses'] = 0

        # 3. The model files, in place and verified (downloads run in the background, never in this pass).
        $files = Update-CmShelfReaderFiles -Ctx $Ctx -EnvMap $envMap -Sr $sr -Dir (Get-CmModelsDir -Ctx $Ctx -EnvMap $envMap) -Now $now
        if (-not $files.Ready) {
            if (& $inUse) { & $stop; Write-CmLog 'shelf reader: a model file is not in place, so the reader was stopped' 'WARN' }
            & $note $files.Note
            return
        }
        if ("$($sr['lastAlert'])" -like 'low-space:*') { $sr['lastAlert'] = '' }

        # 4. After a failure, wait.
        if ((ConvertTo-CmLong $sr['retryAt']) -gt $nowSec) { return }

        $rc = & $rctx
        if (-not $rc) { Write-CmLog "shelf reader: could not read the compose file at $($tag.Substring(0, 12)) from the clone; the next poll tries again" 'WARN'; return }

        # 5. The container.
        $status = Get-CmShelfReaderStatus -RCtx $rc -Tag $tag
        $health = $status.Health
        if ($health -eq 'unknown') { return }
        $short = $tag.Substring(0, 12)
        $start = {
            $have = Invoke-Cm -Command 'docker' -Arguments @('image', 'inspect', $script:CmShelfReaderImage, '--format', '{{.Id}}') -TimeoutSeconds $script:CmShelfReaderCallSeconds
            if ($have.ExitCode -ne 0) {
                Write-CmLog "shelf reader: pulling $($script:CmShelfReaderImage) (about 2.6 GB, once)"
                $pull = Invoke-Cm -Mutating -Command 'docker' -Arguments @('pull', '--quiet', $script:CmShelfReaderImage) -TimeoutSeconds $script:CmShelfReaderPullSeconds
                if ($pull.ExitCode -ne 0) { return "could not pull the llama.cpp image (docker exited $($pull.ExitCode))" }
            }
            $up = Invoke-Cm -Mutating -Command 'docker' -Env @{ CM_TAG = $tag; CM_SHELF_READER_ENDPOINT = (Get-CmShelfReaderEndpoint -Ctx $Ctx) } -Arguments (Get-CmGpuComposeArgs $rc @('up', '-d', '--no-deps', $script:CmShelfReaderService)) -TimeoutSeconds $script:CmShelfReaderUpSeconds
            if ($up.ExitCode -ne 0) {
                $last = "$(@($up.Output) | Where-Object { "$_".Trim() } | Select-Object -Last 1)".Trim()
                if ($last.Length -gt 200) { $last = $last.Substring(0, 200) }
                return "could not start the shelf-reader container (docker exited $($up.ExitCode)$(if ($last) { ": $last" }))"
            }
            ''
        }

        # 6. A running reader must match the compose file at the deployed commit (#802): compose's config hash for the
        # service (asked with the same environment as the `up` below) against the hash compose stamped on the container.
        # Changed: recreated with the same `up`, at most once per deployed commit, and judged again from its new start
        # (start window, card check). Either hash unreadable skips only this decision; the rest of the step carries on.
        $box = $null
        if ($health -in 'healthy', 'starting', 'unhealthy') {
            $box = Get-CmShelfReaderContainer -Id $status.Id
            $want = if ($box -and $box.ConfigHash) { Get-CmShelfReaderWantedHash -RCtx $rc -Tag $tag -Endpoint (Get-CmShelfReaderEndpoint -Ctx $Ctx) } else { '' }
            if (-not $box -or -not $box.ConfigHash -or -not $want) {
                if ("$($sr['hashUnread'])" -ne $tag) {
                    $sr['hashUnread'] = $tag
                    Write-CmLog "shelf reader: whether the running reader matches the compose file at $short could not be read, so it is not recreated this poll" 'WARN'
                }
            }
            else {
                if ($sr.ContainsKey('hashUnread')) { [void]$sr.Remove('hashUnread') }
                if ($want -ne $box.ConfigHash) {
                    if ("$($sr['recreatedAt'])" -ne $tag) {
                        $sr['recreatedAt'] = $tag
                        $why = & $start
                        if ($why) { & $fail "could not recreate the reader for its changed definition at ${short}: $why"; return }
                        $sr['startedAt'] = $nowSec
                        if ($sr['on'] -eq $true) { $sr['downSince'] = $nowSec }
                        $keeps = if ($sr['on'] -eq $true) { '; the api keeps its endpoint and reports the reader unavailable while it loads' } else { '' }
                        Write-CmLog "shelf reader: its definition changed at $short, so the container was recreated; it is checked again once healthy$keeps"
                        return
                    }
                    if ("$($sr['stuckAt'])" -ne $tag) {
                        $sr['stuckAt'] = $tag
                        Write-CmLog "shelf reader: the container still does not match the compose file at $short after it was recreated once for this commit; it is left as it is until the next deployed commit" 'WARN'
                    }
                }
            }
        }
        # 7. The card inside Docker (#802): read once per container start, when it is first seen healthy, before the api
        # is given the endpoint. A start log that says the card is not usable is a failure. One that cannot be read (or does
        # not reach back to the start) waits a poll, but not past the start window counted from the container's start.
        $cardCheck = {
            if (-not $box) {
                # The start time cannot be read: a container already checked under this id passes, any other waits.
                if ($status.Id -and "$($sr['checkedStart'])".StartsWith("$($status.Id)|")) { return [pscustomobject]@{ Verdict = 'ok'; Line = '' } }
                return [pscustomobject]@{ Verdict = 'unknown'; Line = '' }
            }
            $key = "$($status.Id)|$($box.StartedAt)"
            if ("$($sr['checkedStart'])" -eq $key) { return [pscustomobject]@{ Verdict = 'ok'; Line = '' } }
            $v = Get-CmShelfReaderCardVerdict -Id $status.Id -StartedAt $box.StartedAt
            if ($v.Verdict -eq 'ok') { $sr['checkedStart'] = $key; Write-CmLog 'shelf reader: its start log shows llama.cpp starting with no sign that the graphics card is unusable' }
            $v
        }
        $unknownCard = {
            # True when the step must stop here: still inside the start window (wait a poll), or past it (a failure).
            param($noteText)
            $startSec = 0L
            if ($box) { try { $startSec = ConvertTo-CmEpoch (ConvertTo-CmUtc $box.StartedAt) } catch { $startSec = 0L } }
            if ($startSec -le 0) { $startSec = ConvertTo-CmLong $sr['startedAt'] }
            if ($startSec -le 0 -or ($nowSec - $startSec) -ge $script:CmShelfReaderStartMinutes * 60) {
                & $fail 'the reader''s start log could not be read, so the graphics card could not be checked'
                return
            }
            & $note $noteText
        }
        $noCard = { param($line) "the reader started but cannot use the graphics card inside Docker (its log says: $line)" }

        if ($sr['on'] -eq $true) {
            if ($health -eq 'healthy') {
                $card = & $cardCheck
                if ($card.Verdict -eq 'unknown') { & $unknownCard 'on, but the reader''s start log could not be read to check the graphics card; the next poll tries again'; return }
                if ($card.Verdict -eq 'bad') { & $fail (& $noCard $card.Line); return }
                $sr['downSince'] = 0; & $note 'on'; return
            }
            if ((ConvertTo-CmLong $sr['downSince']) -le 0) { $sr['downSince'] = $nowSec }
            if ($health -in 'missing', 'stopped') {
                $why = & $start
                if ($why) { & $fail $why; return }
            }
            if (($nowSec - (ConvertTo-CmLong $sr['downSince'])) -ge $script:CmShelfReaderDownMinutes * 60) {
                & $fail "the reader has not been healthy for $($script:CmShelfReaderDownMinutes) minutes (it is $health)"
                return
            }
            & $note "on, but the reader is $health; the api reports it unavailable until it answers again"
            return
        }

        switch ($health) {
            { $_ -in 'missing', 'stopped' } {
                if ((ConvertTo-CmLong $sr['startedAt']) -gt 0) { & $fail 'the reader stopped after it was started (see docker compose logs shelf-reader)'; return }
                $why = & $start
                if ($why) { & $fail $why; return }
                $sr['startedAt'] = $nowSec
                # Not waited for here: the next quiet poll looks at its health.
                Write-CmLog 'shelf reader: started the shelf-reader container; the api gets its endpoint once it is healthy'
                return
            }
            'restarting' { & $fail 'the reader keeps restarting (see docker compose logs shelf-reader)'; return }
            { $_ -in 'starting', 'unhealthy' } {
                # Inside the start window an unhealthy reader is still loading (compose start_period is the same 10 minutes).
                if ((ConvertTo-CmLong $sr['startedAt']) -le 0) { $sr['startedAt'] = $nowSec }
                if (($nowSec - (ConvertTo-CmLong $sr['startedAt'])) -ge $script:CmShelfReaderStartMinutes * 60) {
                    & $fail "the reader did not become healthy within $($script:CmShelfReaderStartMinutes) minutes of starting (it is $health)"
                }
                else { & $note 'starting; the api gets the endpoint once the reader is healthy' }
                return
            }
            'healthy' {
                if ((ConvertTo-CmLong $sr['startedAt']) -le 0) { $sr['startedAt'] = $nowSec }
                $card = & $cardCheck
                if ($card.Verdict -eq 'unknown') { & $unknownCard 'healthy, but its start log could not be read to check the graphics card; the next poll tries again'; return }
                if ($card.Verdict -eq 'bad') { & $fail (& $noCard $card.Line); return }
                $running = Get-CmRunningApiEndpoint -RCtx $rc -Tag $tag
                if ($null -eq $running) { & $note 'healthy, but the running api''s settings could not be read; the next poll tries again'; return }
                if ($running -ne $script:CmShelfReaderEndpointUrl) {
                    # The one reason this step restarts the api. Two restarts (the switch and its restore) must fit the day's cap.
                    $used = if ("$($sr['apiRestartsDay'])" -eq $day) { ConvertTo-CmLong $sr['apiRestarts'] } else { 0 }
                    if ($used + 2 -gt $script:CmShelfReaderApiRestartsPerDay) {
                        & $note "healthy, but the api was already restarted $used times today for the reader; it gets the endpoint tomorrow or with the next deploy"
                        return
                    }
                    if (-not (Invoke-CmApiRestart -Ctx $Ctx -RCtx $rc -Sr $sr -Tag $tag -Endpoint $script:CmShelfReaderEndpointUrl -Day $day)) {
                        $restored = Invoke-CmApiRestart -Ctx $Ctx -RCtx $rc -Sr $sr -Tag $tag -Endpoint '' -Day $day
                        if (-not $restored) {
                            Write-CmShelfReaderAlert -Ctx $Ctx -EnvMap $envMap -Kind 'shelf-reader-api-down' -Subject 'api' -Why 'the api failed its smoke test after a restart with the shelf reader''s endpoint, and again after a restart without it; it may be down'
                        }
                        & $fail "the api did not answer its smoke test with the reader's endpoint, so it was restarted without it$(if (-not $restored) { ' (and still did not answer)' })"
                        return
                    }
                }
                $sr['on'] = $true; $sr['downSince'] = 0; $sr['retryAt'] = 0; $sr['lastAlert'] = ''
                $sr['note'] = 'on'
                Write-CmLog "shelf reader: on; the api reads shelf photos through $($script:CmShelfReaderEndpointUrl)"
                & $clear 'the shelf reader is on and the api answers with it'
                return
            }
        }
    }
    catch { Write-CmLog "the shelf reader step failed ($($_.Exception.Message)); the deploy is not affected" 'WARN' }
    finally {
        try {
            if ($sr -and $global:CmApply -and (ConvertTo-CmCanonicalJson $sr) -ne $before) {
                $fresh = Read-CmStateStrict -Path $Ctx.StateFile
                $current = if ($fresh.Ok) { $fresh.State['current'] } else { $null }
                if ($current -is [System.Collections.IDictionary] -and "$($current['sha'])" -cmatch '^[0-9a-f]{40}$') {
                    $fresh.State['shelfReader'] = $sr
                    Write-CmState -Path $Ctx.StateFile -State $fresh.State
                }
                else { Write-CmLog 'the state file could not be read again, so the shelf reader''s state was not saved this poll' 'WARN' }
            }
        }
        catch { Write-CmLog "could not save the shelf reader's state ($($_.Exception.Message))" 'WARN' }
        if ($script:CmReaderCtxCopy) { Remove-CmCtxAtCommit -Ctx $Ctx -CommitCtx $script:CmReaderCtxCopy; $script:CmReaderCtxCopy = $null }
    }
}
