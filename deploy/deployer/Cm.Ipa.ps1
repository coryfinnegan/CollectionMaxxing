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

function Invoke-CmIpaSync {
    <#
      Returns @{ Outcome; ExitCode }. Outcomes: dry-run, no-release, no-checksum, checksum-mismatch, up-to-date, synced, copy-failed.
      A checksum problem leaves the file that is already served exactly as it was.
      The release tag becomes ipa/version.json ({"version":"1.0.2","sha256":"..."}): the iPhone app compares it with its own version.
    #>
    param($Ctx, [string]$Tag = '')
    function Result($outcome, $code) { [pscustomobject]@{ Outcome = $outcome; ExitCode = $code } }

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
    $viewed = Invoke-Cm -Command 'gh' -Arguments $view
    $release = if ($viewed.ExitCode -eq 0) { @($viewed.Output | ForEach-Object { "$_".Trim() } | Where-Object { $_ }) | Select-Object -First 1 } else { $null }
    if (-not $release -or $release -notmatch $script:CmReleaseTagPattern) {
        Write-CmLog 'could not read a usable tag from the latest release; nothing copied' 'INFO'
        return Result 'no-release' 0
    }
    $version = if ($release -match $script:CmVersionPattern) { $Matches[1] } else { $null }

    # Only ever clean the staging folder inside our own root.
    $rootFull = [IO.Path]::GetFullPath($Ctx.Root)
    $stageFull = [IO.Path]::GetFullPath($stage)
    if (-not $stageFull.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) { throw "staging folder $stageFull is outside $rootFull" }
    if (Test-Path -LiteralPath $stageFull) { Remove-Item -LiteralPath $stageFull -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stageFull | Out-Null

    $dl = Invoke-Cm -Mutating -Command 'gh' -Arguments (& $downloadArgs $release)
    $ipa = @(Get-ChildItem -LiteralPath $stageFull -Filter '*.ipa' -File -ErrorAction SilentlyContinue | Sort-Object Name) | Select-Object -Last 1
    if ($dl.ExitCode -ne 0 -or -not $ipa) {
        Write-CmLog 'no .ipa found on the latest release; nothing copied' 'INFO'
        return Result 'no-release' 0
    }
    $sumFile = @(Get-ChildItem -LiteralPath $stageFull -Filter '*.sha256' -File -ErrorAction SilentlyContinue) | Select-Object -First 1
    $expected = if ($sumFile) { ([regex]::Match((Get-Content -LiteralPath $sumFile.FullName -Raw), '(?i)\b[0-9a-f]{64}\b')).Value.ToLowerInvariant() } else { '' }
    if (-not $expected) {
        Write-CmLog "release has no usable SHA-256 file for $($ipa.Name); keeping the current .ipa" 'ERROR'
        return Result 'no-checksum' 1
    }
    $actual = (Get-FileHash -LiteralPath $ipa.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) {
        Write-CmLog "checksum mismatch for $($ipa.Name); keeping the current .ipa" 'ERROR'
        return Result 'checksum-mismatch' 1
    }
    # Up to date only when the same file is already there under the same version; the first sync after this feature shipped has no version yet.
    if ($state['ipa'] -and $state['ipa']['sha256'] -eq $actual -and $state['ipa']['version'] -eq $version) { return Result 'up-to-date' 0 }

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
        Write-CmLog 'copying the .ipa into the files volume failed; the previous one is unchanged' 'ERROR'
        return Result 'copy-failed' 1
    }
    $recorded = @{ sha256 = $actual; file = $ipa.Name; syncedAt = [DateTime]::UtcNow.ToString('o') }
    if ($version) { $recorded['version'] = $version }
    $state['ipa'] = $recorded
    Write-CmState -Path $Ctx.StateFile -State $state
    Write-CmLog "synced $($ipa.Name)$(if ($version) { " (version $version)" }) into the files volume under ipa/"
    Result 'synced' 0
}
