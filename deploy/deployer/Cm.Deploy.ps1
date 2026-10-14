# The deploy flow. Dot-sourced by Deploy-CollectionMaxxing.ps1 (and by the tests). Needs Cm.Common.ps1 and Cm.Ipa.ps1.
#Requires -Version 7.0

function New-CmContext {
    param(
        [string]$Root = 'C:\CollectionMaxxing',
        [string]$ComposeFile = '',
        [string]$EnvFile = '',
        [string]$Repo = 'coryfinnegan/CollectionMaxxing',
        [int]$SmokeRetries = 12,
        [int]$SmokeDelaySeconds = 5
    )
    if (-not $ComposeFile) { $ComposeFile = Join-Path $Root 'repo/deploy/docker-compose.yml' }
    if (-not $EnvFile) { $EnvFile = Join-Path $Root '.env' }
    $owner = ($Repo -split '/')[0].ToLowerInvariant()
    $name = ($Repo -split '/')[1].ToLowerInvariant()
    [pscustomobject]@{
        Root              = $Root
        ComposeFile       = $ComposeFile
        EnvFile           = $EnvFile
        Repo              = $Repo
        ImagePrefix       = "ghcr.io/$owner/$name-"
        StateFile         = Join-Path $Root 'state/deployer-state.json'
        LockFile          = Join-Path $Root 'state/deployer.lock'
        LogFile           = Join-Path $Root 'logs/deployer.log'
        StageDir          = Join-Path $Root 'state/ipa-stage'
        SmokeRetries      = $SmokeRetries
        SmokeDelaySeconds = $SmokeDelaySeconds
        SmokeUrl          = 'http://127.0.0.1:5480/health/ready'
        Services          = @('api', 'backup', 'aspire-dashboard')
    }
}

function Get-CmImageRef {
    param($Ctx, [string]$Name, [string]$Tag)
    "$($Ctx.ImagePrefix)$Name`:$Tag"
}

function Get-CmImageInfo {
    <# Reads what is known locally about an image: digest, build revision and build time. $null if it is not there. #>
    param($Ctx, [string]$Ref)
    $r = Invoke-Cm -Command 'docker' -Arguments @('image', 'inspect', $Ref, '--format', '{{json .}}')
    if ($r.ExitCode -ne 0 -or -not $r.Output) { return $null }
    try { $o = ($r.Output -join "`n") | ConvertFrom-Json } catch { return $null }
    $repo = $Ref.Substring(0, $Ref.LastIndexOf(':'))
    $full = @($o.RepoDigests) | Where-Object { $_ -like "$repo@sha256:*" } | Select-Object -First 1
    if (-not $full) { return $null }
    $created = ConvertTo-CmUtc $o.Created
    [pscustomobject]@{
        Ref      = $Ref
        Repo     = $repo
        Digest   = $full.Substring($repo.Length + 1)
        Revision = "$($o.Config.Labels.'org.opencontainers.image.revision')"
        Created  = if ($created) { $created.ToString('o') } else { '' }
    }
}

function Test-CmAttestation {
    <#
      True only if `gh attestation verify` accepts the image AND the certificate says this repo built it from
      refs/heads/main at the expected commit. Anything else (including output we cannot read) is a no.
    #>
    param($Ctx, $Info, [string]$Sha)
    $workflow = "$($Ctx.Repo)/.github/workflows/release.yml"
    $r = Invoke-Cm -Command 'gh' -Arguments @(
        'attestation', 'verify', "oci://$($Info.Repo)@$($Info.Digest)",
        '--repo', $Ctx.Repo, '--signer-workflow', $workflow, '--format', 'json')
    if ($r.ExitCode -ne 0) { return [pscustomobject]@{ Ok = $false; Reason = "attestation check failed for $($Info.Ref)" } }
    try { $results = @(($r.Output -join "`n") | ConvertFrom-Json) } catch {
        return [pscustomobject]@{ Ok = $false; Reason = "attestation output for $($Info.Ref) is not readable" }
    }
    foreach ($res in $results) {
        $cert = $res.verificationResult.signature.certificate
        if (-not $cert) { continue }
        $ref = if ($cert.sourceRepositoryRef) { $cert.sourceRepositoryRef } else { $cert.githubWorkflowRef }
        if ($ref -ne 'refs/heads/main') { continue }
        if ($cert.sourceRepositoryDigest -ne $Sha) { continue }
        if ($cert.sourceRepositoryURI -and -not "$($cert.sourceRepositoryURI)".EndsWith($Ctx.Repo, [StringComparison]::OrdinalIgnoreCase)) { continue }
        return [pscustomobject]@{ Ok = $true; Reason = '' }
    }
    [pscustomobject]@{ Ok = $false; Reason = "$($Info.Ref) was not built from refs/heads/main of $($Ctx.Repo) at $Sha" }
}

