# Deployer tests. Dot-sourced by Run-Tests.ps1 after TestKit.ps1 and the deployer libraries.

function Invoke-TestDeploy {
    <#
      Runs one real deployer pass against a throwaway root with the fake machine installed. -Setup gets the root before
      the run; -Inspect gets it afterwards, before it is deleted, and its answer comes back as Inspected.
    #>
    param([hashtable]$Machine = @{}, [hashtable]$State = $null, [switch]$DryRun, [string]$RollbackTo = '', $StateText = $null, $EnvText = $null,
        [scriptblock]$Setup = $null, [scriptblock]$Inspect = $null, [switch]$LockHeld, [double]$LockAgeMinutes = 0)
    $root = New-TestRoot
    try {
        if ($State) { Set-TestState -Root $root -State $State }
        if ($null -ne $StateText) {
            New-Item -ItemType Directory -Force -Path (Join-Path $root 'state') | Out-Null
            Set-Content -LiteralPath (Join-Path $root 'state/deployer-state.json') -Value $StateText
        }
        if ($null -ne $EnvText) { Set-Content -LiteralPath (Join-Path $root '.env') -Value $EnvText }
        if ($Setup) { & $Setup $root }
        $heldLock = $null
        if ($LockHeld) {
            # Another run holding the lock: backdate the stamp, then hold the file open WITHOUT stamping,
            # the way a stuck run (or one from before the stamp existed) looks to the run under test.
            $lockPath = Join-Path $root 'state/deployer.lock'
            New-Item -ItemType Directory -Force -Path (Join-Path $root 'state') | Out-Null
            Set-Content -LiteralPath $lockPath -Value "pid=99999 started=$([DateTime]::UtcNow.AddMinutes(-$LockAgeMinutes).ToString('o'))" -NoNewline
            [IO.File]::SetLastWriteTimeUtc($lockPath, [DateTime]::UtcNow.AddMinutes(-$LockAgeMinutes))
            $heldLock = [System.IO.File]::Open($lockPath, 'Open', 'ReadWrite', 'None')
        }
        New-FakeMachine $Machine | Out-Null
        Initialize-Cm -Apply (-not $DryRun)
        $ctx = New-TestContext -Root $root
        try {
            $result = Invoke-CmDeploy -Ctx $ctx -RollbackTo $RollbackTo 6>$null
        }
        finally { if ($heldLock) { $heldLock.Dispose() } }
        $log = if (Test-Path -LiteralPath $ctx.LogFile) { Get-Content -LiteralPath $ctx.LogFile -Raw } else { '' }
        $inspected = if ($Inspect) { & $Inspect $root } else { $null }
        [pscustomobject]@{ Result = $result; State = (Get-TestState -Root $root); Log = $log; Root = $root; Inspected = $inspected }
    }
    finally { Remove-TestRoot $root }
}

function Get-OldGoodState {
    @{ current = @{ sha = $script:Sha1; created = '2026-10-12T12:00:00.0000000Z'; deployedAt = '2026-10-12T12:05:00Z' } }
}

function Get-CallIndex { param([string]$Like) $i = 0; foreach ($c in $global:CmCalls) { if ($c.Display -like $Like) { return $i }; $i++ }; -1 }

Test-Case 'deployer: a new attested image deploys in order: verify, migrate, up, smoke test' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState)
    Assert-Equal 'deployed' $r.Result.Outcome
    $verify = Get-CallIndex '*attestation verify*'
    $migrate = Get-CallIndex '*run --rm migrate*'
    $up = Get-CallIndex '*up -d*'
    $smoke = Get-CallIndex 'curl*health/ready'
    Assert-True ($verify -ge 0 -and $verify -lt $migrate -and $migrate -lt $up -and $up -lt $smoke) "order was verify=$verify migrate=$migrate up=$up smoke=$smoke"
    Assert-Equal 3 (Get-Calls 'gh' '*attestation verify*').Count 'api, migrate and backup are all verified'
    Assert-Equal $script:Sha2 $r.State.current.sha
    Assert-Equal $script:Sha2 (Get-Calls 'docker' '*run --rm migrate*')[0].Env['CM_TAG']
    Assert-False $r.State.ContainsKey('failedSha')
}

