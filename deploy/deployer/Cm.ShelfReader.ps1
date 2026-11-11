# The shelf reader on the PC's graphics card (ADR-033, #795). Dot-sourced by Cm.Deploy.ps1 (needs Cm.Common.ps1 and
# Cm.Alerts.ps1).
#
# The rule that outranks every other one here: the reader can never take the app down or cause a rollback.
#   * It runs only after a pass that ended well (a good deploy, or a quiet poll), with the new version already proven
#     and recorded. Nothing here changes the pass's outcome or exit code, and nothing here throws.
#   * The deploy's own health check never looks at it (it is not in $Ctx.Services), and the api does not depend on it.
#   * The api learns the endpoint by being restarted with CM_SHELF_READER_ENDPOINT set, and only once the reader is
#     healthy. The state file remembers that (shelfReader.on), and Invoke-CmUp passes it on every later deploy and
#     rollback. While it is off (or until it is on) the endpoint is empty and the api behaves exactly as without a reader.
#   * A reader that fails (pull, start, health, a bad download, a full disk) is an `ALERT shelf-reader ...` line in the
#     log and a `shelf-reader-failed` alert issue, never a deploy alert or a heartbeat failure ping. The api is switched
#     back to an empty endpoint, the reader stopped, and the next try waits (CmShelfReaderRetryHours).
#   * A reader that stops answering while on is given CmShelfReaderDownMinutes (the api's reader client treats an
#     unreachable reader as "unavailable" meanwhile, and Docker restarts the container); after that it is switched off
#     the same way.
#   * The 6 GB of model files are downloaded in the background (a detached curl to <name>.partial, by the pinned
#     revision, resumable), never in the pass: a later pass sees the finished file, checks its size and SHA-256, and
#     only then renames it into place. A bad hash deletes it and waits a day before fetching again.
#   * CM_SHELF_READER=0 in .env, or a host where nvidia-smi lists no card: nothing is pulled, downloaded or started.
#Requires -Version 7.0

$script:CmShelfReaderService = 'shelf-reader'
$script:CmShelfReaderEndpointUrl = 'http://shelf-reader:8431'
$script:CmShelfReaderImage = 'ghcr.io/ggml-org/llama.cpp:server-cuda-b12282@sha256:bb29527ecb3da442d9459591c252351d957b6e3ee1635e8d0406c0a4442eb08c'
$script:CmShelfReaderRepo = 'Qwen/Qwen3-VL-8B-Instruct-GGUF'
$script:CmShelfReaderRevision = 'f982a07559d4a2f6c8744d840bf6fccab30eea96'
$script:CmShelfReaderPins = @(
    @{ Name = 'Qwen3VL-8B-Instruct-Q4_K_M.gguf'; Size = [long]5027784800; Sha256 = '67d1659bfe71b89d50b45a4ad1a9e5b997e5bb16ce5da66a6a6167abd569e9e2' }
    @{ Name = 'mmproj-Qwen3VL-8B-Instruct-F16.gguf'; Size = [long]1159029824; Sha256 = 'ca524100ebf825c9a870db1c580d03879e0da0ab2541697e2458e64891cf9d38' }
)
$script:CmShelfReaderStartMinutes = 10          # a started reader has this long to become healthy
$script:CmShelfReaderDownMinutes = 10           # a reader that was on may be unhealthy this long before the api is switched off
$script:CmShelfReaderRetryHours = 6             # after a failed pull, start or health, the next try waits this long
$script:CmShelfReaderBadFileRetryHours = 24     # after a file fails its SHA-256 check, the next download waits this long
$script:CmShelfReaderStaleDownloadMinutes = 15  # a .partial that has not grown for this long belongs to a download that died
$script:CmShelfReaderFreeMarginBytes = 10GB     # never fill the disk: this much stays free after a download
$script:CmShelfReaderPullSeconds = 900          # the 2.6 GB image pull, stopped if it hangs (well under the 30-minute stale lock; docker keeps the finished layers for the next try)

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

function Get-CmShelfReaderEndpoint {
    <# What the api's ShelfReader__Endpoint is: the reader's address once the deployer switched it on, else empty. #>
    param($Ctx)
    try {
        $sr = (Read-CmState -Path $Ctx.StateFile)['shelfReader']
        if ($sr -is [System.Collections.IDictionary] -and $sr['on'] -eq $true) { return $script:CmShelfReaderEndpointUrl }
    }
    catch { }
    ''
}

