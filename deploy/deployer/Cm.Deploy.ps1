# The deploy flow. Dot-sourced by Deploy-CollectionMaxxing.ps1 (and by the tests). Needs Cm.Common.ps1.
#Requires -Version 7.0

# A lock stamped longer ago than this means the run holding it is stuck (see New-CmBusyResult): the next run alerts
# instead of quietly exiting. Polls run every 2 minutes and a healthy run takes a few minutes at most (pulls, one
# migration, up to a minute of smoke tests, a fast-forward), so 30 minutes is far beyond any live run, yet pages
# within half an hour instead of silently halting deploys.
$script:CmStaleLockMinutes = 30

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
        RepoDir           = Join-Path $Root 'repo'
        ComposeFile       = $ComposeFile
        EnvFile           = $EnvFile
        Repo              = $Repo
        ImagePrefix       = "ghcr.io/$owner/$name-"
        StateFile         = Join-Path $Root 'state/deployer-state.json'
        LockFile          = Join-Path $Root 'state/deployer.lock'
        LogFile           = Join-Path $Root 'logs/deployer.log'
        StageDir          = Join-Path $Root 'state/ipa-stage'   # left by the retired .ipa sync; removed once (Remove-CmRetiredIpa)
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

function Get-CmUnhealthyServices {
    <#
      Parses `docker compose ps --format json` (an array, or one object per line on older compose) and names every
      service in $Ctx.Services that is not verifiably up: no container, a state other than running (restarting,
      exited, created, ...), or a health other than healthy where the service has a healthcheck. Services the
      deployer does not start (postgres, the finished migrate) are ignored. Anything unreadable fails closed:
      every service is reported unknown.
    #>
    param($Ctx, [string]$PsJson)
    $names = @($Ctx.Services)
    try {
        $text = "$PsJson".Trim()
        if (-not $text) { throw 'empty container list' }
        $rows = if ($text.StartsWith('[')) { @($text | ConvertFrom-Json) }
        else { @($text -split "`r?`n" | Where-Object { "$_".Trim() } | ForEach-Object { $_ | ConvertFrom-Json }) }
    }
    catch { return @($names | ForEach-Object { "$_ is unknown (could not read the container list)" }) }
    $bad = [System.Collections.Generic.List[string]]::new()
    foreach ($name in $names) {
        $mine = @($rows | Where-Object { $_.Service -eq $name })
        if ($mine.Count -eq 0) { $bad.Add("$name has no container"); continue }
        foreach ($row in $mine) {
            if ("$($row.State)" -ne 'running') { $bad.Add("$name is $($row.State)") }
            elseif ($null -ne $row.Health -and "$($row.Health)" -notin @('', 'healthy')) { $bad.Add("$name health is $($row.Health)") }
        }
    }
    $bad.ToArray()
}

function Test-CmServicesHealthy {
    <#
      True once every service in $Ctx.Services is actually up (see Get-CmUnhealthyServices). Polls
      `docker compose ps --format json` with the same retries and delay as the smoke test: right after `up -d`
      a container may still be starting. Returns @{ Ok; Problems } so the failure can name the service.
    #>
    param($Ctx)
    $problems = @('could not list the running containers')
    for ($i = 1; $i -le [math]::Max(1, $Ctx.SmokeRetries); $i++) {
        $r = Invoke-Cm -Command 'docker' -Env @{ CM_TAG = 'health-check' } -Arguments (Get-CmComposeArgs $Ctx @('ps', '--format', 'json'))
        if ($r.ExitCode -eq 0) {
            $problems = @(Get-CmUnhealthyServices -Ctx $Ctx -PsJson ($r.Output -join "`n"))
            if ($problems.Count -eq 0) { return [pscustomobject]@{ Ok = $true; Problems = @() } }
        }
        if ($i -lt $Ctx.SmokeRetries) { Start-CmSleep -Seconds $Ctx.SmokeDelaySeconds }
    }
    [pscustomobject]@{ Ok = $false; Problems = $problems }
}