Test-Case 'deployer: the first ever deploy works with no state file' {
    $r = Invoke-TestDeploy
    Assert-Equal 'deployed' $r.Result.Outcome
    Assert-Equal $script:Sha2 $r.State.current.sha
}

Test-Case 'deployer: a missing state file with a stack already running is refused, loudly' {
    $r = Invoke-TestDeploy -Machine @{ RunningServices = @('abc123') }
    Assert-Equal 'refused' $r.Result.Outcome
    Assert-Equal 4 $r.Result.ExitCode
    Assert-Match $r.Log 'ALERT deploy refused'
    Assert-Match $r.Log 'state file'
    Assert-Match $r.Log 'current'
    Assert-Equal 0 (Get-Calls 'docker' '*up -d*').Count
    Assert-Equal 0 (Get-Calls 'docker' '*run --rm migrate*').Count
    Assert-Equal 1 (Get-Calls 'curl' '*/fail*').Count
}

Test-Case 'deployer: a missing state file with CM_TAG pinned in .env is refused' {
    $envText = @('POSTGRES_PASSWORD=test-only-password', 'CM_BACKUP_DIR=/tmp/cm-test-backups', 'CM_API_PORT=5480', 'CM_DASHBOARD_PORT=18888',
        'CM_HEARTBEAT_URL=https://hc.example/test-uuid', 'CM_SECRETS_DIR=/tmp/cm-test-secrets', 'CM_TAG=latest',
        'CM_PUBLIC_ORIGIN=https://pc.tailnet.ts.net', 'CM_AUTH0_DOMAIN=test.us.auth0.com', 'CM_AUTH0_WEB_CLIENT_ID=test-web', 'CM_AUTH0_WEB_CLIENT_SECRET=test-secret', 'CM_AUTH0_MANAGEMENT_CLIENT_ID=test-mgmt', 'CM_AUTH0_MANAGEMENT_CLIENT_SECRET=test-secret')
    $r = Invoke-TestDeploy -EnvText $envText
    Assert-Equal 'refused' $r.Result.Outcome
    Assert-Equal 4 $r.Result.ExitCode
    Assert-Match $r.Log 'ALERT deploy refused'
    Assert-Equal 0 (Get-Calls 'docker' '*up -d*').Count
}

Test-Case 'deployer: a missing state file and nothing running or pinned is still a first deploy' {
    $r = Invoke-TestDeploy -Machine @{ RunningServices = @() }
    Assert-Equal 'deployed' $r.Result.Outcome
    Assert-Equal 0 $r.Result.ExitCode
    Assert-Equal $script:Sha2 $r.State.current.sha
}

Test-Case 'deployer: a good deploy fast-forwards its clone at poll start and reads no GitHub release' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState)
    $merge = Get-CallIndex 'git * merge --ff-only*'
    $pull = Get-CallIndex 'docker pull*'
    Assert-True ($merge -ge 0 -and $pull -gt $merge) "the clone moves before the first pull (merge=$merge pull=$pull)"
    Assert-Match (Get-Calls 'git' '*merge --ff-only*')[0].Display 'origin/main'
    Assert-Equal 0 (Get-Calls 'gh' 'gh release *').Count 'the iPhone app comes from TestFlight (ADR-024)'
}

Test-Case 'deployer: an image without a valid attestation deploys nothing' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine @{ BadAttest = @('api') }
    Assert-Equal 'refused' $r.Result.Outcome
    Assert-Equal 0 (Get-Calls 'docker' '*run --rm migrate*').Count
    Assert-Equal 0 (Get-Calls 'docker' '*up -d*').Count
    Assert-Equal $script:Sha1 $r.State.current.sha
}

Test-Case 'deployer: a bad attestation on any one of the three images deploys nothing' {
    foreach ($bad in 'migrate', 'backup') {
        $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine @{ BadAttest = @($bad) }
        Assert-Equal 'refused' $r.Result.Outcome "(bad: $bad)"
        Assert-Equal 0 (Get-Calls 'docker' '*up -d*').Count
    }
}

