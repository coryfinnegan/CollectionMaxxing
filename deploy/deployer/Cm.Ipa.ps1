# Copies the newest .ipa from the latest GitHub release into the files volume (under ipa/), checksum verified.
# Dot-sourced by Sync-Ipa.ps1 and by the deployer (its last step). Needs Cm.Common.ps1.
#Requires -Version 7.0

function Invoke-CmIpaSync {
    <#
      Returns @{ Outcome; ExitCode }. Outcomes: dry-run, no-release, no-checksum, checksum-mismatch, up-to-date, synced, copy-failed.
      A checksum problem leaves the file that is already served exactly as it was.
    #>
    param($Ctx, [string]$Tag = '')
    function Result($outcome, $code) { [pscustomobject]@{ Outcome = $outcome; ExitCode = $code } }

    $state = Read-CmState -Path $Ctx.StateFile
    if (-not $Tag -and $state['current']) { $Tag = $state['current']['sha'] }
    if (-not $Tag) { $Tag = '<sha>' }
    $helperImage = Get-CmImageRef $Ctx 'backup' $Tag   # the backup image has a shell; the api image does not.
    $stage = $Ctx.StageDir

    $download = @('release', 'download', '--repo', $Ctx.Repo, '--pattern', '*.ipa', '--pattern', '*.sha256', '--dir', $stage, '--clobber')
    $copyScript = 'set -eu; mkdir -p /data/files/ipa; ' +
    'cp /in/app.ipa /data/files/ipa/.new.ipa; cp /in/app.sha256 /data/files/ipa/.new.sha256; ' +
    'chmod 644 /data/files/ipa/.new.ipa /data/files/ipa/.new.sha256; ' +
    'mv -f /data/files/ipa/.new.ipa /data/files/ipa/CollectionMaxxing.ipa; ' +
    'mv -f /data/files/ipa/.new.sha256 /data/files/ipa/CollectionMaxxing.ipa.sha256'
    $copy = @('run', '--rm', '--network', 'none', '-v', 'collectionmaxxing_files:/data/files', '-v', "${stage}:/in:ro",
        '--entrypoint', 'sh', $helperImage, '-c', $copyScript)

    if (-not $global:CmApply) {
        Write-CmLog 'DRY RUN (.ipa sync): would run, in order:'
        Write-CmLog "  $(Format-CmCommand 'gh' $download)"
        Write-CmLog '  (verify the SHA-256 of the .ipa against the release checksum file; a mismatch stops here)'
        Write-CmLog "  $(Format-CmCommand 'docker' $copy)"
        return Result 'dry-run' 0
    }

    # Only ever clean the staging folder inside our own root.
    $rootFull = [IO.Path]::GetFullPath($Ctx.Root)
    $stageFull = [IO.Path]::GetFullPath($stage)
    if (-not $stageFull.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) { throw "staging folder $stageFull is outside $rootFull" }
    if (Test-Path -LiteralPath $stageFull) { Remove-Item -LiteralPath $stageFull -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stageFull | Out-Null

    $dl = Invoke-Cm -Mutating -Command 'gh' -Arguments $download
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
    if ($state['ipa'] -and $state['ipa']['sha256'] -eq $actual) { return Result 'up-to-date' 0 }

    # The helper reads fixed names, so nothing from the release asset name reaches a shell.
    Copy-Item -LiteralPath $ipa.FullName -Destination (Join-Path $stageFull 'app.ipa') -Force
    Set-Content -LiteralPath (Join-Path $stageFull 'app.sha256') -Value "$actual  CollectionMaxxing.ipa" -Encoding ascii
    $r = Invoke-Cm -Mutating -Command 'docker' -Arguments $copy
    if ($r.ExitCode -ne 0) {
        Write-CmLog 'copying the .ipa into the files volume failed; the previous one is unchanged' 'ERROR'
        return Result 'copy-failed' 1
    }
    $state['ipa'] = @{ sha256 = $actual; file = $ipa.Name; syncedAt = [DateTime]::UtcNow.ToString('o') }
    Write-CmState -Path $Ctx.StateFile -State $state
    Write-CmLog "synced $($ipa.Name) into the files volume under ipa/"
    Result 'synced' 0
}
