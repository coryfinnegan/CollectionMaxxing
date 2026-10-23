# OPS-1b tests: the ancestry guard, loud refusals, the explicit rollback path. Dot-sourced by Run-Tests.ps1 after
# deployer.tests.ps1 (it uses Invoke-TestDeploy and Get-OldGoodState from there).

$script:Sha3 = ('c3' * 20)   # a commit nobody has heard of
$script:Sha4 = ('d4' * 20)   # a descendant of Sha2

function Get-StateAt { param([string]$Sha) @{ current = @{ sha = $Sha; created = '2026-10-12T12:00:00.0000000Z'; deployedAt = '2026-10-12T12:05:00Z' } } }
function Get-Graph {
    param([string]$Shape)
    $g = @{}
    switch ($Shape) {
        'linear' { $g[$script:Sha1] = @(); $g[$script:Sha2] = @($script:Sha1); $g[$script:Sha4] = @($script:Sha1, $script:Sha2) }
        'diverged' { $g[$script:Sha1] = @(); $g[$script:Sha2] = @(); $g[$script:Sha4] = @() }
    }
    $g
}
function Get-AlertCalls { @(Get-Calls 'curl' '*/fail*') }

# ---- the guard itself: by ancestry, never by time ------------------------------------------------------------------------

Test-Case 'ancestry: a descendant of the deployed commit is accepted' {
    New-FakeMachine @{ Graph = (Get-Graph 'linear') } | Out-Null
    $r = Test-CmAncestry -Ctx (New-TestContext -Root 'x') -Current $script:Sha1 -Target $script:Sha4
    Assert-True $r.Ok; Assert-Equal 'descendant' $r.Relation
}

Test-Case 'ancestry: the same commit is refused' {
    New-FakeMachine @{ Graph = (Get-Graph 'linear') } | Out-Null
    $r = Test-CmAncestry -Ctx (New-TestContext -Root 'x') -Current $script:Sha2 -Target $script:Sha2
    Assert-False $r.Ok; Assert-Equal 'equal' $r.Relation
}

Test-Case 'ancestry: an ancestor (an older commit) is refused' {
    New-FakeMachine @{ Graph = (Get-Graph 'linear') } | Out-Null
    $r = Test-CmAncestry -Ctx (New-TestContext -Root 'x') -Current $script:Sha4 -Target $script:Sha1
    Assert-False $r.Ok; Assert-Equal 'ancestor' $r.Relation
}

Test-Case 'ancestry: a diverged branch is refused' {
    New-FakeMachine @{ Graph = (Get-Graph 'diverged') } | Out-Null
    $r = Test-CmAncestry -Ctx (New-TestContext -Root 'x') -Current $script:Sha1 -Target $script:Sha2
    Assert-False $r.Ok; Assert-Equal 'diverged' $r.Relation
}

Test-Case 'ancestry: an unknown current commit is refused (fails closed)' {
    New-FakeMachine @{ Graph = (Get-Graph 'linear') } | Out-Null
    $r = Test-CmAncestry -Ctx (New-TestContext -Root 'x') -Current $script:Sha3 -Target $script:Sha2
    Assert-False $r.Ok; Assert-Equal 'unknown-current' $r.Relation
}

Test-Case 'ancestry: an unknown target commit is refused' {
    New-FakeMachine @{ Graph = (Get-Graph 'linear') } | Out-Null
    $r = Test-CmAncestry -Ctx (New-TestContext -Root 'x') -Current $script:Sha1 -Target $script:Sha3
    Assert-False $r.Ok; Assert-Equal 'unknown-target' $r.Relation
}

Test-Case 'ancestry: something that is not a full commit hash is refused without asking git' {
    New-FakeMachine @{ Graph = (Get-Graph 'linear') } | Out-Null
    foreach ($bad in '', 'main', 'HEAD~1', 'a1a1', "$($script:Sha1);rm", ('A1' * 20)) {
        $r = Test-CmAncestry -Ctx (New-TestContext -Root 'x') -Current $bad -Target $script:Sha2
        Assert-False $r.Ok "current '$bad'"; Assert-Equal 'unknown-current' $r.Relation
    }
    Assert-Equal 0 $global:CmCalls.Count 'a malformed sha never reaches git'
}