Test-Case 'deployer: an image attested for the wrong ref deploys nothing' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine @{ AttestRef = 'refs/heads/some-feature' }
    Assert-Equal 'refused' $r.Result.Outcome
    Assert-Equal 0 (Get-Calls 'docker' '*run --rm migrate*').Count
    Assert-Equal 0 (Get-Calls 'docker' '*up -d*').Count
    Assert-Match $r.Log 'refs/heads/main'
}

Test-Case 'deployer: a certificate with no source commit is refused (fails closed)' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine @{ OmitCertDigest = $true }
    Assert-Equal 'refused' $r.Result.Outcome
    Assert-Equal 0 (Get-Calls 'docker' '*run --rm migrate*').Count
    Assert-Equal 0 (Get-Calls 'docker' '*up -d*').Count
}

Test-Case 'deployer: a failed smoke test pins the previous good tag and restarts' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine @{ UnhealthyTags = @($script:Sha2) }
    Assert-Equal 'rolled-back' $r.Result.Outcome
    Assert-Equal 1 $r.Result.ExitCode
    $ups = Get-Calls 'docker' '*up -d*'
    Assert-Equal 2 $ups.Count
    Assert-Equal $script:Sha2 $ups[0].Env['CM_TAG']
    Assert-Equal $script:Sha1 $ups[1].Env['CM_TAG'] 'rollback pins the previous tag'
    Assert-Equal $script:Sha1 $r.State.current.sha 'last good stays the old one'
    Assert-Equal $script:Sha2 $r.State.failedSha
}

Test-Case 'deployer: every started service is checked after the smoke test passes' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState)
    Assert-Equal 'deployed' $r.Result.Outcome
    $smoke = Get-CallIndex 'curl*health/ready'
    $ps = Get-CallIndex '*ps --format json*'
    Assert-True ($ps -gt $smoke) "the service check runs after the smoke test (smoke=$smoke ps=$ps)"
}

Test-Case 'deployer: a crash-looping backup rolls the deploy back even though the API answers' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine @{ BrokenServices = @{ $script:Sha2 = @('backup') } }
    Assert-Equal 'rolled-back' $r.Result.Outcome
    Assert-Equal 1 $r.Result.ExitCode
    $ups = Get-Calls 'docker' '*up -d*'
    Assert-Equal 2 $ups.Count
    Assert-Equal $script:Sha1 $r.State.current.sha 'last good stays the old one'
    Assert-Equal $script:Sha2 $r.State.failedSha
    Assert-Match $r.Log 'backup'
    Assert-Match $r.Log 'restarting'
    Assert-Match $r.Result.Reason 'backup'
    Assert-Equal 1 (Get-Calls 'curl' '*/fail*').Count
}

Test-Case 'deployer: a service with no container fails the deploy instead of declaring success' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine @{ MissingServices = @{ $script:Sha2 = @('aspire-dashboard') } }
    Assert-Equal 'rolled-back' $r.Result.Outcome
    Assert-Equal 1 $r.Result.ExitCode
    Assert-Equal $script:Sha1 $r.State.current.sha
    Assert-Match $r.Log 'aspire-dashboard'
    Assert-Match $r.Result.Reason 'aspire-dashboard'
}

Test-Case 'deployer: when the container list cannot be read the deploy fails closed' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine @{ PsFails = $true }
    Assert-Equal 'rolled-back' $r.Result.Outcome
    Assert-Equal 1 $r.Result.ExitCode
    Assert-Equal $script:Sha1 $r.State.current.sha
    Assert-Match $r.Log 'could not list'
}

Test-Case 'deployer: the service check ignores postgres and the finished migrate container' {
    # The fake `ps` always lists postgres (running) and migrate (exited 0) next to the three services.
    $r = Invoke-TestDeploy -State (Get-OldGoodState)
    Assert-Equal 'deployed' $r.Result.Outcome
    Assert-True ((Get-Calls 'docker' '*ps --format json*').Count -ge 1) 'the service check ran'
}

