# Copies the newest .ipa from the latest GitHub release into the files volume (under ipa/), checksum verified, with the release's version beside it.
# Dot-sourced by Sync-Ipa.ps1 and by the deployer (its last step). Needs Cm.Common.ps1.
#Requires -Version 7.0

# A release tag that is safe to hand to `gh` as an argument (never starts with '-') and a tag that reads as a version (1.2.3, v1.2.3, 1.2.3-rc.1).
$script:CmReleaseTagPattern = '^[0-9A-Za-z][0-9A-Za-z._+-]*$'
$script:CmVersionPattern = '^v?(\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?)$'

function Get-CmIpaCopyScript {
    <# The helper container's script. It reads fixed names only, so nothing from a release reaches a shell. version.json is moved last. #>
    param([bool]$WithVersion)
    $dir = '/data/files/ipa'
    $staged = @("$dir/.new.ipa", "$dir/.new.sha256")
    $parts = @('set -eu', "mkdir -p $dir", "cp /in/app.ipa $dir/.new.ipa", "cp /in/app.sha256 $dir/.new.sha256")
    if ($WithVersion) {
        $parts += "cp /in/app.version.json $dir/.new.version.json"
        $staged += "$dir/.new.version.json"
    }
    $parts += "chmod 644 $($staged -join ' ')"
    $parts += "mv -f $dir/.new.ipa $dir/CollectionMaxxing.ipa"
    $parts += "mv -f $dir/.new.sha256 $dir/CollectionMaxxing.ipa.sha256"
    # A release whose tag is not a version leaves no stale version file behind.
    $parts += if ($WithVersion) { "mv -f $dir/.new.version.json $dir/version.json" } else { "rm -f $dir/version.json" }
    $parts -join '; '
}

function Get-CmLatestReleaseTag {
    <# One read-only `gh release view`: the tag of the latest release, or $null when it cannot be read or is not a safe argument for `gh`. #>
    param($Ctx)
    $r = Invoke-Cm -Command 'gh' -Arguments @('release', 'view', '--repo', $Ctx.Repo, '--json', 'tagName', '--jq', '.tagName')
    $tag = if ($r.ExitCode -eq 0) { @($r.Output | ForEach-Object { "$_".Trim() } | Where-Object { $_ }) | Select-Object -First 1 } else { $null }
    if ($tag -and $tag -match $script:CmReleaseTagPattern) { $tag } else { $null }
}

