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
        RepoDir           = Join-Path $Root 'repo'
        ComposeFile       = $ComposeFile
        EnvFile           = $EnvFile
        Repo              = $Repo
        ImagePrefix       = "ghcr.io/$owner/$name-"
        StateFile         = Join-Path $Root 'state/deployer-state.json'
        LockFile          = Join-Path $Root 'state/deployer.lock'
        LogFile           = Join-Path $Root 'logs/deployer.log'
        StageDir          = Join-Path $Root 'state/ipa-stage'
        IpaRetryMinutes   = 15   # how long a release whose .ipa sync did not work waits before the next silent try
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
    $plan += '(if the smoke test fails: the same up -d with CM_TAG=<previous good sha>)'
    $plan += '(then, if the clone is clean and on main, it is fast-forwarded to the deployed commit so the deployer scripts on disk match the running build)'
    $plan += Format-CmCommand 'git' @('-C', $Ctx.RepoDir, 'merge', '--ff-only', $tag)
    $plan += '(then: Sync-Ipa.ps1 copies the newest .ipa from the latest GitHub release into the files volume)'
    $plan += '(a run with nothing to deploy still reads the latest release tag, and runs that same sync when the tag is new)'
    $plan += Format-CmCommand 'gh' @('release', 'view', '--repo', $Ctx.Repo, '--json', 'tagName', '--jq', '.tagName')
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

function Complete-CmQuietRun {
    <# A run with nothing to deploy: still sync the .ipa when the latest release is new. Never turns a quiet run into a crash or an alert storm. #>
    param($Ctx, [string]$Outcome)
    try { $ipa = Invoke-CmIpaCatchUp -Ctx $Ctx }
    catch {
        Write-CmLog "could not check the latest release for a new .ipa: $($_.Exception.Message)" 'WARN'
        return New-CmResult $Outcome 0
    }
    if ($ipa.Outcome -eq 'ipa-failed') {
        $message = ".ipa sync failed on a run with nothing to deploy ($($ipa.Detail)); the previous .ipa is still served. This is reported once per release; it is tried again quietly"
        Write-CmLog $message 'ERROR'
        return New-CmResult 'ipa-failed' 5 $message
    }
    if ($ipa.Outcome -eq 'ipa-synced') { return New-CmResult 'ipa-synced' 0 }
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
    <# Exit codes: 0 done or nothing to do, 1 failed or rolled back, 2 config refused, 3 Docker/GHCR unreachable, 4 refused, 5 .ipa sync failed. #>
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
        0  dry-run, busy, up-to-date, known-bad, deployed, manual-rollback, ipa-synced (nothing to deploy, but a new release's .ipa was synced)
        1  migrate-failed, rolled-back, failed, rollback-failed, crashed
        2  config-error
        3  no-docker
        4  refused, downgrade-refused, rollback-refused
        5  ipa-failed (after a good deploy, or the first failed sync of a new release on a run with nothing to deploy)
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
    if (-not $lock) {
        Write-CmLog 'another deployer run is in progress; exiting' 'INFO'
        return New-CmResult 'busy' 0
    }
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

        # 4. Migrate (expand-only migrations, so the running api stays compatible), then restart the api.
        $mig = Invoke-Cm -Mutating -Command 'docker' -Env @{ CM_TAG = $sha } -Arguments (Get-CmComposeArgs $Ctx @('run', '--rm', 'migrate'))
        if ($mig.ExitCode -ne 0) {
            $message = "migration failed for $($sha.Substring(0, 12)); the running api was not touched"
            Write-CmLog $message 'ERROR'
            $state['failedSha'] = $sha
            Write-CmState -Path $Ctx.StateFile -State $state
            return New-CmResult 'migrate-failed' 1 $message
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
            try { Update-CmRepoToDeployed -Ctx $Ctx -Sha $sha | Out-Null }
            catch { Write-CmLog "could not move the clone at $($Ctx.RepoDir) to $($sha.Substring(0, 12)): $($_.Exception.Message)" 'WARN' }
            try { $ipa = Invoke-CmIpaSync -Ctx $Ctx -Tag $sha }
            catch { $ipa = [pscustomobject]@{ Outcome = 'error'; ExitCode = 1; Detail = $_.Exception.Message } }
            if ($ipa.ExitCode -ne 0) {
                $message = ".ipa sync failed after a good deploy of $($sha.Substring(0, 12)) ($($ipa.Outcome)); the previous .ipa is still served"
                Write-CmLog $message 'ERROR'
                return New-CmResult 'ipa-failed' 5 $message
            }
            return New-CmResult 'deployed' 0
        }

        # 5. Rollback: pin the previous good tag and restart.
        $state['failedSha'] = $sha
        Write-CmState -Path $Ctx.StateFile -State $state
        if ($current) {
            Write-CmLog "smoke test failed; pinning previous good $("$($current['sha'])".Substring(0, 12))" 'ERROR'
            Invoke-CmUp -Ctx $Ctx -Tag $current['sha'] | Out-Null
            if (Invoke-CmSmokeTest -Ctx $Ctx) { Write-CmLog 'rolled back; the previous version is healthy' 'WARN'; $tail = 'the previous version is healthy again' }
            else { Write-CmLog 'rolled back but the previous version is not healthy either; check the stack' 'ERROR'; $tail = 'the previous version is NOT healthy either; check the stack' }
            return New-CmResult 'rolled-back' 1 "the smoke test failed for $($sha.Substring(0, 12)); rolled back to $("$($current['sha'])".Substring(0, 12)); $tail"
        }
        $message = "smoke test failed for $($sha.Substring(0, 12)) and there is no previous good version to return to"
        Write-CmLog $message 'ERROR'
        New-CmResult 'failed' 1 $message
    }
    finally {
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
    if (-not $lock) {
        Write-CmLog 'another deployer run is in progress; exiting' 'INFO'
        return New-CmResult 'busy' 0
    }
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

        $up = Invoke-CmUp -Ctx $Ctx -Tag $Target
        $healthy = ($up.ExitCode -eq 0) -and (Invoke-CmSmokeTest -Ctx $Ctx)
        if (-not $healthy) {
            Invoke-CmUp -Ctx $Ctx -Tag $current['sha'] | Out-Null
            $back = if (Invoke-CmSmokeTest -Ctx $Ctx) { "$("$($current['sha'])".Substring(0, 12)) is running again" } else { 'the previous version is NOT healthy either; check the stack' }
            $message = "the older version $($Target.Substring(0, 12)) did not pass the smoke test; $back"
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
        Exit-CmLock $lock
    }
}