Test-Case 'deployer: a known-bad image is not retried every two minutes' {
    $state = Get-OldGoodState; $state['failedSha'] = $script:Sha2
    $r = Invoke-TestDeploy -State $state
    Assert-Equal 'known-bad' $r.Result.Outcome
    Assert-Equal 0 (Get-Calls 'docker' '*run --rm migrate*').Count
}

Test-Case 'deployer: a downgrade is refused (judged by commit ancestry; see ops1b.tests.ps1 for the full set)' {
    # The running commit (Sha2) is a descendant of the main image's commit (Sha1): main is behind what runs.
    $r = Invoke-TestDeploy -State @{ current = @{ sha = $script:Sha2; created = '2026-10-14T00:00:00.0000000Z' } } -Machine @{ Sha = $script:Sha1 }
    Assert-Equal 'downgrade-refused' $r.Result.Outcome
    Assert-Equal 0 (Get-Calls 'docker' '*run --rm migrate*').Count
    Assert-Equal 0 (Get-Calls 'docker' '*up -d*').Count
    Assert-Equal $script:Sha2 $r.State.current.sha
}

Test-Case 'deployer: the same image that is already running only moves the clone' {
    $r = Invoke-TestDeploy -State @{ current = @{ sha = $script:Sha2; created = '2026-10-13T12:00:00Z' } }
    Assert-Equal 'up-to-date' $r.Result.Outcome
    Assert-Equal 0 (Get-MutatingCalls | Where-Object { $_.Display -match 'compose' }).Count
    Assert-Equal 0 (@(Get-MutatingCalls) | Where-Object { $_.Display -notmatch '^docker pull|^git ' }).Count 'only the pull and the clone move'
    Assert-Equal 1 (Get-Calls 'git' '*merge --ff-only*').Count 'the clone still follows main on a quiet run'
}

Test-Case 'deployer: a failed migration leaves the running api alone and is remembered' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine @{ MigrateExit = 1 }
    Assert-Equal 'migrate-failed' $r.Result.Outcome
    Assert-Equal 0 (Get-Calls 'docker' '*up -d*').Count
    Assert-Equal $script:Sha2 $r.State.failedSha
}

Test-Case 'deployer: Docker not running exits with code 3 and changes nothing' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine @{ PullFails = $true }
    Assert-Equal 3 $r.Result.ExitCode
    Assert-Equal 0 (Get-Calls 'docker' '*up -d*').Count
}