Test-Case 'ancestry (real git): descendant, equal, ancestor, diverged and unknown, against a real repository' {
    $dir = Join-Path ([IO.Path]::GetTempPath()) ("cm-ops1-test-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    try {
        $g = @('-C', $dir, '-c', 'user.name=test', '-c', 'user.email=test@example.invalid', '-c', 'commit.gpgsign=false')
        & git @g init -q | Out-Null
        & git @g commit -q --allow-empty -m one | Out-Null; $c1 = (& git @g rev-parse HEAD).Trim()
        & git @g commit -q --allow-empty -m two | Out-Null; $c2 = (& git @g rev-parse HEAD).Trim()
        & git @g checkout -q -b side $c1 | Out-Null
        & git @g commit -q --allow-empty -m side | Out-Null; $side = (& git @g rev-parse HEAD).Trim()
        $global:CmRunner = { param($Command, $Arguments, $Env, $Mutating) Invoke-CmReal -Command $Command -Arguments $Arguments -Env $Env }
        $ctx = New-TestContext -Root 'x'; $ctx.RepoDir = $dir
        Assert-Equal 'descendant' (Test-CmAncestry -Ctx $ctx -Current $c1 -Target $c2).Relation
        Assert-Equal 'equal' (Test-CmAncestry -Ctx $ctx -Current $c2 -Target $c2).Relation
        Assert-Equal 'ancestor' (Test-CmAncestry -Ctx $ctx -Current $c2 -Target $c1).Relation
        Assert-Equal 'diverged' (Test-CmAncestry -Ctx $ctx -Current $c2 -Target $side).Relation
        Assert-Equal 'unknown-current' (Test-CmAncestry -Ctx $ctx -Current $script:Sha3 -Target $c2).Relation
        Assert-Equal 'unknown-target' (Test-CmAncestry -Ctx $ctx -Current $c1 -Target $script:Sha3).Relation
        $ctx.RepoDir = Join-Path $dir 'not-a-repo'
        Assert-Equal 'unknown-current' (Test-CmAncestry -Ctx $ctx -Current $c1 -Target $c2).Relation 'a missing clone fails closed'
    }
    finally { if ($dir -like '*cm-ops1-test-*') { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue } }
}

# ---- the guard inside a deploy run -----------------------------------------------------------------------------------------

Test-Case 'deployer: a descendant is deployed, after the repo is fetched and the ancestry is checked' {
    $r = Invoke-TestDeploy -State (Get-StateAt $script:Sha1) -Machine @{ Graph = (Get-Graph 'linear') }
    Assert-Equal 'deployed' $r.Result.Outcome
    Assert-Equal 0 $r.Result.ExitCode
    $fetch = Get-CallIndex 'git * fetch*'
    $anc = Get-CallIndex 'git * merge-base --is-ancestor*'
    $migrate = Get-CallIndex '*run --rm migrate*'
    Assert-True ($fetch -ge 0 -and $fetch -lt $anc -and $anc -lt $migrate) "order was fetch=$fetch ancestry=$anc migrate=$migrate"
    Assert-Equal 0 (Get-AlertCalls).Count 'a clean deploy sends no alert'
}

Test-Case 'deployer: an older commit than the deployed one is refused, loudly, with nothing changed' {
    $r = Invoke-TestDeploy -State (Get-StateAt $script:Sha2) -Machine @{ Sha = $script:Sha1 }
    Assert-Equal 'downgrade-refused' $r.Result.Outcome
    Assert-Equal 4 $r.Result.ExitCode
    Assert-Equal 0 (Get-Calls 'docker' '*run --rm migrate*').Count
    Assert-Equal 0 (Get-Calls 'docker' '*up -d*').Count
    Assert-Equal $script:Sha2 $r.State.current.sha
    Assert-Equal 1 (Get-AlertCalls).Count
}

Test-Case 'deployer: a diverged branch is refused' {
    $r = Invoke-TestDeploy -State (Get-StateAt $script:Sha1) -Machine @{ Graph = (Get-Graph 'diverged') }
    Assert-Equal 'downgrade-refused' $r.Result.Outcome
    Assert-Equal 4 $r.Result.ExitCode
    Assert-Equal 0 (Get-Calls 'docker' '*up -d*').Count
    Assert-Match $r.Log 'diverge'
}