function Get-CmShelfReaderPlan {
    <# The reader's calls, for the dry-run plan. #>
    param($Ctx)
    $dir = '<models folder>'
    foreach ($f in Get-CmShelfReaderFiles) { "curl.exe (in the background) -fsSL -C - -o $dir\$($f.Name).partial $(Get-CmShelfReaderFileUrl $f.Name)" }
    Format-CmCommand 'docker' @('pull', '--quiet', $script:CmShelfReaderImage)
    Format-CmCommand 'docker' (Get-CmGpuComposeArgs $Ctx @('up', '-d', '--no-deps', $script:CmShelfReaderService)) @{ CM_TAG = '<deployed sha>' }
    Format-CmCommand 'docker' (Get-CmGpuComposeArgs $Ctx @('ps', '-a', '--format', 'json', $script:CmShelfReaderService)) @{ CM_TAG = '<deployed sha>' }
    Format-CmCommand 'docker' (Get-CmComposeArgs $Ctx @('up', '-d', '--no-deps', 'api')) @{ CM_TAG = '<deployed sha>'; CM_SHELF_READER_ENDPOINT = $script:CmShelfReaderEndpointUrl }
}

# ---- seams: the background start, free space and hashing ---------------------------------------------------------------

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

# ---- one look at the card, the files and the container -------------------------------------------------------------------

function Test-CmNvidiaCard {
    <# True when nvidia-smi answers and lists at least one card. Read only, stopped after 30 seconds. #>
    $r = Invoke-Cm -Command 'nvidia-smi' -Arguments @('-L') -TimeoutSeconds 30
    $r.ExitCode -eq 0 -and @(@($r.Output) | Where-Object { "$_" -match '^\s*GPU \d+:' }).Count -gt 0
}

function Write-CmShelfReaderAlert {
    <# An ALERT shelf-reader line (the deployer's alert convention) and the shelf-reader-failed issue. Never a deploy alert. #>
    param($Ctx, [hashtable]$EnvMap, [string]$Why)
    Write-CmLog "ALERT shelf-reader $Why" 'ERROR'
    try {
        if (Test-CmAlertIssuesEnabled -EnvMap $EnvMap -ProcessValue "$env:CM_ALERT_ISSUES") {
            Invoke-CmAlertRaise -Ctx $Ctx -Kind 'shelf-reader-failed' -Result (New-CmResult 'shelf-reader-failed' 0 $Why -Kind 'shelf-reader-failed') -EnvMap $EnvMap
        }
    }
    catch { Write-CmLog "could not file the shelf reader alert issue ($($_.Exception.Message)); the log line is still the alert" 'WARN' }
}