Test-Case 'deployer: a second concurrent run exits at once without touching anything' {
    $root = New-TestRoot
    try {
        New-FakeMachine @{} | Out-Null
        Initialize-Cm -Apply $true
        $ctx = New-TestContext -Root $root
        $first = Enter-CmLock -Path $ctx.LockFile
        try {
            $result = Invoke-CmDeploy -Ctx $ctx 6>$null
        }
        finally { Exit-CmLock $first }
        Assert-Equal 'busy' $result.Outcome
        Assert-Equal 0 $result.ExitCode
        Assert-Equal 0 (Get-MutatingCalls).Count
        Assert-Equal 0 $global:CmCalls.Count 'a busy run makes no calls at all'
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'deployer: the lock is released after a run so the next one can start' {
    $root = New-TestRoot
    try {
        New-FakeMachine @{} | Out-Null
        Initialize-Cm -Apply $true
        $ctx = New-TestContext -Root $root
        Invoke-CmDeploy -Ctx $ctx 6>$null | Out-Null
        $again = Enter-CmLock -Path $ctx.LockFile
        Assert-True ($null -ne $again) 'lock should be free after a run'
        Exit-CmLock $again
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'deployer: a freshly held lock is still a quiet busy exit on both the deploy and rollback paths' {
    foreach ($rb in '', $script:Sha1) {
        $global:CmCalls.Clear()
        $r = Invoke-TestDeploy -RollbackTo $rb -LockHeld
        Assert-Equal 'busy' $r.Result.Outcome "(rollbackTo: '$rb')"
        Assert-Equal 0 $r.Result.ExitCode "(rollbackTo: '$rb')"
        Assert-Equal 0 $global:CmCalls.Count 'a busy run makes no calls at all'
        Assert-Equal 0 (Get-Calls 'curl' '*/fail*').Count 'no alert'
        Assert-False ($r.Log -match 'ALERT') 'no alert line'
    }
}

Test-Case 'deployer: a lock held past the stale threshold alerts like a failure on both the deploy and rollback paths' {
    foreach ($rb in '', $script:Sha1) {
        $global:CmCalls.Clear()
        $r = Invoke-TestDeploy -RollbackTo $rb -LockHeld -LockAgeMinutes 45
        Assert-Equal 'stale-lock' $r.Result.Outcome "(rollbackTo: '$rb')"
        Assert-Equal 1 $r.Result.ExitCode "(rollbackTo: '$rb')"
        Assert-Match $r.Log 'ALERT deploy stale-lock'
        Assert-Match $r.Log 'stuck'
        Assert-Equal 1 (Get-Calls 'curl' '*/fail*').Count 'one alert ping'
        Assert-Equal 0 (Get-Calls 'docker' '*').Count 'nothing is started or stopped'
        Assert-Equal 0 (Get-Calls 'git' '*').Count 'nothing is read or moved'
    }
}

Test-Case 'deployer: a lock held just under the stale threshold stays quiet' {
    $r = Invoke-TestDeploy -LockHeld -LockAgeMinutes 29
    Assert-Equal 'busy' $r.Result.Outcome
    Assert-Equal 0 $r.Result.ExitCode
    Assert-False ($r.Log -match 'ALERT')
}

Test-Case 'deployer: taking the lock stamps it with the holder, so a stuck run can be told from a live one' {
    $root = New-TestRoot
    try {
        $ctx = New-TestContext -Root $root
        $lock = Enter-CmLock -Path $ctx.LockFile
        try {
            Assert-True ($null -ne $lock) 'the lock was taken'
            $age = Get-CmLockAgeMinutes -Path $ctx.LockFile
            Assert-True ($age -lt 1) "a fresh stamp is seconds old, got [$age] minutes"
        }
        finally { Exit-CmLock $lock }
        $stamp = Get-Content -LiteralPath $ctx.LockFile -Raw
        Assert-Match $stamp "pid=$PID"
        Assert-Match $stamp 'started='
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'deployer: a lock file that is not there has no age, so it is never called stale' {
    $missing = Join-Path ([IO.Path]::GetTempPath()) 'cm-ops1-test-no-such-dir/deployer.lock'
    Assert-Equal '' (Get-CmLockAgeMinutes -Path $missing)
}

Test-Case 'deployer: the log never contains the database password or the heartbeat URL' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState)
    Assert-True ($r.Log.Length -gt 0) 'there should be a log'
    Assert-False ($r.Log -match 'test-only-password')
    Assert-False ($r.Log -match 'test-uuid')
}

Test-Case 'deployer: the log rotates instead of growing forever' {
    $root = New-TestRoot
    try {
        $log = Join-Path $root 'logs/deployer.log'
        1..40 | ForEach-Object { Add-CmLogLine -Path $log -Line ('x' * 100) -MaxBytes 1000 -Keep 3 }
        Assert-True (Test-Path "$log.1") 'rotated file exists'
        Assert-False (Test-Path "$log.4") 'no more than 3 rotated files are kept'
        Assert-True ((Get-Item $log).Length -le 1200) 'the live log stays small'
    }
    finally { Remove-TestRoot $root }
}

# ---- dry run / -WhatIf ------------------------------------------------------------------------------------------------

Test-Case 'deployer dry run: records zero mutating calls and prints the plan' {
    $root = New-TestRoot
    try {
        New-FakeMachine @{} | Out-Null
        Initialize-Cm -Apply $false
        $ctx = New-TestContext -Root $root
        $output = (Invoke-CmDeploy -Ctx $ctx 6>&1 | Out-String)
        Assert-Equal 0 (Get-MutatingCalls).Count
        Assert-NoForbiddenCalls
        $text = (Get-CmPlan -Ctx $ctx) -join "`n"
        Assert-Match $text 'compose .* run --rm migrate'
        Assert-Match $text 'compose .* up -d api backup aspire-dashboard'
        Assert-Match $text 'gh attestation verify'
        Assert-False (Test-Path (Join-Path $root 'logs')) 'a dry run writes no log files'
        Assert-False (Test-Path (Join-Path $root 'state')) 'a dry run writes no state or lock'
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'deployer script: with no -Apply it is a dry run; with -Apply -WhatIf it is too' {
    $script = Join-Path $PSScriptRoot '../deployer/Deploy-CollectionMaxxing.ps1'
    foreach ($flags in '', '-Apply -WhatIf') {
        $extra = if ($flags) { @{ Apply = $true; WhatIf = $true } } else { @{} }
        $root = New-TestRoot
        try {
            New-FakeMachine @{} | Out-Null
            & $script -Root $root -ComposeFile (New-TestComposePath $root) @extra 6>$null
            Assert-Equal 0 $LASTEXITCODE
            Assert-Equal 0 (Get-MutatingCalls).Count "(args: $flags)"
            Assert-NoForbiddenCalls
            Assert-Equal 0 (Get-Calls 'docker' '*pull*').Count
        }
        finally { Remove-TestRoot $root }
    }
}

Test-Case 'deployer script: -Apply deploys through the same seam' {
    $script = Join-Path $PSScriptRoot '../deployer/Deploy-CollectionMaxxing.ps1'
    $root = New-TestRoot
    try {
        New-FakeMachine @{} | Out-Null
        & $script -Root $root -ComposeFile (New-TestComposePath $root) -SmokeDelaySeconds 0 -Apply 6>$null
        Assert-Equal 0 $LASTEXITCODE
        Assert-Equal 1 (Get-Calls 'docker' '*run --rm migrate*').Count
        Assert-Equal $script:Sha2 (Get-TestState -Root $root).current.sha
    }
    finally { Remove-TestRoot $root }
}

# ---- config guard -------------------------------------------------------------------------------------------------------

Test-Case 'config guard: the four ports other apps use are refused' {
    foreach ($port in 8080, 8409, 32400, 8096) {
        $problems = @(Test-CmConfig -EnvMap @{ CM_API_PORT = "$port" })
        Assert-True ($problems.Count -ge 1 -and $problems[0] -match "$port") "port $port should be refused"
        $problems = @(Test-CmConfig -EnvMap @{ CM_DASHBOARD_PORT = "$port" })
        Assert-True ($problems.Count -ge 1) "dashboard port $port should be refused"
        $cfg = New-GoodComposeConfig; $cfg.services.api.ports[0].published = "$port"
        Assert-True (@(Test-CmConfig -ComposeConfig $cfg).Count -ge 1) "published $port should be refused"
    }
}

Test-Case 'config guard: a bind that is not 127.0.0.1 is refused' {
    foreach ($hostIp in '0.0.0.0', '', '192.168.1.5', '::') {
        $cfg = New-GoodComposeConfig; $cfg.services.api.ports[0].host_ip = $hostIp
        $problems = @(Test-CmConfig -ComposeConfig $cfg)
        Assert-True ($problems.Count -ge 1) "bind '$hostIp' should be refused"
    }
}

Test-Case 'config guard: published postgres, host networking and privileged mode are refused' {
    $cfg = New-GoodComposeConfig
    $cfg.services.postgres | Add-Member -NotePropertyName ports -NotePropertyValue @([pscustomobject]@{ host_ip = '127.0.0.1'; published = '5432' })
    Assert-Match ((Test-CmConfig -ComposeConfig $cfg) -join ';') 'postgres'
    $cfg = New-GoodComposeConfig
    $cfg.services.api | Add-Member -NotePropertyName network_mode -NotePropertyValue 'host'
    Assert-Match ((Test-CmConfig -ComposeConfig $cfg) -join ';') 'host networking'
    $cfg = New-GoodComposeConfig
    $cfg.services.api | Add-Member -NotePropertyName privileged -NotePropertyValue $true
    Assert-Match ((Test-CmConfig -ComposeConfig $cfg) -join ';') 'privileged'
}

Test-Case 'config guard: the normal config (5480 and 18888 on 127.0.0.1) passes' {
    Assert-Equal 0 @(Test-CmConfig -EnvMap @{ CM_API_PORT = '5480'; CM_DASHBOARD_PORT = '18888' } -ComposeConfig (New-GoodComposeConfig)).Count
}

Test-Case 'config guard: the deployer refuses a bad config before pulling or changing anything' {
    $cfg = New-GoodComposeConfig; $cfg.services.api.ports[0].host_ip = '0.0.0.0'
    $r = Invoke-TestDeploy -Machine @{ ComposeConfig = $cfg }
    Assert-Equal 'config-error' $r.Result.Outcome
    Assert-Equal 2 $r.Result.ExitCode
    Assert-Equal 0 (Get-Calls 'docker' '*pull*').Count
    Assert-Equal 0 (Get-MutatingCalls).Count
}

Test-Case 'deployer: a portable gh under <Root>/tools/gh/bin goes first on this process PATH, once' {
    $root = New-TestRoot
    $saved = $env:PATH
    try {
        $bin = [IO.Path]::Combine($root, 'tools', 'gh', 'bin')
        New-Item -ItemType Directory -Force -Path $bin | Out-Null
        Use-CmPortableTools -Root $root
        Use-CmPortableTools -Root $root
        $parts = @($env:PATH -split [regex]::Escape([IO.Path]::PathSeparator))
        Assert-Equal $bin $parts[0] 'the portable gh wins over a machine-wide one'
        Assert-Equal 1 @($parts | Where-Object { $_ -eq $bin }).Count 'added only once'
    }
    finally { $env:PATH = $saved; Remove-TestRoot $root }
}

Test-Case 'deployer: a release that adds a compose variable starts with the compose file at its own commit' {
    $new = 'services: {api: {environment: [CM_NEW_REQUIRED=${CM_NEW_REQUIRED:?}]}}'
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine @{ ComposeAt = @{ $script:Sha2 = $new; $script:Sha1 = 'services: {}' } } -Inspect { param($root) @(Get-ChildItem -LiteralPath (Join-Path $root 'repo/deploy') -Force).Count }
    Assert-Equal 'deployed' $r.Result.Outcome
    foreach ($c in @(@(Get-Calls 'docker' '*run --rm migrate*') + @(Get-Calls 'docker' '*up -d*'))) {
        Assert-Equal $new $c.ComposeText "compose call used: $($c.Display)"
        Assert-Match $c.ComposeFile ([regex]::Escape($script:Sha2.Substring(0, 12))) 'the file is the one at the new commit'
    }
    Assert-Equal 0 $r.Inspected 'temp compose copies are removed'
}

Test-Case 'deployer: the automatic rollback after a failed smoke test uses the previous commit compose file' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine @{ UnhealthyTags = @($script:Sha2); ComposeAt = @{ $script:Sha2 = 'new: file'; $script:Sha1 = 'old: file' } }
    Assert-Equal 'rolled-back' $r.Result.Outcome
    $ups = Get-Calls 'docker' '*up -d*'
    Assert-Equal 'new: file' $ups[0].ComposeText
    Assert-Equal 'old: file' $ups[1].ComposeText 'rollback must not use the new release compose file'
}

Test-Case 'deployer: -RollbackTo starts the older release with its own compose file' {
    $r = Invoke-TestDeploy -RollbackTo $script:Sha1 -State @{ current = @{ sha = $script:Sha2; created = '2026-10-13T12:00:00.0000000Z' } } -Machine @{ Sha = $script:Sha1; ComposeAt = @{ $script:Sha2 = 'new: file'; $script:Sha1 = 'old: file' } }
    Assert-Equal 'manual-rollback' $r.Result.Outcome
    $ups = Get-Calls 'docker' '*up -d*'
    Assert-Equal 'old: file' $ups[0].ComposeText
}

Test-Case 'deployer: a release whose own compose file the config guard refuses migrates and starts nothing' {
    $bad = New-GoodComposeConfig; $bad.services.api.ports[0].host_ip = '0.0.0.0'
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine @{ ComposeAt = @{ $script:Sha2 = 'bad: file' }; ComposeConfigFor = @{ 'bad: file' = $bad } } -Inspect { param($root) @(Get-ChildItem -LiteralPath (Join-Path $root 'repo/deploy') -Force).Count }
    Assert-Equal 'config-error' $r.Result.Outcome
    Assert-Equal 2 $r.Result.ExitCode
    Assert-Equal 0 (Get-Calls 'docker' '*run --rm migrate*').Count
    Assert-Equal 0 (Get-Calls 'docker' '*up -d*').Count
    Assert-False $r.State.ContainsKey('failedSha') 'nothing is recorded as failed'
    Assert-Equal 0 $r.Inspected 'the hidden copy is removed'
}

Test-Case 'deployer: a release whose compose file cannot be read at its commit deploys nothing' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine @{ ComposeAt = @{ $script:Sha2 = '' } }
    Assert-Equal 'refused' $r.Result.Outcome
    Assert-Equal 0 (Get-Calls 'docker' '*run --rm migrate*').Count
    Assert-Equal 0 (Get-Calls 'docker' '*up -d*').Count
}

Test-Case 'deployer: without a portable gh the PATH is left alone' {
    $root = New-TestRoot
    $saved = $env:PATH
    try {
        Use-CmPortableTools -Root $root
        Assert-Equal $saved $env:PATH
    }
    finally { $env:PATH = $saved; Remove-TestRoot $root }
}

# ---- #491: the running server proves its build -------------------------------------------------------------------

Test-Case 'deployer #491: an image whose first-listed digest is unattested still deploys when another digest verifies' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine @{ ExtraDigests = @{ api = @(('e1' * 32)) } }
    Assert-Equal 'deployed' $r.Result.Outcome
    Assert-Equal $script:Sha2 $r.State.current.sha
    Assert-Equal "sha256:$($script:Digest.api)" $r.State.current.digests.api 'the recorded digest is the attested one'
    Assert-Equal "sha256:$($script:Digest.migrate)" $r.State.current.digests.migrate
    Assert-Equal "sha256:$($script:Digest.backup)" $r.State.current.digests.backup
    Assert-True ((Get-Calls 'gh' '*attestation verify*').Count -gt 3) 'the unattested digest was tried before the attested one'
}

Test-Case 'deployer #491: an image with no attested digest at all still deploys nothing' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine @{ ExtraDigests = @{ api = @(('e1' * 32)) }; BadAttest = @('api') }
    Assert-Equal 'refused' $r.Result.Outcome
    Assert-Equal 0 (Get-Calls 'docker' '*up -d*').Count
    Assert-Equal $script:Sha1 $r.State.current.sha
}

Test-Case 'deployer #491: when the main tag moves while checking, the stale commit is not deployed' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine @{ MainMovesTo = ('c3' * 20) }
    Assert-Equal 'refused' $r.Result.Outcome
    Assert-Match $r.Log 'the main tag moved while checking'
    Assert-Equal 0 (Get-Calls 'docker' '*run --rm migrate*').Count
    Assert-Equal 0 (Get-Calls 'docker' '*up -d*').Count
    Assert-Equal $script:Sha1 $r.State.current.sha
}

Test-Case 'deployer #491: when the api that started reports an older build, the deploy rolls back' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine @{ RunningRevision = $script:Sha1 }
    Assert-Equal 'rolled-back' $r.Result.Outcome
    Assert-Equal 1 $r.Result.ExitCode
    Assert-Match $r.Log 'reports build'
    Assert-Match $r.Result.Reason 'reports build'
    $ups = Get-Calls 'docker' '*up -d*'
    Assert-Equal 2 $ups.Count
    Assert-Equal $script:Sha1 $ups[1].Env['CM_TAG'] 'rollback pins the previous tag'
    Assert-Equal $script:Sha1 $r.State.current.sha
    Assert-Equal $script:Sha2 $r.State.failedSha
}

Test-Case 'deployer #491: when the running api version cannot be read, the deploy fails closed' {
    foreach ($machine in @(@{ ApiContainerMissing = $true }, @{ ApiInspectFails = $true })) {
        $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine $machine
        Assert-Equal 'rolled-back' $r.Result.Outcome
        Assert-Equal $script:Sha1 $r.State.current.sha
        Assert-Equal $script:Sha2 $r.State.failedSha
    }
}