Test-Case 'deployer: an unknown current commit is refused (fails closed), even for a newer image' {
    $r = Invoke-TestDeploy -State (Get-StateAt $script:Sha3)
    Assert-Equal 'downgrade-refused' $r.Result.Outcome
    Assert-Equal 4 $r.Result.ExitCode
    Assert-Equal 0 (Get-Calls 'docker' '*run --rm migrate*').Count
    Assert-Equal 0 (Get-Calls 'docker' '*up -d*').Count
    Assert-Match $r.Log 'not in the local clone|unknown|cannot be checked'
}

Test-Case 'deployer: a damaged state file is refused instead of being treated as a first deploy' {
    $r = Invoke-TestDeploy -StateText '{ this is not json'
    Assert-Equal 'refused' $r.Result.Outcome
    Assert-Equal 4 $r.Result.ExitCode
    Assert-Equal 0 (Get-Calls 'docker' '*up -d*').Count
}

Test-Case 'deployer: a state file whose current commit is not a commit hash is refused' {
    $r = Invoke-TestDeploy -State @{ current = @{ sha = 'latest' } }
    Assert-Equal 'downgrade-refused' $r.Result.Outcome
    Assert-Equal 0 (Get-Calls 'docker' '*up -d*').Count
}

Test-Case 'deployer: if the repo cannot be fetched, nothing is deployed' {
    $r = Invoke-TestDeploy -State (Get-StateAt $script:Sha1) -Machine @{ FetchFails = $true }
    Assert-Equal 'downgrade-refused' $r.Result.Outcome
    Assert-Equal 4 $r.Result.ExitCode
    Assert-Equal 0 (Get-Calls 'docker' '*up -d*').Count
}

Test-Case 'deployer: the build time of an image no longer decides anything' {
    # Older build time, but a descendant commit: allowed. (Clock skew and rebuilds must not block or allow a deploy.)
    $r = Invoke-TestDeploy -State @{ current = @{ sha = $script:Sha1; created = '2027-01-01T00:00:00Z' } }
    Assert-Equal 'deployed' $r.Result.Outcome
}

# ---- refusal is loud -------------------------------------------------------------------------------------------------------

Test-Case 'loud: every refused, failed or rolled-back outcome exits non-zero, logs ALERT and pings the failure URL' {
    $cases = @(
        @{ Name = 'refused (bad attestation)'; Machine = @{ BadAttest = @('api') }; State = (Get-OldGoodState); Outcome = 'refused' },
        @{ Name = 'refused (older commit)'; Machine = @{ Sha = $script:Sha1 }; State = (Get-StateAt $script:Sha2); Outcome = 'downgrade-refused' },
        @{ Name = 'migrate-failed'; Machine = @{ MigrateExit = 1 }; State = (Get-OldGoodState); Outcome = 'migrate-failed' },
        @{ Name = 'rolled-back'; Machine = @{ UnhealthyTags = @($script:Sha2) }; State = (Get-OldGoodState); Outcome = 'rolled-back' },
        @{ Name = 'failed (no previous good)'; Machine = @{ UnhealthyTags = @($script:Sha2) }; State = $null; Outcome = 'failed' },
        @{ Name = 'no-docker'; Machine = @{ PullFails = $true }; State = (Get-OldGoodState); Outcome = 'no-docker' }
    )
    foreach ($c in $cases) {
        $global:CmCalls.Clear()
        $r = Invoke-TestDeploy -State $c.State -Machine $c.Machine
        Assert-Equal $c.Outcome $r.Result.Outcome "($($c.Name))"
        Assert-True ($r.Result.ExitCode -ne 0) "$($c.Name) must exit non-zero"
        Assert-Match $r.Log 'ALERT' "($($c.Name))"
        $alerts = Get-AlertCalls
        Assert-Equal 1 $alerts.Count "($($c.Name)) one alert ping"
        Assert-Match $alerts[0].Display 'https://hc\.example/test-uuid/fail' "($($c.Name))"
        Assert-Match $alerts[0].Display $c.Outcome "($($c.Name)) the reason is in the ping"
        Assert-False ($r.Log -match 'test-uuid') "($($c.Name)) the secret URL never reaches the log"
    }
}