function Test-CmStackHealthy {
    <#
      The full "is this version good" check, in order: the API answers /health/ready, then every started service
      is up. Returns @{ Ok; Note } where Note names what failed ('the smoke test failed', or 'the service check
      failed (backup is restarting)').
    #>
    param($Ctx)
    if (-not (Invoke-CmSmokeTest -Ctx $Ctx)) { return [pscustomobject]@{ Ok = $false; Note = 'the smoke test failed' } }
    $services = Test-CmServicesHealthy -Ctx $Ctx
    if (-not $services.Ok) { return [pscustomobject]@{ Ok = $false; Note = "the service check failed ($($services.Problems -join '; '))" } }
    [pscustomobject]@{ Ok = $true; Note = '' }
}

function New-CmCtxAtCommit {
    <#
      A copy of Ctx whose compose file is the one AT commit Sha (git show), so a release starts with its own compose file
      (new variables, volumes, services) instead of the previous release's. It is written next to the clone's compose file
      as an untracked, hidden file so the relative paths inside it (./recognition, ../evals) resolve the same, and the
      clone stays clean (the clone check ignores untracked files). Returns $null when the file cannot be read.
      Remove-CmCtxAtCommit deletes it. Invoke-CmReal merges stderr into the output, so a garbled copy (a git error in
      the text) is caught by the config guard run on it before anything migrates.
    #>
    param($Ctx, [string]$Sha)
    if ($Sha -cnotmatch '^[0-9a-f]{40}$') { return $null }
    $show = Invoke-Cm -Command 'git' -Arguments @('-C', $Ctx.RepoDir, 'show', "${Sha}:deploy/docker-compose.yml")
    if ($show.ExitCode -ne 0 -or -not (@($show.Output) -join '').Trim()) { return $null }
    $dir = Split-Path -Parent $Ctx.ComposeFile
    $path = Join-Path $dir ".docker-compose.$($Sha.Substring(0, 12)).$PID.yml"
    try { Set-Content -LiteralPath $path -Value (@($show.Output) -join "`n") -Encoding utf8 } catch { return $null }
    $copy = $Ctx.PSObject.Copy()
    $copy.ComposeFile = $path
    $copy
}

function Remove-CmCtxAtCommit {
    param($Ctx, $CommitCtx)
    if ($CommitCtx -and $CommitCtx.ComposeFile -ne $Ctx.ComposeFile) { Remove-Item -LiteralPath $CommitCtx.ComposeFile -Force -ErrorAction SilentlyContinue }
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
    $plan += Format-CmCommand 'git' @('-C', $Ctx.RepoDir, 'fetch', '--quiet', 'origin', 'main')
    $plan += Format-CmCommand 'git' @('-C', $Ctx.RepoDir, 'merge-base', '--is-ancestor', '<deployed sha>', $tag)
    $plan += '(the deploy is refused unless the new commit descends from the deployed one; an unknown deployed commit is refused too)'
    foreach ($name in 'api', 'migrate', 'backup') {
        $plan += Format-CmCommand 'docker' @('pull', (Get-CmImageRef $Ctx $name $tag))
        $plan += Format-CmCommand 'gh' @('attestation', 'verify', "oci://$($Ctx.ImagePrefix)$name@<digest>", '--repo', $Ctx.Repo,
            '--signer-workflow', "$($Ctx.Repo)/.github/workflows/release.yml", '--format', 'json')
    }
    $plan += Format-CmCommand 'docker' (Get-CmComposeArgs $Ctx @('run', '--rm', 'migrate')) @{ CM_TAG = $tag }
    $plan += Format-CmCommand 'docker' (Get-CmComposeArgs $Ctx (@('up', '-d') + $Ctx.Services)) @{ CM_TAG = $tag }
    $plan += Format-CmCommand 'curl' @('-s', '-m', '5', '-w', '%{http_code}', $Ctx.SmokeUrl)
    $plan += Format-CmCommand 'docker' (Get-CmComposeArgs $Ctx @('ps', '--format', 'json')) @{ CM_TAG = 'health-check' }
    $plan += '(if the smoke test or the service check fails: the same up -d with CM_TAG=<previous good sha>)'
    $plan += '(then, if the clone is clean and on main, it is fast-forwarded to the deployed commit so the deployer scripts on disk match the running build)'
    $plan += Format-CmCommand 'git' @('-C', $Ctx.RepoDir, 'merge', '--ff-only', $tag)
    $plan += '(then, once, on a server that still has the .ipa it served before the iPhone app moved to TestFlight, any run removes it)'
    $plan += Format-CmCommand 'docker' (Get-CmRetiredIpaArgs $Ctx '<deployed sha>')
    $plan += '(any refusal, failure or rollback exits non-zero, writes an ALERT line to the log and pings <CM_HEARTBEAT_URL>/fail)'
    $plan
}

function Get-CmRollbackPlan {
    param($Ctx, [string]$Target)
    $sha = if ($Target) { $Target } else { '<target sha>' }
    $plan = @()
    $plan += Format-CmCommand 'git' @('-C', $Ctx.RepoDir, 'fetch', '--quiet', 'origin', 'main')
    $plan += "(refused unless $sha is an older commit of the deployed one: git merge-base --is-ancestor $sha <deployed sha>)"
    foreach ($name in 'api', 'migrate', 'backup') {
        $plan += Format-CmCommand 'docker' @('pull', (Get-CmImageRef $Ctx $name $sha))
        $plan += Format-CmCommand 'gh' @('attestation', 'verify', "oci://$($Ctx.ImagePrefix)$name@<digest>", '--repo', $Ctx.Repo,
            '--signer-workflow', "$($Ctx.Repo)/.github/workflows/release.yml", '--format', 'json')
    }
    $plan += '(no migration: migrations are expand-only, so the older api runs on the newer schema)'
    $plan += Format-CmCommand 'docker' (Get-CmComposeArgs $Ctx (@('up', '-d') + $Ctx.Services)) @{ CM_TAG = $sha }
    $plan += Format-CmCommand 'curl' @('-s', '-m', '5', '-w', '%{http_code}', $Ctx.SmokeUrl)
    $plan += Format-CmCommand 'docker' (Get-CmComposeArgs $Ctx @('ps', '--format', 'json')) @{ CM_TAG = 'health-check' }
    $plan += '(if the older version is not healthy: the same up -d with CM_TAG=<the version that was running>)'
    $plan += '(on success the commit rolled back from is remembered as bad, so the poller does not put it straight back)'
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

# ---- the ancestry guard ------------------------------------------------------------------------------------------------

function Update-CmRepo {
    <# Fetches main into the deployer's clone so the commits to compare exist locally. Apply mode only. True on success. #>
    param($Ctx)
    $r = Invoke-Cm -Mutating -Command 'git' -Arguments @('-C', $Ctx.RepoDir, 'fetch', '--quiet', 'origin', 'main')
    $r.ExitCode -eq 0
}

function Update-CmRepoToDeployed {
    <#
      After a good deploy of Sha: fast-forward the deployer's clone to it, so the scripts the next run loads (and the
      compose file) are the ones from the build that is now running. The images were just attested as built from this
      commit on main, and the clone only ever moves along main. It never resets, checks out, cleans or forces: a clone with
      local changes, off main, or that cannot fast-forward is left exactly as it is, with a WARN (not an alert).
      Returns $true when the clone is at Sha afterwards.
    #>
    param($Ctx, [string]$Sha)
    $repo = $Ctx.RepoDir
    $short = if ($Sha.Length -ge 12) { $Sha.Substring(0, 12) } else { $Sha }
    if ($Sha -cnotmatch '^[0-9a-f]{40}$') { Write-CmLog "not moving the clone at ${repo}: '$Sha' is not a full commit hash" 'WARN'; return $false }
    $status = Invoke-Cm -Command 'git' -Arguments @('-C', $repo, 'status', '--porcelain', '--untracked-files=no')
    if ($status.ExitCode -ne 0) { Write-CmLog "could not read the state of the clone at $repo, so it was not moved to $short" 'WARN'; return $false }
    if (@($status.Output | Where-Object { "$_".Trim() }).Count -gt 0) {
        Write-CmLog "the clone at $repo has local changes, so it was not moved to $short; the deployer scripts there may be older than the running build" 'WARN'
        return $false
    }
    $branch = Invoke-Cm -Command 'git' -Arguments @('-C', $repo, 'symbolic-ref', '--quiet', '--short', 'HEAD')
    if ($branch.ExitCode -ne 0 -or (@($branch.Output) -join '').Trim() -ne 'main') {
        Write-CmLog "the clone at $repo is not on main, so it was not moved to $short" 'WARN'
        return $false
    }
    $merge = Invoke-Cm -Mutating -Command 'git' -Arguments @('-C', $repo, 'merge', '--ff-only', $Sha)
    if ($merge.ExitCode -ne 0) {
        Write-CmLog "the clone at $repo could not be fast-forwarded to $short (it has diverged, or the commit was not fetched); left as it is" 'WARN'
        return $false
    }
    Write-CmLog "moved the clone at $repo to $short, so the deployer scripts match the running build"
    $true
}

function Get-CmRetiredIpaArgs {
    <# The helper container that deletes ipa/ from the files volume. The backup image has a shell; the api image does not. #>
    param($Ctx, [string]$Tag)
    @('run', '--rm', '--network', 'none', '-v', 'collectionmaxxing_files:/data/files', '--entrypoint', 'sh', (Get-CmImageRef $Ctx 'backup' $Tag), '-c', 'rm -rf /data/files/ipa')
}

function Remove-CmRetiredIpa {
    <#
      ADR-024: the iPhone app comes from TestFlight, so the server no longer serves an .ipa. On a run whose state still
      remembers one (ipa or ipaAttempt, from the retired sync), delete ipa/ from the files volume and the old staging
      folder, then forget it. A failure is a WARN, never an alert, and the next run tries again. Apply mode only.
    #>
    param($Ctx)
    if (-not $global:CmApply) { return }
    $state = Read-CmState -Path $Ctx.StateFile
    if (-not ($state.ContainsKey('ipa') -or $state.ContainsKey('ipaAttempt')) -or -not $state['current']) { return }
    try {
        $r = Invoke-Cm -Mutating -Command 'docker' -Arguments (Get-CmRetiredIpaArgs $Ctx "$($state['current']['sha'])")
        if ($r.ExitCode -ne 0) { throw "docker run exited $($r.ExitCode)" }
        # Only ever clean the staging folder inside our own root.
        $rootFull = [IO.Path]::GetFullPath($Ctx.Root)
        $stageFull = [IO.Path]::GetFullPath($Ctx.StageDir)
        if ($stageFull.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $stageFull)) {
            Remove-Item -LiteralPath $stageFull -Recurse -Force
        }
    }
    catch {
        Write-CmLog "could not remove the old .ipa from the files volume ($($_.Exception.Message)); the next run tries again" 'WARN'
        return
    }
    $state.Remove('ipa')
    $state.Remove('ipaAttempt')
    Write-CmState -Path $Ctx.StateFile -State $state
    Write-CmLog 'removed the old .ipa from the files volume: the iPhone app comes from TestFlight now (ADR-024)'
}