function Invoke-CmIpaSync {
    <#
      Returns @{ Outcome; ExitCode }. Outcomes: dry-run, no-release, no-checksum, checksum-mismatch, up-to-date, synced, copy-failed.
      A checksum problem leaves the file that is already served exactly as it was.
      The release tag becomes ipa/version.json ({"version":"1.0.2","sha256":"..."}): the iPhone app compares it with its own version.
      The deployer state remembers the release it last synced (ipa.tag) and, after a sync that did not work, the attempt
      (ipaAttempt: tag, outcome, time) so a quiet run can tell a new problem from one it has already reported.
      -Release names a tag the caller has already read (no second `gh release view`); -Quiet logs nothing for a failure (a silent retry).
    #>
    param($Ctx, [string]$Tag = '', [string]$Release = '', [switch]$Quiet)
    function Result($outcome, $code) { [pscustomobject]@{ Outcome = $outcome; ExitCode = $code } }
    $log = { param($message, $level = 'INFO') if (-not $Quiet) { Write-CmLog $message $level } }

    $state = Read-CmState -Path $Ctx.StateFile
    if (-not $Tag -and $state['current']) { $Tag = $state['current']['sha'] }
    if (-not $Tag) { $Tag = '<sha>' }
    $helperImage = Get-CmImageRef $Ctx 'backup' $Tag   # the backup image has a shell; the api image does not.
    $stage = $Ctx.StageDir

    $view = @('release', 'view', '--repo', $Ctx.Repo, '--json', 'tagName', '--jq', '.tagName')
    $downloadArgs = { param($release) @('release', 'download', $release, '--repo', $Ctx.Repo, '--pattern', '*.ipa', '--pattern', '*.sha256', '--dir', $stage, '--clobber') }
    $copyArgs = {
        param($withVersion)
        @('run', '--rm', '--network', 'none', '-v', 'collectionmaxxing_files:/data/files', '-v', "${stage}:/in:ro",
            '--entrypoint', 'sh', $helperImage, '-c', (Get-CmIpaCopyScript -WithVersion $withVersion))
    }

    if (-not $global:CmApply) {
        Write-CmLog 'DRY RUN (.ipa sync): would run, in order:'
        Write-CmLog "  $(Format-CmCommand 'gh' $view)"
        Write-CmLog "  $(Format-CmCommand 'gh' (& $downloadArgs '<latest release tag>'))"
        Write-CmLog '  (verify the SHA-256 of the .ipa against the release checksum file; a mismatch stops here)'
        Write-CmLog '  (write app.version.json from the release tag: the version the iPhone app compares with its own)'
        Write-CmLog "  $(Format-CmCommand 'docker' (& $copyArgs $true))"
        return Result 'dry-run' 0
    }

    # Name the release once, then download exactly that one, so a release made in between cannot give us a version and an .ipa from two different ones.
    $release = if ($Release) { $Release } else { Get-CmLatestReleaseTag -Ctx $Ctx }
    if (-not $release -or $release -notmatch $script:CmReleaseTagPattern) {
        & $log 'could not read a usable tag from the latest release; nothing copied'
        return Result 'no-release' 0
    }
    $version = if ($release -match $script:CmVersionPattern) { $Matches[1] } else { $null }
    # A sync that did not work is remembered per tag; a good one forgets it.
    $failed = {
        param($outcome, $code)
        $state['ipaAttempt'] = @{ tag = $release; outcome = $outcome; at = [DateTime]::UtcNow.ToString('o') }
        Write-CmState -Path $Ctx.StateFile -State $state
        Result $outcome $code
    }

    # Only ever clean the staging folder inside our own root.
    $rootFull = [IO.Path]::GetFullPath($Ctx.Root)
    $stageFull = [IO.Path]::GetFullPath($stage)
    if (-not $stageFull.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) { throw "staging folder $stageFull is outside $rootFull" }
    if (Test-Path -LiteralPath $stageFull) { Remove-Item -LiteralPath $stageFull -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stageFull | Out-Null

    $dl = Invoke-Cm -Mutating -Command 'gh' -Arguments (& $downloadArgs $release)
    $ipa = @(Get-ChildItem -LiteralPath $stageFull -Filter '*.ipa' -File -ErrorAction SilentlyContinue | Sort-Object Name) | Select-Object -Last 1
    if ($dl.ExitCode -ne 0 -or -not $ipa) {
        & $log "no .ipa found on release $release yet; nothing copied, will look again"
        return (& $failed 'no-release' 0)
    }
    $sumFile = @(Get-ChildItem -LiteralPath $stageFull -Filter '*.sha256' -File -ErrorAction SilentlyContinue) | Select-Object -First 1
    $expected = if ($sumFile) { ([regex]::Match((Get-Content -LiteralPath $sumFile.FullName -Raw), '(?i)\b[0-9a-f]{64}\b')).Value.ToLowerInvariant() } else { '' }
    if (-not $expected) {
        & $log "release $release has no usable SHA-256 file for $($ipa.Name); keeping the current .ipa" 'ERROR'
        return (& $failed 'no-checksum' 1)
    }
    $actual = (Get-FileHash -LiteralPath $ipa.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) {
        & $log "checksum mismatch for $($ipa.Name) from release $release; keeping the current .ipa" 'ERROR'
        return (& $failed 'checksum-mismatch' 1)
    }
    # Up to date only when the same file is already there under the same version; the first sync after this feature shipped has no version yet.
    if ($state['ipa'] -and $state['ipa']['sha256'] -eq $actual -and $state['ipa']['version'] -eq $version) {
        # Nothing to copy, but remember this release so a quiet run does not download it again.
        if ("$($state['ipa']['tag'])" -ne $release -or $state.ContainsKey('ipaAttempt')) {
            $state['ipa']['tag'] = $release
            $state.Remove('ipaAttempt')
            Write-CmState -Path $Ctx.StateFile -State $state
        }
        return Result 'up-to-date' 0
    }

    # The helper reads fixed names, so nothing from the release asset name reaches a shell.
    Copy-Item -LiteralPath $ipa.FullName -Destination (Join-Path $stageFull 'app.ipa') -Force
    Set-Content -LiteralPath (Join-Path $stageFull 'app.sha256') -Value "$actual  CollectionMaxxing.ipa" -Encoding ascii
    if ($version) {
        # The checksum binds the version to this exact .ipa: the API ignores a version file whose checksum is not the served file's.
        $json = [ordered]@{ version = $version; sha256 = $actual } | ConvertTo-Json -Compress
        Set-Content -LiteralPath (Join-Path $stageFull 'app.version.json') -Value $json -Encoding ascii
    }
    $r = Invoke-Cm -Mutating -Command 'docker' -Arguments (& $copyArgs ([bool]$version))
    if ($r.ExitCode -ne 0) {
        & $log 'copying the .ipa into the files volume failed; the previous one is unchanged' 'ERROR'
        return (& $failed 'copy-failed' 1)
    }
    $recorded = @{ sha256 = $actual; file = $ipa.Name; syncedAt = [DateTime]::UtcNow.ToString('o'); tag = $release }
    if ($version) { $recorded['version'] = $version }
    $state['ipa'] = $recorded
    $state.Remove('ipaAttempt')
    Write-CmState -Path $Ctx.StateFile -State $state
    Write-CmLog "synced $($ipa.Name)$(if ($version) { " (version $version)" }) into the files volume under ipa/"
    Result 'synced' 0
}

function Invoke-CmIpaCatchUp {
    <#
      For a run with nothing to deploy: sync the .ipa when the latest release is one this server has not synced yet. A
      release is published by the iOS build some time after the commit's images were deployed, so waiting for the next
      deploy would leave the previous .ipa (and version) served for days. Costs one `gh release view` per run.
      Returns @{ Outcome; ExitCode; Detail } with Outcome none (nothing to do, or nothing to report), ipa-synced, or
      ipa-failed (exit 5). A failed attempt is reported once per release tag: the same tag is looked at again after
      IpaRetryMinutes, silently, so a transient problem heals itself without a new alert every two minutes. A release
      that has no .ipa yet is not a failure (it is logged once and looked at again later).
    #>
    param($Ctx)
    function Answer($outcome, $code, $detail = '') { [pscustomobject]@{ Outcome = $outcome; ExitCode = $code; Detail = $detail } }

    $release = Get-CmLatestReleaseTag -Ctx $Ctx
    if (-not $release) { return Answer 'none' 0 }   # GitHub unreachable or nothing published: say nothing, ask again next run
    $state = Read-CmState -Path $Ctx.StateFile
    if ($state['ipa'] -and "$($state['ipa']['tag'])" -eq $release) { return Answer 'none' 0 }

    $prior = $state['ipaAttempt']
    $retry = [bool]($prior -and "$($prior['tag'])" -eq $release)
    if ($retry) {
        $at = try { ConvertTo-CmUtc $prior['at'] } catch { $null }
        if ($at -and ([DateTime]::UtcNow - $at).TotalMinutes -lt $Ctx.IpaRetryMinutes) { return Answer 'none' 0 }
    }

    $sync = Invoke-CmIpaSync -Ctx $Ctx -Release $release -Quiet:$retry
    if ($sync.ExitCode -ne 0) {
        if ($retry) { return Answer 'none' 0 }   # already reported for this tag
        return Answer 'ipa-failed' 5 "release ${release}: $($sync.Outcome)"
    }
    if ($sync.Outcome -eq 'synced') { return Answer 'ipa-synced' 0 }
    Answer 'none' 0
}