function Invoke-CmSmokeTest {
    param($Ctx)
    $sink = if ($IsWindows) { 'NUL' } else { '/dev/null' }
    for ($i = 1; $i -le [math]::Max(1, $Ctx.SmokeRetries); $i++) {
        $r = Invoke-Cm -Command 'curl' -Arguments @('-s', '-m', '5', '-o', $sink, '-w', '%{http_code}', $Ctx.SmokeUrl)
        if ($r.ExitCode -eq 0 -and (($r.Output -join '').Trim() -eq '200')) { return $true }
        if ($i -lt $Ctx.SmokeRetries) { Start-CmSleep -Seconds $Ctx.SmokeDelaySeconds }
    }
    $false
}

function Invoke-CmUp {
    param($Ctx, [string]$Tag)
    Invoke-Cm -Mutating -Command 'docker' -Env @{ CM_TAG = $Tag } -Arguments (Get-CmComposeArgs $Ctx (@('up', '-d') + $Ctx.Services))
}

function Get-CmPlan {
    <# The commands a real run would make, in order, with <sha> where the answer is not known yet. Built from the same helpers. #>
    param($Ctx)
    $tag = '<sha>'
    $plan = @()
    $plan += Format-CmCommand 'docker' @('pull', (Get-CmImageRef $Ctx 'api' 'main'))
    $plan += Format-CmCommand 'docker' @('image', 'inspect', (Get-CmImageRef $Ctx 'api' 'main'), '--format', '{{json .}}')
    foreach ($name in 'api', 'migrate', 'backup') {
        $plan += Format-CmCommand 'docker' @('pull', (Get-CmImageRef $Ctx $name $tag))
        $plan += Format-CmCommand 'gh' @('attestation', 'verify', "oci://$($Ctx.ImagePrefix)$name@<digest>", '--repo', $Ctx.Repo,
            '--signer-workflow', "$($Ctx.Repo)/.github/workflows/release.yml", '--format', 'json')
    }
    $plan += Format-CmCommand 'docker' (Get-CmComposeArgs $Ctx @('run', '--rm', 'migrate')) @{ CM_TAG = $tag }
    $plan += Format-CmCommand 'docker' (Get-CmComposeArgs $Ctx (@('up', '-d') + $Ctx.Services)) @{ CM_TAG = $tag }
    $plan += Format-CmCommand 'curl' @('-s', '-m', '5', '-w', '%{http_code}', $Ctx.SmokeUrl)
    $plan += '(if the smoke test fails: the same up -d with CM_TAG=<previous good sha>)'
    $plan += '(then: Sync-Ipa.ps1 copies the newest .ipa from the latest GitHub release into the files volume)'
    $plan
}