function Complete-CmQuietRun {
    <# A run with nothing to deploy: the one-time clean-up of the retired .ipa, if it is still there. Never turns a quiet run into a crash or an alert. #>
    param($Ctx, [string]$Outcome)
    Remove-CmRetiredIpa -Ctx $Ctx
    New-CmResult $Outcome 0
}

function Test-CmAncestry {
    <#
      Decides by commit ancestry (git merge-base --is-ancestor), never by build time or run number, whether Target may
      replace Current. Ok only when Target is a strict descendant of Current.
      Relation: descendant, equal, ancestor (Target is older), diverged, unknown-current, unknown-target.
      Anything git cannot answer (no clone, a commit it has never seen, a malformed hash) fails closed.
    #>
    param($Ctx, [string]$Current, [string]$Target)
    function Answer($ok, $relation, $reason) { [pscustomobject]@{ Ok = $ok; Relation = $relation; Reason = $reason } }
    $short = { param($s) if ("$s".Length -ge 12) { "$s".Substring(0, 12) } else { "'$s'" } }

    if ("$Current" -cnotmatch '^[0-9a-f]{40}$') { return Answer $false 'unknown-current' "the deployed commit $(& $short $Current) is not a full commit hash, so the new one cannot be checked against it" }
    if ("$Target" -cnotmatch '^[0-9a-f]{40}$') { return Answer $false 'unknown-target' "the target commit $(& $short $Target) is not a full commit hash" }

    foreach ($which in @(@('unknown-current', $Current), @('unknown-target', $Target))) {
        $r = Invoke-Cm -Command 'git' -Arguments @('-C', $Ctx.RepoDir, 'cat-file', '-e', ($which[1] + '^{commit}'))
        if ($r.ExitCode -ne 0) {
            $what = if ($which[0] -eq 'unknown-current') { 'the deployed commit' } else { 'the new commit' }
            return Answer $false $which[0] "$what $(& $short $which[1]) is not in the local clone at $($Ctx.RepoDir), so the order of the two cannot be checked"
        }
    }
    if ($Current -eq $Target) { return Answer $false 'equal' "commit $(& $short $Target) is the one already deployed" }

    $forward = Invoke-Cm -Command 'git' -Arguments @('-C', $Ctx.RepoDir, 'merge-base', '--is-ancestor', $Current, $Target)
    if ($forward.ExitCode -eq 0) { return Answer $true 'descendant' '' }
    if ($forward.ExitCode -ne 1) { return Answer $false 'unknown-current' 'git could not compare the two commits' }
    $backward = Invoke-Cm -Command 'git' -Arguments @('-C', $Ctx.RepoDir, 'merge-base', '--is-ancestor', $Target, $Current)
    if ($backward.ExitCode -eq 0) { return Answer $false 'ancestor' "commit $(& $short $Target) is older than the deployed $(& $short $Current)" }
    Answer $false 'diverged' "commit $(& $short $Target) has diverged from the deployed $(& $short $Current) (it is not a descendant of it)"
}