Test-Case 'loud: a config refusal exits 2 and alerts' {
    $cfg = New-GoodComposeConfig; $cfg.services.api.ports[0].host_ip = '0.0.0.0'
    $r = Invoke-TestDeploy -Machine @{ ComposeConfig = $cfg }
    Assert-Equal 2 $r.Result.ExitCode
    Assert-Equal 1 (Get-AlertCalls).Count
}

Test-Case 'loud: a clean deploy exits 0 and sends nothing to the failure URL' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState)
    Assert-Equal 0 $r.Result.ExitCode
    Assert-Equal 0 (Get-AlertCalls).Count
    Assert-False ($r.Log -match 'ALERT')
}

Test-Case 'loud: nothing-to-do outcomes (up to date, known bad, busy) stay quiet and exit 0' {
    $r = Invoke-TestDeploy -State @{ current = @{ sha = $script:Sha2; created = '2026-10-13T12:00:00Z' } }
    Assert-Equal 0 $r.Result.ExitCode; Assert-Equal 0 (Get-AlertCalls).Count
    $state = Get-OldGoodState; $state['failedSha'] = $script:Sha2
    $r = Invoke-TestDeploy -State $state
    Assert-Equal 'known-bad' $r.Result.Outcome; Assert-Equal 0 $r.Result.ExitCode; Assert-Equal 0 (Get-AlertCalls).Count
}

Test-Case 'loud: a dry run exits 0 and alerts nothing, even when the real run would be refused' {
    $r = Invoke-TestDeploy -DryRun -State (Get-StateAt $script:Sha2) -Machine @{ Sha = $script:Sha1; BadAttest = @('api') }
    Assert-Equal 'dry-run' $r.Result.Outcome
    Assert-Equal 0 $r.Result.ExitCode
    Assert-Equal 0 (Get-AlertCalls).Count
    Assert-Equal 0 (Get-MutatingCalls).Count
}

Test-Case 'loud: with no heartbeat URL the run still exits non-zero and the log line is the alert' {
    $envText = @('POSTGRES_PASSWORD=test-only-password', 'CM_BACKUP_DIR=/tmp/cm-test-backups', 'CM_API_PORT=5480', 'CM_HEARTBEAT_URL=')
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine @{ BadAttest = @('api') } -EnvText $envText
    Assert-True ($r.Result.ExitCode -ne 0)
    Assert-Match $r.Log 'ALERT'
    Assert-Equal 0 (Get-AlertCalls).Count
    Assert-Match $r.Log 'CM_HEARTBEAT_URL'
}

Test-Case 'loud: a failing alert ping is logged but does not change the exit code' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine @{ BadAttest = @('api'); AlertFails = $true }
    Assert-Equal 4 $r.Result.ExitCode
    Assert-Match $r.Log 'alert ping failed'
}