function Get-CmComposeGuard {
    <# Runs `docker compose config` (read only, needs no daemon) and applies the config guard. Returns the list of problems. #>
    param($Ctx, [hashtable]$EnvMap)
    foreach ($name in 'POSTGRES_PASSWORD', 'CM_BACKUP_DIR') {
        if (-not $EnvMap.ContainsKey($name) -or -not $EnvMap[$name]) { return "$name is not set in $($Ctx.EnvFile)" }
    }
    $r = Invoke-Cm -Command 'docker' -Env @{ CM_TAG = 'guard-check' } -Arguments (Get-CmComposeArgs $Ctx @('config', '--format', 'json'))
    if ($r.ExitCode -ne 0) { return 'docker compose config failed (is the compose file path right?)' }
    try { $config = ($r.Output -join "`n") | ConvertFrom-Json } catch { return 'docker compose config did not return JSON' }
    Test-CmConfig -EnvMap $EnvMap -ComposeConfig $config
}

function Invoke-CmDeploy {
    <#
      One deployer run. Returns @{ Outcome; ExitCode }.
      Outcomes: dry-run, busy, config-error, no-docker, up-to-date, known-bad, refused, downgrade-refused, deployed,
                migrate-failed, rolled-back, failed.
    #>
    param($Ctx)
    function Result($outcome, $code) { [pscustomobject]@{ Outcome = $outcome; ExitCode = $code } }

    $envMap = Read-CmEnvFile -Path $Ctx.EnvFile
    Initialize-Cm -Apply $global:CmApply -LogFile $Ctx.LogFile -Secrets @($envMap['POSTGRES_PASSWORD'], $envMap['CM_HEARTBEAT_URL'])
    $apiPort = 0
    if ([int]::TryParse("$($envMap['CM_API_PORT'])", [ref]$apiPort) -and -not (Test-CmPort 'CM_API_PORT' $apiPort)) {
        $Ctx.SmokeUrl = "http://127.0.0.1:$apiPort/health/ready"
    }

    if (-not $global:CmApply) {
        Write-CmLog 'DRY RUN: nothing will be changed. Add -Apply to deploy for real.'
        $problems = @(Get-CmComposeGuard -Ctx $Ctx -EnvMap $envMap)
        foreach ($p in $problems) { Write-CmLog "config problem: $p" 'WARN' }
        Write-CmLog 'A real run would make these calls, in order:'
        foreach ($line in Get-CmPlan -Ctx $Ctx) { Write-CmLog "  $line" }
        return Result 'dry-run' 0
    }

    $lock = Enter-CmLock -Path $Ctx.LockFile
    if (-not $lock) {
        Write-CmLog 'another deployer run is in progress; exiting' 'INFO'
        return Result 'busy' 0
    }
    try {
        $problems = @(Get-CmComposeGuard -Ctx $Ctx -EnvMap $envMap)
        if ($problems.Count -gt 0) {
            foreach ($p in $problems) { Write-CmLog "config refused: $p" 'ERROR' }
            return Result 'config-error' 2
        }

        $state = Read-CmState -Path $Ctx.StateFile
        $current = $state['current']

        # 1. The newest main image.
        $apiMain = Get-CmImageRef $Ctx 'api' 'main'
        $pull = Invoke-Cm -Mutating -Command 'docker' -Arguments @('pull', '--quiet', $apiMain)
        if ($pull.ExitCode -ne 0) {
            Write-CmLog "could not pull $apiMain (is Docker running, and is there a main image yet?)" 'ERROR'
            return Result 'no-docker' 3
        }
        $api = Get-CmImageInfo -Ctx $Ctx -Ref $apiMain
        if (-not $api -or $api.Revision -notmatch '^[0-9a-f]{40}$') {
            Write-CmLog "$apiMain has no usable build revision label; nothing deployed" 'ERROR'
            return Result 'refused' 0
        }
        $sha = $api.Revision

        if ($current -and $current['sha'] -eq $sha) { return Result 'up-to-date' 0 }
        if ($state['failedSha'] -eq $sha) {
            Write-CmLog "image $($sha.Substring(0, 12)) already failed here; waiting for a newer one" 'INFO'
            return Result 'known-bad' 0
        }

        # 2. Never go backwards.
        if ($current -and $current['created'] -and $api.Created) {
            if ((ConvertTo-CmUtc $api.Created) -lt (ConvertTo-CmUtc $current['created'])) {
                Write-CmLog "refusing a downgrade: main image $($sha.Substring(0, 12)) is older than the deployed $("$($current['sha'])".Substring(0, 12))" 'WARN'
                return Result 'downgrade-refused' 0
            }
        }

        # 3. All three images at that commit, each proven to come from this repo's workflow on main.
        $infos = @{}
        foreach ($name in 'api', 'migrate', 'backup') {
            $ref = Get-CmImageRef $Ctx $name $sha
            $p = Invoke-Cm -Mutating -Command 'docker' -Arguments @('pull', '--quiet', $ref)
            $info = if ($p.ExitCode -eq 0) { Get-CmImageInfo -Ctx $Ctx -Ref $ref } else { $null }
            if (-not $info -or $info.Revision -ne $sha) {
                Write-CmLog "$ref is missing or is not built from $sha; nothing deployed" 'ERROR'
                return Result 'refused' 0
            }
            $check = Test-CmAttestation -Ctx $Ctx -Info $info -Sha $sha
            if (-not $check.Ok) {
                Write-CmLog "$($check.Reason); nothing deployed" 'ERROR'
                return Result 'refused' 0
            }
            $infos[$name] = $info
        }
        if ($infos['api'].Digest -ne $api.Digest) {
            Write-CmLog 'the main tag moved while checking; nothing deployed, will retry' 'WARN'
            return Result 'refused' 0
        }
        Write-CmLog "deploying $($sha.Substring(0, 12)) (attestations verified)"

        # 4. Migrate (expand-only migrations, so the running api stays compatible), then restart the api.
        $mig = Invoke-Cm -Mutating -Command 'docker' -Env @{ CM_TAG = $sha } -Arguments (Get-CmComposeArgs $Ctx @('run', '--rm', 'migrate'))
        if ($mig.ExitCode -ne 0) {
            Write-CmLog 'migration failed; the running api was not touched' 'ERROR'
            $state['failedSha'] = $sha
            Write-CmState -Path $Ctx.StateFile -State $state
            return Result 'migrate-failed' 1
        }
        $up = Invoke-CmUp -Ctx $Ctx -Tag $sha
        $healthy = ($up.ExitCode -eq 0) -and (Invoke-CmSmokeTest -Ctx $Ctx)

        if ($healthy) {
            $state['current'] = @{
                sha        = $sha
                created    = $api.Created
                deployedAt = [DateTime]::UtcNow.ToString('o')
                digests    = @{ api = $infos['api'].Digest; migrate = $infos['migrate'].Digest; backup = $infos['backup'].Digest }
            }
            $state.Remove('failedSha')
            Write-CmState -Path $Ctx.StateFile -State $state
            Write-CmLog "deployed $($sha.Substring(0, 12)); smoke test passed"
            try { Invoke-CmIpaSync -Ctx $Ctx -Tag $sha | Out-Null } catch { Write-CmLog ".ipa sync failed: $($_.Exception.Message)" 'WARN' }
            return Result 'deployed' 0
        }

        # 5. Rollback: pin the previous good tag and restart.
        $state['failedSha'] = $sha
        Write-CmState -Path $Ctx.StateFile -State $state
        if ($current) {
            Write-CmLog "smoke test failed; pinning previous good $("$($current['sha'])".Substring(0, 12))" 'ERROR'
            Invoke-CmUp -Ctx $Ctx -Tag $current['sha'] | Out-Null
            if (Invoke-CmSmokeTest -Ctx $Ctx) { Write-CmLog 'rolled back; the previous version is healthy' 'WARN' }
            else { Write-CmLog 'rolled back but the previous version is not healthy either; check the stack' 'ERROR' }
            return Result 'rolled-back' 1
        }
        Write-CmLog 'smoke test failed and there is no previous good version to return to' 'ERROR'
        Result 'failed' 1
    }
    finally {
        Exit-CmLock $lock
    }
}