function Read-CmStateStrict {
    <# Like Read-CmState, but a state file that exists and cannot be read is a problem, not "nothing deployed yet". #>
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return [pscustomobject]@{ Ok = $true; State = @{}; Reason = '' } }
    try { $state = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable } catch { $state = $null }
    if ($state -isnot [hashtable]) {
        return [pscustomobject]@{ Ok = $false; State = @{}; Reason = "the state file $Path exists but cannot be read; refusing to treat this as a first deploy" }
    }
    [pscustomobject]@{ Ok = $true; State = $state; Reason = '' }
}

function Test-CmUnseededInstall {
    <#
      Called when no deployed commit is on record. A true first deploy has no running stack and no pinned CM_TAG. If a
      stack is running (or .env / the compose file pins a tag) the state file was lost or points at the wrong place, and
      deploying "whatever main is" could be a downgrade, so the caller must refuse. Returns $null when it is a first
      deploy, otherwise the reason.
    #>
    param($Ctx, [hashtable]$EnvMap)
    $seed = "seed it with the deployed commit: write {""current"":{""sha"":""<40-char sha of the running api image>""}} to $($Ctx.StateFile) (deploy/README.md, 'If the state file is lost')"
    if ("$($EnvMap['CM_TAG'])".Trim()) { return "no deployed commit on record at $($Ctx.StateFile), but .env pins CM_TAG=$($EnvMap['CM_TAG']), so something is already deployed; refusing to treat this as a first deploy; $seed" }
    if (Test-Path -LiteralPath $Ctx.ComposeFile) {
        $text = Get-Content -LiteralPath $Ctx.ComposeFile -Raw
        if ($text -match '(?m)^[^#\r\n]*\$\{CM_TAG:?-') { return "no deployed commit on record at $($Ctx.StateFile), but $($Ctx.ComposeFile) pins a default CM_TAG, so something is already deployed; refusing to treat this as a first deploy; $seed" }
    }
    $ps = Invoke-Cm -Command 'docker' -Env @{ CM_TAG = 'state-check' } -Arguments (Get-CmComposeArgs $Ctx @('ps', '-q', '--status', 'running'))
    if ($ps.ExitCode -ne 0) { return "no deployed commit on record at $($Ctx.StateFile) and docker compose could not say whether a stack is running; refusing to treat this as a first deploy; $seed" }
    if (@($ps.Output | Where-Object { "$_".Trim() }).Count -gt 0) { return "no deployed commit on record at $($Ctx.StateFile), but a stack is already running; refusing to treat this as a first deploy; $seed" }
    $null
}

# ---- loud failures -----------------------------------------------------------------------------------------------------