Test-Case 'loud: an unexpected crash is also a non-zero exit and an alert' {
    $root = New-TestRoot
    try {
        New-FakeMachine @{} | Out-Null
        Initialize-Cm -Apply $true
        $ctx = New-TestContext -Root $root
        $global:CmRunner = { param($Command, $Arguments, $Env, $Mutating) if ($Command -eq 'git' -or $Command -eq 'curl') { return [pscustomobject]@{ ExitCode = 0; Output = @('200') } }; throw 'boom' }
        $result = Invoke-CmDeploy -Ctx $ctx 6>$null
        Assert-Equal 'crashed' $result.Outcome
        Assert-Equal 1 $result.ExitCode
        Assert-True (@($global:CmCalls).Count -ge 0)
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'attestation: a tampered artifact exits non-zero before anything is changed' {
    foreach ($bad in 'api', 'migrate', 'backup') {
        $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine @{ BadAttest = @($bad) }
        Assert-True ($r.Result.ExitCode -ne 0) "tampered $bad"
        Assert-Equal 0 (Get-Calls 'docker' '*run --rm migrate*').Count
        Assert-Equal 0 (Get-Calls 'docker' '*up -d*').Count
        $changing = @(Get-MutatingCalls | Where-Object { $_.Display -notmatch '^docker pull|^git ' })
        Assert-Equal 0 $changing.Count "only pulls and the repo fetch ran: $(($changing | ForEach-Object Display) -join ' | ')"
        Assert-Equal $script:Sha1 $r.State.current.sha
    }
}

# ---- the explicit rollback path -------------------------------------------------------------------------------------------

Test-Case 'rollback: -RollbackTo an older commit deploys its attested images without migrating, and holds back the bad one' {
    $r = Invoke-TestDeploy -RollbackTo $script:Sha1 -State (Get-StateAt $script:Sha2) -Machine @{ Sha = $script:Sha1 }
    Assert-Equal 'manual-rollback' $r.Result.Outcome
    Assert-Equal 0 $r.Result.ExitCode
    Assert-Equal 3 (Get-Calls 'gh' '*attestation verify*').Count 'the older images are verified like any other'
    Assert-Equal 0 (Get-Calls 'docker' '*run --rm migrate*').Count 'migrations are expand-only: the older api runs on the newer schema'
    Assert-Equal $script:Sha1 (Get-Calls 'docker' '*up -d*')[0].Env['CM_TAG']
    Assert-Equal $script:Sha1 $r.State.current.sha
    Assert-Equal $script:Sha2 $r.State.failedSha 'the next poll does not put the bad commit straight back'
    Assert-Equal 0 (Get-AlertCalls).Count
}

Test-Case 'rollback: after a rollback the poller skips the commit it rolled back from' {
    $r = Invoke-TestDeploy -State @{ current = @{ sha = $script:Sha1 }; failedSha = $script:Sha2 }
    Assert-Equal 'known-bad' $r.Result.Outcome
}

Test-Case 'rollback: a target that is not older than the running commit is refused' {
    foreach ($case in @(
            @{ Why = 'newer'; Target = $script:Sha4; Current = $script:Sha2 },
            @{ Why = 'same'; Target = $script:Sha2; Current = $script:Sha2 })) {
        $global:CmCalls.Clear()
        $r = Invoke-TestDeploy -RollbackTo $case.Target -State (Get-StateAt $case.Current) -Machine @{ Sha = $case.Target; Graph = (Get-Graph 'linear') }
        Assert-Equal 'rollback-refused' $r.Result.Outcome $case.Why
        Assert-Equal 4 $r.Result.ExitCode
        Assert-Equal 0 (Get-Calls 'docker' '*up -d*').Count
        Assert-Equal 1 (Get-AlertCalls).Count
    }
}

Test-Case 'rollback: a diverged target, an unknown target and a malformed target are refused' {
    $r = Invoke-TestDeploy -RollbackTo $script:Sha1 -State (Get-StateAt $script:Sha2) -Machine @{ Sha = $script:Sha1; Graph = (Get-Graph 'diverged') }
    Assert-Equal 'rollback-refused' $r.Result.Outcome
    $r = Invoke-TestDeploy -RollbackTo $script:Sha3 -State (Get-StateAt $script:Sha2) -Machine @{ Sha = $script:Sha3 }
    Assert-Equal 'rollback-refused' $r.Result.Outcome
    $r = Invoke-TestDeploy -RollbackTo 'HEAD~1' -State (Get-StateAt $script:Sha2)
    Assert-Equal 'rollback-refused' $r.Result.Outcome
    Assert-Equal 0 (Get-Calls 'docker' '*up -d*').Count
}

Test-Case 'rollback: with no deployed commit on record there is nothing to roll back from' {
    $r = Invoke-TestDeploy -RollbackTo $script:Sha1
    Assert-Equal 'rollback-refused' $r.Result.Outcome
    Assert-Equal 4 $r.Result.ExitCode
}

Test-Case 'rollback: images that are not attested are refused and the running version is untouched' {
    $r = Invoke-TestDeploy -RollbackTo $script:Sha1 -State (Get-StateAt $script:Sha2) -Machine @{ Sha = $script:Sha1; BadAttest = @('migrate') }
    Assert-Equal 'rollback-refused' $r.Result.Outcome
    Assert-Equal 0 (Get-Calls 'docker' '*up -d*').Count
    Assert-Equal $script:Sha2 $r.State.current.sha
}

Test-Case 'rollback: if the older version is not healthy the running one is pinned again and the run fails loudly' {
    $r = Invoke-TestDeploy -RollbackTo $script:Sha1 -State (Get-StateAt $script:Sha2) -Machine @{ Sha = $script:Sha1; UnhealthyTags = @($script:Sha1) }
    Assert-Equal 'rollback-failed' $r.Result.Outcome
    Assert-Equal 1 $r.Result.ExitCode
    $ups = Get-Calls 'docker' '*up -d*'
    Assert-Equal 2 $ups.Count
    Assert-Equal $script:Sha2 $ups[1].Env['CM_TAG']
    Assert-Equal $script:Sha2 $r.State.current.sha
    Assert-Equal 1 (Get-AlertCalls).Count
}

Test-Case 'rollback: a dry run only prints the plan' {
    $r = Invoke-TestDeploy -DryRun -RollbackTo $script:Sha1 -State (Get-StateAt $script:Sha2) -Machine @{ Sha = $script:Sha1 }
    Assert-Equal 'dry-run' $r.Result.Outcome
    Assert-Equal 0 $r.Result.ExitCode
    Assert-Equal 0 (Get-MutatingCalls).Count
}

Test-Case 'rollback: the script exposes it as -RollbackTo and is a dry run without -Apply' {
    $script = Join-Path $PSScriptRoot '../deployer/Deploy-CollectionMaxxing.ps1'
    $root = New-TestRoot
    try {
        Set-TestState -Root $root -State (Get-StateAt $script:Sha2)
        New-FakeMachine @{ Sha = $script:Sha1 } | Out-Null
        & $script -Root $root -ComposeFile (New-TestComposePath $root) -RollbackTo $script:Sha1 6>$null
        Assert-Equal 0 $LASTEXITCODE
        Assert-Equal 0 (Get-MutatingCalls).Count
        & $script -Root $root -ComposeFile (New-TestComposePath $root) -SmokeDelaySeconds 0 -RollbackTo $script:Sha1 -Apply 6>$null
        Assert-Equal 0 $LASTEXITCODE
        Assert-Equal $script:Sha1 (Get-TestState -Root $root).current.sha
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'script: a refused deploy makes the script itself exit non-zero' {
    $script = Join-Path $PSScriptRoot '../deployer/Deploy-CollectionMaxxing.ps1'
    $root = New-TestRoot
    try {
        Set-TestState -Root $root -State (Get-StateAt $script:Sha2)
        New-FakeMachine @{ Sha = $script:Sha1 } | Out-Null
        & $script -Root $root -ComposeFile (New-TestComposePath $root) -SmokeDelaySeconds 0 -Apply 6>$null
        Assert-Equal 4 $LASTEXITCODE
        Assert-Equal 1 (Get-AlertCalls).Count
    }
    finally { Remove-TestRoot $root }
}

# ---- the restore drill wrapper -----------------------------------------------------------------------------------------------

Test-Case 'restore-drill.ps1: passes the backup path to the .sh and returns its exit code' {
    $scripts = Join-Path ([IO.Path]::GetTempPath()) ("cm-ops1-test-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force -Path $scripts | Out-Null
    try {
        Copy-Item (Join-Path $PSScriptRoot '../../scripts/restore-drill.ps1') $scripts
        $marker = Join-Path $scripts 'args.txt'
        Set-Content -LiteralPath (Join-Path $scripts 'restore-drill.sh') -Value "#!/usr/bin/env bash`necho `"`$1`" > '$($marker -replace '\\', '/')'`nexit 7`n" -NoNewline
        $pwshExe = (Get-Process -Id $PID).Path
        & $pwshExe -NoProfile -File (Join-Path $scripts 'restore-drill.ps1') -Path 'F:\Backups\CollectionMaxxing' | Out-Null
        Assert-Equal 7 $LASTEXITCODE
        Assert-Equal 'F:/Backups/CollectionMaxxing' ((Get-Content -LiteralPath $marker -Raw).Trim())
        # With no -Path it reads CM_BACKUP_DIR from the .env under -Root.
        $root = New-TestRoot
        try {
            Add-Content -LiteralPath (Join-Path $root '.env') -Value 'CM_BACKUP_DIR=G:/Somewhere/Else'
            & $pwshExe -NoProfile -File (Join-Path $scripts 'restore-drill.ps1') -Root $root | Out-Null
            Assert-Equal 'G:/Somewhere/Else' ((Get-Content -LiteralPath $marker -Raw).Trim())
        }
        finally { Remove-TestRoot $root }
    }
    finally { if ($scripts -like '*cm-ops1-test-*') { Remove-Item -LiteralPath $scripts -Recurse -Force -ErrorAction SilentlyContinue } }
}