function Update-CmShelfReaderFiles {
    <#
      Brings the two model files into place, one step per pass and never waiting on a download. Returns @{ Ready; Note }:
      Ready when both are in place with the pinned size and SHA-256. A file already verified (same size and time as
      recorded) is not hashed again. A finished .partial is hashed and only then renamed; a bad one is deleted, alerted
      and not fetched again for a day. A missing file starts one background download, unless one is running.
    #>
    param($Ctx, [hashtable]$EnvMap, $Sr, [string]$Dir, [DateTime]$Now)
    $nowSec = ConvertTo-CmEpoch $Now
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
            $dl['badAt'] = $nowSec; $dl['startedAt'] = 0
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
            $item = Get-Item -LiteralPath $partial
            $have = $item.Length
            if ($have -eq $pin.Size) {
                if ((Get-CmFileSha256 $partial) -ne $pin.Sha256) { & $bad $partial 'was downloaded but does not match its pinned SHA-256'; continue }
                Move-Item -LiteralPath $partial -Destination $final -Force
                $item = Get-Item -LiteralPath $final
                $Sr['files'][$name] = @{ size = $item.Length; ticks = $item.LastWriteTimeUtc.Ticks; sha = $pin.Sha256 }
                $dl['startedAt'] = 0
                Write-CmLog "shelf reader: $name downloaded and verified (SHA-256), and renamed into place"
                continue
            }
            if ($have -gt $pin.Size) { Remove-Item -LiteralPath $partial -Force; $have = 0L }
            elseif (($Now - $item.LastWriteTimeUtc).TotalMinutes -lt $script:CmShelfReaderStaleDownloadMinutes) { $downloading.Add($name); continue }
        }
        $started = ConvertTo-CmLong $dl['startedAt']
        if ($started -gt 0 -and ($nowSec - $started) -lt $script:CmShelfReaderStaleDownloadMinutes * 60) { $downloading.Add($name); continue }
        $badAt = ConvertTo-CmLong $dl['badAt']
        if ($badAt -gt 0 -and ($nowSec - $badAt) -lt $script:CmShelfReaderBadFileRetryHours * 3600) { $waiting.Add($name); continue }
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
        # -C - resumes a partial file; --speed-limit/--speed-time end a stalled transfer, so a dead download goes stale
        # and the next pass resumes it instead of two writers sharing one file.
        $arguments = @('-fsSL', '--retry', '5', '--retry-delay', '30', '--speed-limit', '1024', '--speed-time', '300', '-C', '-', '-o', $partial, (Get-CmShelfReaderFileUrl $name))
        try {
            Start-CmDetached -File $curl -ArgumentList $arguments | Out-Null
            $dl['startedAt'] = $nowSec
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

function Get-CmShelfReaderHealth {
    <# The reader container as compose sees it: healthy, starting, unhealthy, restarting, stopped, missing, or unknown (compose could not say). Read only. #>
    param($RCtx, [string]$Tag)
    $r = Invoke-Cm -Command 'docker' -Env @{ CM_TAG = $Tag } -Arguments (Get-CmGpuComposeArgs $RCtx @('ps', '-a', '--format', 'json', $script:CmShelfReaderService))
    if ($r.ExitCode -ne 0) { return 'unknown' }
    $text = (@($r.Output) -join "`n").Trim()
    if (-not $text -or $text -eq '[]') { return 'missing' }
    try {
        $rows = if ($text.StartsWith('[')) { @($text | ConvertFrom-Json) } else { @($text -split "`r?`n" | Where-Object { "$_".Trim() } | ForEach-Object { $_ | ConvertFrom-Json }) }
    }
    catch { return 'unknown' }
    $row = @($rows | Where-Object { "$($_.Service)" -eq $script:CmShelfReaderService }) | Select-Object -First 1
    if (-not $row) { return 'missing' }
    switch ("$($row.State)") {
        'running' {
            switch ("$($row.Health)") { 'healthy' { return 'healthy' } 'unhealthy' { return 'unhealthy' } default { return 'starting' } }
        }
        'restarting' { return 'restarting' }
        default { return 'stopped' }
    }
}

function Set-CmApiShelfReaderEndpoint {
    <# Restarts only the api (--no-deps: postgres, migrate and the reader are left alone) with the endpoint, then smoke-tests it. True when it answers. #>
    param($Ctx, $RCtx, [string]$Tag, [string]$Endpoint)
    $up = Invoke-Cm -Mutating -Command 'docker' -Env @{ CM_TAG = $Tag; CM_SHELF_READER_ENDPOINT = $Endpoint } -Arguments (Get-CmComposeArgs $RCtx @('up', '-d', '--no-deps', 'api'))
    $up.ExitCode -eq 0 -and (Invoke-CmSmokeTest -Ctx $Ctx)
}

function Stop-CmShelfReaderUse {
    <# Takes the reader out of use: the api back to an empty endpoint (if it had one) and the container stopped (if one was started), freeing the card. #>
    param($Ctx, $RCtx, $Sr, [string]$Tag)
    if (-not $RCtx) { $RCtx = $Ctx }   # the deployed commit's compose file could not be read: the clone's will do to stop it
    if ($Sr['on'] -eq $true) {
        if (-not (Set-CmApiShelfReaderEndpoint -Ctx $Ctx -RCtx $RCtx -Tag $Tag -Endpoint '')) {
            Write-CmLog 'the api did not answer its smoke test after the shelf reader was switched off; the next deploy checks it again' 'ERROR'
        }
    }
    if ($Sr['on'] -eq $true -or (ConvertTo-CmLong $Sr['startedAt']) -gt 0) {
        Invoke-Cm -Mutating -Command 'docker' -Env @{ CM_TAG = $Tag } -Arguments (Get-CmGpuComposeArgs $RCtx @('stop', $script:CmShelfReaderService)) -TimeoutSeconds 120 | Out-Null
    }
    $Sr['on'] = $false; $Sr['startedAt'] = 0; $Sr['downSince'] = 0
}

function Invoke-CmShelfReader {
    <#
      One look at the shelf reader after a pass that ended well (see the notes at the top of this file). Apply mode only.
      Never throws and never changes the pass's result. Status lines are logged when they change, not on every poll.
    #>
    param($Ctx)
    if (-not $global:CmApply) { return }
    $rctx = $null
    try {
        $envMap = Read-CmEnvFile -Path $Ctx.EnvFile
        $state = Read-CmState -Path $Ctx.StateFile
        $tag = if ($state['current'] -is [System.Collections.IDictionary]) { "$($state['current']['sha'])" } else { '' }
        if ($tag -cnotmatch '^[0-9a-f]{40}$') { return }
        $sr = Get-CmShelfReaderState -State $state
        $now = Get-CmNow
        $nowSec = ConvertTo-CmEpoch $now
        $note = {
            param($text)
            if ("$($sr['note'])" -ne $text) { Write-CmLog "shelf reader: $text"; $sr['note'] = $text }
        }
        $context = {
            if (-not $script:rctxHolder) { $script:rctxHolder = New-CmCtxAtCommit -Ctx $Ctx -Sha $tag }
            $script:rctxHolder
        }
        $script:rctxHolder = $null
        $fail = {
            param($why, [double]$hours = $script:CmShelfReaderRetryHours)
            $retry = $now.AddHours($hours)
            Write-CmShelfReaderAlert -Ctx $Ctx -EnvMap $envMap -Why "$why; the app keeps running without it (nothing was rolled back) and the deployer tries again after $(Format-CmAlertTime $retry)"
            Stop-CmShelfReaderUse -Ctx $Ctx -RCtx (& $context) -Sr $sr -Tag $tag
            $sr['retryAt'] = ConvertTo-CmEpoch $retry
            $sr['note'] = "off after a failure until $(Format-CmAlertTime $retry)"
        }
        $clear = {
            param($why)
            try { if (Test-CmAlertIssuesEnabled -EnvMap $envMap -ProcessValue "$env:CM_ALERT_ISSUES") { Invoke-CmAlertClear -Ctx $Ctx -Why @{ 'shelf-reader-failed' = $why } } } catch { }
        }

        # 1. The owner's off switch, then the card. Either way nothing is pulled, downloaded or started.
        $off = if (Test-CmShelfReaderSwitchedOff -EnvMap $envMap) { 'off: CM_SHELF_READER=0 in .env, so nothing is pulled, downloaded or started' }
        elseif (-not (Test-CmNvidiaCard)) { 'off: no NVIDIA graphics card answered (nvidia-smi), so nothing is pulled, downloaded or started' }
        else { '' }
        if ($off) {
            if ($sr['on'] -eq $true -or (ConvertTo-CmLong $sr['startedAt']) -gt 0) {
                $rc = & $context
                if ($rc) { Stop-CmShelfReaderUse -Ctx $Ctx -RCtx $rc -Sr $sr -Tag $tag; Write-CmLog 'shelf reader: switched off; the api runs without it' }
            }
            & $note $off
            & $clear 'the reader was switched off on purpose'
            return
        }

        # 2. The model files, in place and verified (downloads run in the background, never in this pass).
        $files = Update-CmShelfReaderFiles -Ctx $Ctx -EnvMap $envMap -Sr $sr -Dir (Get-CmModelsDir -Ctx $Ctx -EnvMap $envMap) -Now $now
        if (-not $files.Ready) {
            if ($sr['on'] -eq $true -or (ConvertTo-CmLong $sr['startedAt']) -gt 0) {
                $rc = & $context
                if ($rc) { Stop-CmShelfReaderUse -Ctx $Ctx -RCtx $rc -Sr $sr -Tag $tag }
            }
            & $note $files.Note
            return
        }
        if ("$($sr['lastAlert'])" -like 'low-space:*') { $sr['lastAlert'] = '' }

        # 3. After a failure, wait.
        if ((ConvertTo-CmLong $sr['retryAt']) -gt $nowSec) { return }

        $rctx = & $context
        if (-not $rctx) { Write-CmLog "shelf reader: could not read the compose file at $($tag.Substring(0, 12)) from the clone; the next poll tries again" 'WARN'; return }

        # 4. The container.
        $health = Get-CmShelfReaderHealth -RCtx $rctx -Tag $tag
        if ($health -eq 'unknown') { return }
        $endpoint = Get-CmShelfReaderEndpoint -Ctx $Ctx
        $start = {
            $have = Invoke-Cm -Command 'docker' -Arguments @('image', 'inspect', $script:CmShelfReaderImage, '--format', '{{.Id}}')
            if ($have.ExitCode -ne 0) {
                Write-CmLog "shelf reader: pulling $($script:CmShelfReaderImage) (about 2.6 GB, once)"
                $pull = Invoke-Cm -Mutating -Command 'docker' -Arguments @('pull', '--quiet', $script:CmShelfReaderImage) -TimeoutSeconds $script:CmShelfReaderPullSeconds
                if ($pull.ExitCode -ne 0) { return "could not pull the llama.cpp image (docker exited $($pull.ExitCode))" }
            }
            $up = Invoke-Cm -Mutating -Command 'docker' -Env @{ CM_TAG = $tag; CM_SHELF_READER_ENDPOINT = $endpoint } -Arguments (Get-CmGpuComposeArgs $rctx @('up', '-d', '--no-deps', $script:CmShelfReaderService)) -TimeoutSeconds 300
            if ($up.ExitCode -ne 0) {
                $last = "$(@($up.Output) | Where-Object { "$_".Trim() } | Select-Object -Last 1)".Trim()
                if ($last.Length -gt 200) { $last = $last.Substring(0, 200) }
                return "could not start the shelf-reader container (docker exited $($up.ExitCode)$(if ($last) { ": $last" }))"
            }
            ''
        }

        if ($sr['on'] -eq $true) {
            if ($health -eq 'healthy') { $sr['downSince'] = 0; & $note 'on'; return }
            if ((ConvertTo-CmLong $sr['downSince']) -le 0) { $sr['downSince'] = $nowSec }
            if ($health -in 'missing', 'stopped') {
                $why = & $start
                if ($why) { & $fail $why; return }
            }
            if (($nowSec - (ConvertTo-CmLong $sr['downSince'])) -ge $script:CmShelfReaderDownMinutes * 60) {
                & $fail "the reader has not been healthy for $($script:CmShelfReaderDownMinutes) minutes (it is $health), so the api was switched back to no reader"
                return
            }
            & $note "on, but the reader is $health; the api treats it as unavailable until it answers again"
            return
        }

        if ($health -in 'missing', 'stopped') {
            $why = & $start
            if ($why) { & $fail $why; return }
            $sr['startedAt'] = $nowSec
            Write-CmLog 'shelf reader: started the shelf-reader container; the api gets its endpoint once it is healthy'
            for ($i = 1; $i -le [math]::Max(1, $Ctx.SmokeRetries); $i++) {
                $health = Get-CmShelfReaderHealth -RCtx $rctx -Tag $tag
                if ($health -ne 'starting') { break }
                if ($i -lt $Ctx.SmokeRetries) { Start-CmSleep -Seconds $Ctx.SmokeDelaySeconds }
            }
        }
        elseif ((ConvertTo-CmLong $sr['startedAt']) -le 0) { $sr['startedAt'] = $nowSec }

        switch ($health) {
            'healthy' {
                if (Set-CmApiShelfReaderEndpoint -Ctx $Ctx -RCtx $rctx -Tag $tag -Endpoint $script:CmShelfReaderEndpointUrl) {
                    $sr['on'] = $true; $sr['downSince'] = 0; $sr['retryAt'] = 0; $sr['lastAlert'] = ''
                    $sr['note'] = 'on'
                    Write-CmLog "shelf reader: on; the api reads shelf photos through $($script:CmShelfReaderEndpointUrl)"
                    & $clear 'the shelf reader is on and the api answers with it'
                }
                else {
                    $sr['on'] = $true   # so the switch back below restarts the api with the endpoint empty
                    & $fail 'the api did not answer its smoke test with the reader''s endpoint, so it was switched back to no reader'
                }
            }
            'starting' {
                if (($nowSec - (ConvertTo-CmLong $sr['startedAt'])) -ge $script:CmShelfReaderStartMinutes * 60) {
                    & $fail "the reader did not become healthy within $($script:CmShelfReaderStartMinutes) minutes of starting"
                }
                else { & $note 'starting; the api gets the endpoint once the reader is healthy' }
            }
            default { & $fail "the reader is $health after starting (see docker compose logs shelf-reader)" }
        }
    }
    catch { Write-CmLog "the shelf reader step failed ($($_.Exception.Message)); the deploy is not affected" 'WARN' }
    finally {
        try {
            if ($sr -and $global:CmApply) {
                $fresh = Read-CmState -Path $Ctx.StateFile
                $fresh['shelfReader'] = $sr
                Write-CmState -Path $Ctx.StateFile -State $fresh
            }
        }
        catch { Write-CmLog "could not save the shelf reader's state ($($_.Exception.Message))" 'WARN' }
        if ($script:rctxHolder) { Remove-CmCtxAtCommit -Ctx $Ctx -CommitCtx $script:rctxHolder; $script:rctxHolder = $null }
    }
}