function New-CmResult {
    <# Exit codes: 0 done or nothing to do, 1 failed or rolled back, 2 config refused, 3 Docker/GHCR unreachable, 4 refused. (5 was the retired .ipa sync.) #>
    param([string]$Outcome, [int]$Code, [string]$Reason = '')
    [pscustomobject]@{ Outcome = $Outcome; ExitCode = $Code; Reason = $Reason }
}

function Initialize-CmRun {
    <# Reads .env, sets the log file and the secrets to mask, points the smoke test at the configured port. Returns the .env table. #>
    param($Ctx)
    $envMap = Read-CmEnvFile -Path $Ctx.EnvFile
    Initialize-Cm -Apply $global:CmApply -LogFile $Ctx.LogFile -Secrets @($envMap['POSTGRES_PASSWORD'], $envMap['CM_HEARTBEAT_URL'])
    $apiPort = 0
    if ([int]::TryParse("$($envMap['CM_API_PORT'])", [ref]$apiPort) -and -not (Test-CmPort 'CM_API_PORT' $apiPort)) {
        $Ctx.SmokeUrl = "http://127.0.0.1:$apiPort/health/ready"
    }
    $envMap
}

function Send-CmAlert {
    <#
      A refused, failed or rolled-back run is announced twice: an ALERT line in the deployer log, and a ping of
      <CM_HEARTBEAT_URL>/fail (the same free check the nightly backup uses; healthchecks.io emails on it) with the reason
      as the body. A failed ping is logged and changes nothing: the exit code stays what the deploy decided.
    #>
    param($Ctx, $Result)
    $reason = if ($Result.Reason) { $Result.Reason } else { 'no reason recorded; see the lines above in the log' }
    $envMap = Initialize-CmRun -Ctx $Ctx
    Write-CmLog "ALERT deploy $($Result.Outcome) (exit $($Result.ExitCode)): $reason" 'ERROR'
    $base = "$($envMap['CM_HEARTBEAT_URL'])".Trim()
    if (-not $base) {
        Write-CmLog 'CM_HEARTBEAT_URL is not set in .env, so no failure ping was sent; this log line is the only alert' 'WARN'
        return
    }
    $body = Hide-CmSecrets "ALERT deploy $($Result.Outcome): $reason"
    if ($body.Length -gt 900) { $body = $body.Substring(0, 900) }
    $sink = if ($IsWindows) { 'NUL' } else { '/dev/null' }
    $ping = Invoke-Cm -Command 'curl' -Arguments @('-fsS', '-m', '10', '--retry', '3', '-o', $sink, '--data-raw', $body, ($base.TrimEnd('/') + '/fail'))
    if ($ping.ExitCode -ne 0) { Write-CmLog 'alert ping failed (the log line above is still the alert)' 'WARN' }
}

# ---- one deploy ----------------------------------------------------------------------------------------------------------

function Get-CmVerifiedImages {
    <# Pulls api, migrate and backup at one commit and proves each came from this repo's workflow on main at that commit. #>
    param($Ctx, [string]$Sha)
    $infos = @{}
    foreach ($name in 'api', 'migrate', 'backup') {
        $ref = Get-CmImageRef $Ctx $name $Sha
        $p = Invoke-Cm -Mutating -Command 'docker' -Arguments @('pull', '--quiet', $ref)
        $info = if ($p.ExitCode -eq 0) { Get-CmImageInfo -Ctx $Ctx -Ref $ref } else { $null }
        if (-not $info -or $info.Revision -ne $Sha) {
            return [pscustomobject]@{ Ok = $false; Reason = "$ref is missing or is not built from $Sha; nothing deployed"; Infos = $null }
        }
        $check = Test-CmAttestation -Ctx $Ctx -Info $info -Sha $Sha
        if (-not $check.Ok) { return [pscustomobject]@{ Ok = $false; Reason = "$($check.Reason); nothing deployed"; Infos = $null } }
        $infos[$name] = $info
    }
    [pscustomobject]@{ Ok = $true; Reason = ''; Infos = $infos }
}

function Invoke-CmDeploy {
    <#
      One deployer run. Returns @{ Outcome; ExitCode; Reason }. Without -RollbackTo it is the normal poll; with it, the
      explicit rollback path (the only way to go back to an older commit).
      Outcomes and exit codes:
        0  dry-run, busy, up-to-date, known-bad, deployed, manual-rollback
        1  migrate-failed, rolled-back, failed, rollback-failed, crashed, stale-lock
        2  config-error
        3  no-docker
        4  refused, downgrade-refused, rollback-refused
      Every non-zero outcome of a real (-Apply) run is logged as ALERT and pinged to <CM_HEARTBEAT_URL>/fail.
    #>
    param($Ctx, [string]$RollbackTo = '')
    try {
        $result = if ($RollbackTo) { Invoke-CmRollbackCore -Ctx $Ctx -Target $RollbackTo } else { Invoke-CmDeployCore -Ctx $Ctx }
    }
    catch {
        $message = "the deployer crashed: $($_.Exception.Message)"
        Write-CmLog $message 'ERROR'
        $result = New-CmResult 'crashed' 1 $message
    }
    if ($result.ExitCode -ne 0 -and $global:CmApply) { Send-CmAlert -Ctx $Ctx -Result $result }
    $result
}

function Get-CmLockAgeMinutes {
    <# How long ago the holder stamped the lock file, in minutes. $null when there is nothing to judge (no file, or it cannot be read): never stale. Reads only metadata, so it works even while another run holds the file open exclusively. #>
    param([string]$Path)
    try {
        if (-not (Test-Path -LiteralPath $Path)) { return $null }
        ([DateTime]::UtcNow - [IO.File]::GetLastWriteTimeUtc($Path)).TotalMinutes
    }
    catch { $null }
}

function New-CmBusyResult {
    <#
      The lock is held by another run. Usually that is just overlapping polls: exit 0, quietly. But a run that
      hangs (a stuck docker or gh call) while holding the lock would silence every later run the same way, with
      exit 0 and no alert, so a lock stamped longer ago than CmStaleLockMinutes alerts like a failure instead.
      The lock is never broken: two deployers must never run at once.
    #>
    param($Ctx)
    $age = Get-CmLockAgeMinutes -Path $Ctx.LockFile
    if (($null -ne $age) -and ($age -gt $script:CmStaleLockMinutes)) {
        $message = "the deployer lock at $($Ctx.LockFile) has been held for $([math]::Floor($age)) minutes, so the run holding it looks stuck; not starting another one (the lock was not broken)"
        Write-CmLog $message 'ERROR'
        return New-CmResult 'stale-lock' 1 $message
    }
    Write-CmLog 'another deployer run is in progress; exiting' 'INFO'
    New-CmResult 'busy' 0
}

function Invoke-CmDeployCore {
    param($Ctx)
    $envMap = Initialize-CmRun -Ctx $Ctx

    if (-not $global:CmApply) {
        Write-CmLog 'DRY RUN: nothing will be changed. Add -Apply to deploy for real.'
        $problems = @(Get-CmComposeGuard -Ctx $Ctx -EnvMap $envMap)
        foreach ($p in $problems) { Write-CmLog "config problem: $p" 'WARN' }
        Write-CmLog 'A real run would make these calls, in order:'
        foreach ($line in Get-CmPlan -Ctx $Ctx) { Write-CmLog "  $line" }
        return New-CmResult 'dry-run' 0
    }

    $lock = Enter-CmLock -Path $Ctx.LockFile
    if (-not $lock) { return New-CmBusyResult -Ctx $Ctx }
    try {
        $problems = @(Get-CmComposeGuard -Ctx $Ctx -EnvMap $envMap)
        if ($problems.Count -gt 0) {
            foreach ($p in $problems) { Write-CmLog "config refused: $p" 'ERROR' }
            return New-CmResult 'config-error' 2 ($problems -join '; ')
        }

        $read = Read-CmStateStrict -Path $Ctx.StateFile
        if (-not $read.Ok) {
            Write-CmLog $read.Reason 'ERROR'
            return New-CmResult 'refused' 4 $read.Reason
        }
        $state = $read.State
        $current = $state['current']
        if (-not $current) {
            $unseeded = Test-CmUnseededInstall -Ctx $Ctx -EnvMap $envMap
            if ($unseeded) {
                Write-CmLog $unseeded 'ERROR'
                return New-CmResult 'refused' 4 $unseeded
            }
        }

        # 1. The newest main image.
        $apiMain = Get-CmImageRef $Ctx 'api' 'main'
        $pull = Invoke-Cm -Mutating -Command 'docker' -Arguments @('pull', '--quiet', $apiMain)
        if ($pull.ExitCode -ne 0) {
            $message = "could not pull $apiMain (is Docker running, and is there a main image yet?)"
            Write-CmLog $message 'ERROR'
            return New-CmResult 'no-docker' 3 $message
        }
        $api = Get-CmImageInfo -Ctx $Ctx -Ref $apiMain
        if (-not $api -or $api.Revision -notmatch '^[0-9a-f]{40}$') {
            $message = "$apiMain has no usable build revision label; nothing deployed"
            Write-CmLog $message 'ERROR'
            return New-CmResult 'refused' 4 $message
        }
        $sha = $api.Revision

        if ($current -and $current['sha'] -eq $sha) { return Complete-CmQuietRun -Ctx $Ctx -Outcome 'up-to-date' }
        if ($state['failedSha'] -eq $sha) {
            Write-CmLog "image $($sha.Substring(0, 12)) already failed here; waiting for a newer one" 'INFO'
            return Complete-CmQuietRun -Ctx $Ctx -Outcome 'known-bad'
        }

        # 2. Never go backwards: the new commit must descend from the deployed one (git, not clocks). The first ever
        #    deploy has nothing to compare with. A deployed commit this clone does not know fails closed.
        if ($current) {
            if (-not (Update-CmRepo -Ctx $Ctx)) {
                $message = "could not fetch main into $($Ctx.RepoDir), so the new commit cannot be checked against the deployed one; nothing deployed"
                Write-CmLog $message 'ERROR'
                return New-CmResult 'downgrade-refused' 4 $message
            }
            $order = Test-CmAncestry -Ctx $Ctx -Current "$($current['sha'])" -Target $sha
            if (-not $order.Ok) {
                $message = "refusing to deploy $($sha.Substring(0, 12)): $($order.Reason)"
                Write-CmLog $message 'WARN'
                return New-CmResult 'downgrade-refused' 4 $message
            }
        }

        # 3. All three images at that commit, each proven to come from this repo's workflow on main.
        $verified = Get-CmVerifiedImages -Ctx $Ctx -Sha $sha
        if (-not $verified.Ok) {
            Write-CmLog $verified.Reason 'ERROR'
            return New-CmResult 'refused' 4 $verified.Reason
        }
        $infos = $verified.Infos
        if ($infos['api'].Digest -ne $api.Digest) {
            $message = 'the main tag moved while checking; nothing deployed, will retry'
            Write-CmLog $message 'WARN'
            return New-CmResult 'refused' 4 $message
        }
        Write-CmLog "deploying $($sha.Substring(0, 12)) (attestations verified)"

        # 4. Migrate (expand-only migrations, so the running api stays compatible), then restart the api. Both use the
        #    compose file AT the new commit; the clone itself is only moved after a good deploy.
        $newCtx = New-CmCtxAtCommit -Ctx $Ctx -Sha $sha
        if (-not $newCtx) {
            $message = "could not read deploy/docker-compose.yml at $($sha.Substring(0, 12)) from the clone at $($Ctx.RepoDir); nothing deployed"
            Write-CmLog $message 'ERROR'
            return New-CmResult 'refused' 4 $message
        }
        # The release's own file must pass the config guard too (a variable .env lacks, a bad port binding) BEFORE
        # anything is migrated; otherwise the schema moves, the api fails and the rollback runs on the new schema.
        $newProblems = @(Get-CmComposeGuard -Ctx $newCtx -EnvMap $envMap)
        if ($newProblems.Count -gt 0) {
            foreach ($p in $newProblems) { Write-CmLog "config refused for $($sha.Substring(0, 12)): $p" 'ERROR' }
            return New-CmResult 'config-error' 2 ($newProblems -join '; ')
        }
        $oldCtx = if ($current) { New-CmCtxAtCommit -Ctx $Ctx -Sha "$($current['sha'])" } else { $null }
        $mig = Invoke-Cm -Mutating -Command 'docker' -Env @{ CM_TAG = $sha } -Arguments (Get-CmComposeArgs $newCtx @('run', '--rm', 'migrate'))
        if ($mig.ExitCode -ne 0) {
            $message = "migration failed for $($sha.Substring(0, 12)); the running api was not touched"
            Write-CmLog $message 'ERROR'
            $state['failedSha'] = $sha
            Write-CmState -Path $Ctx.StateFile -State $state
            return New-CmResult 'migrate-failed' 1 $message
        }
        $up = Invoke-CmUp -Ctx $newCtx -Tag $sha
        $health = if ($up.ExitCode -eq 0) { Test-CmStackHealthy -Ctx $Ctx } else { [pscustomobject]@{ Ok = $false; Note = 'starting the containers failed' } }

        if ($health.Ok) {
            $state['current'] = @{
                sha        = $sha
                created    = $api.Created
                deployedAt = [DateTime]::UtcNow.ToString('o')
                digests    = @{ api = $infos['api'].Digest; migrate = $infos['migrate'].Digest; backup = $infos['backup'].Digest }
            }
            $state.Remove('failedSha')
            Write-CmState -Path $Ctx.StateFile -State $state
            Write-CmLog "deployed $($sha.Substring(0, 12)); smoke test passed"
            try { Update-CmRepoToDeployed -Ctx $Ctx -Sha $sha | Out-Null }
            catch { Write-CmLog "could not move the clone at $($Ctx.RepoDir) to $($sha.Substring(0, 12)): $($_.Exception.Message)" 'WARN' }
            Remove-CmRetiredIpa -Ctx $Ctx
            return New-CmResult 'deployed' 0
        }

        # 5. Rollback: pin the previous good tag and restart.
        $state['failedSha'] = $sha
        Write-CmState -Path $Ctx.StateFile -State $state
        if ($current) {
            Write-CmLog "$($health.Note); pinning previous good $("$($current['sha'])".Substring(0, 12))" 'ERROR'
            # The previous release's own compose file; if it cannot be read, the clone's (still the previous release's).
            Invoke-CmUp -Ctx $(if ($oldCtx) { $oldCtx } else { $Ctx }) -Tag $current['sha'] | Out-Null
            if ((Test-CmStackHealthy -Ctx $Ctx).Ok) { Write-CmLog 'rolled back; the previous version is healthy' 'WARN'; $tail = 'the previous version is healthy again' }
            else { Write-CmLog 'rolled back but the previous version is not healthy either; check the stack' 'ERROR'; $tail = 'the previous version is NOT healthy either; check the stack' }
            return New-CmResult 'rolled-back' 1 "$($health.Note) for $($sha.Substring(0, 12)); rolled back to $("$($current['sha'])".Substring(0, 12)); $tail"
        }
        $message = "$($health.Note) for $($sha.Substring(0, 12)) and there is no previous good version to return to"
        Write-CmLog $message 'ERROR'
        New-CmResult 'failed' 1 $message
    }
    finally {
        Remove-CmCtxAtCommit -Ctx $Ctx -CommitCtx $newCtx
        Remove-CmCtxAtCommit -Ctx $Ctx -CommitCtx $oldCtx
        Exit-CmLock $lock
    }
}

function Invoke-CmRollbackCore {
    <#
      The explicit rollback path: go back to an OLDER commit of the deployed one on purpose. The target is held to the
      same proof as any deploy (images at that commit, attested from main). No migration runs (they are expand-only, so
      the older api runs on the newer schema). The commit rolled back from is remembered as bad so the poller does not
      put it straight back; a newer commit on main still deploys normally.
    #>
    param($Ctx, [string]$Target)
    $envMap = Initialize-CmRun -Ctx $Ctx

    if (-not $global:CmApply) {
        Write-CmLog "DRY RUN: nothing will be changed. Add -Apply to roll back to $Target for real."
        $problems = @(Get-CmComposeGuard -Ctx $Ctx -EnvMap $envMap)
        foreach ($p in $problems) { Write-CmLog "config problem: $p" 'WARN' }
        Write-CmLog 'A real rollback would make these calls, in order:'
        foreach ($line in Get-CmRollbackPlan -Ctx $Ctx -Target $Target) { Write-CmLog "  $line" }
        return New-CmResult 'dry-run' 0
    }

    function Refuse($message) { Write-CmLog $message 'ERROR'; New-CmResult 'rollback-refused' 4 $message }

    $lock = Enter-CmLock -Path $Ctx.LockFile
    if (-not $lock) { return New-CmBusyResult -Ctx $Ctx }
    try {
        $problems = @(Get-CmComposeGuard -Ctx $Ctx -EnvMap $envMap)
        if ($problems.Count -gt 0) {
            foreach ($p in $problems) { Write-CmLog "config refused: $p" 'ERROR' }
            return New-CmResult 'config-error' 2 ($problems -join '; ')
        }
        $read = Read-CmStateStrict -Path $Ctx.StateFile
        if (-not $read.Ok) { return Refuse $read.Reason }
        $state = $read.State
        $current = $state['current']
        if (-not $current -or -not $current['sha']) { return Refuse 'rollback refused: no deployed commit is on record, so there is nothing to roll back from' }
        if ($Target -cnotmatch '^[0-9a-f]{40}$') { return Refuse "rollback refused: '$Target' is not a full (40 character, lower case) commit hash" }

        if (-not (Update-CmRepo -Ctx $Ctx)) { return Refuse "rollback refused: could not fetch main into $($Ctx.RepoDir) to check the order of the commits" }
        $order = Test-CmAncestry -Ctx $Ctx -Current "$($current['sha'])" -Target $Target
        if ($order.Relation -ne 'ancestor') {
            $why = if ($order.Relation -eq 'descendant') { "commit $($Target.Substring(0, 12)) is newer than the deployed one; that is a normal deploy, not a rollback" } else { $order.Reason }
            return Refuse "rollback refused: $why"
        }

        $verified = Get-CmVerifiedImages -Ctx $Ctx -Sha $Target
        if (-not $verified.Ok) { return Refuse "rollback refused: $($verified.Reason)" }
        Write-CmLog "rolling back from $("$($current['sha'])".Substring(0, 12)) to $($Target.Substring(0, 12)) (attestations verified)" 'WARN'

        # The older commit's own compose file (the clone is at the newer one); the running version's file to go back.
        $targetCtx = New-CmCtxAtCommit -Ctx $Ctx -Sha $Target
        if (-not $targetCtx) { return Refuse "rollback refused: could not read deploy/docker-compose.yml at $($Target.Substring(0, 12)) from the clone at $($Ctx.RepoDir)" }
        $fromCtx = New-CmCtxAtCommit -Ctx $Ctx -Sha "$($current['sha'])"

        $up = Invoke-CmUp -Ctx $targetCtx -Tag $Target
        $health = if ($up.ExitCode -eq 0) { Test-CmStackHealthy -Ctx $Ctx } else { [pscustomobject]@{ Ok = $false; Note = 'starting the containers failed' } }
        if (-not $health.Ok) {
            Invoke-CmUp -Ctx $(if ($fromCtx) { $fromCtx } else { $Ctx }) -Tag $current['sha'] | Out-Null
            $back = if ((Test-CmStackHealthy -Ctx $Ctx).Ok) { "$("$($current['sha'])".Substring(0, 12)) is running again" } else { 'the previous version is NOT healthy either; check the stack' }
            $message = "the older version $($Target.Substring(0, 12)): $($health.Note); $back"
            Write-CmLog $message 'ERROR'
            return New-CmResult 'rollback-failed' 1 $message
        }

        $from = "$($current['sha'])"
        $state['current'] = @{
            sha          = $Target
            created      = $verified.Infos['api'].Created
            deployedAt   = [DateTime]::UtcNow.ToString('o')
            rolledBackFrom = $from
            digests      = @{ api = $verified.Infos['api'].Digest; migrate = $verified.Infos['migrate'].Digest; backup = $verified.Infos['backup'].Digest }
        }
        $state['failedSha'] = $from
        Write-CmState -Path $Ctx.StateFile -State $state
        Write-CmLog "rolled back to $($Target.Substring(0, 12)); smoke test passed. $($from.Substring(0, 12)) will not be redeployed; a newer commit on main will."
        New-CmResult 'manual-rollback' 0
    }
    finally {
        Remove-CmCtxAtCommit -Ctx $Ctx -CommitCtx $targetCtx
        Remove-CmCtxAtCommit -Ctx $Ctx -CommitCtx $fromCtx
        Exit-CmLock $lock
    }
}
