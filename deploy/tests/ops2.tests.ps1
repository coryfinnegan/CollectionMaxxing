# OPS-IPA2, then ADR-024: every poll starts by fast-forwarding the deployer's own clone to origin/main (#499), and
# the .ipa the server used to serve (before the iPhone app moved to TestFlight) is removed once from the files volume.
# Nothing reads GitHub releases now. Dot-sourced by Run-Tests.ps1 after deployer.tests.ps1 and ops1b.tests.ps1
# (it uses their helpers).

# The deployed commit is already the newest main image (a quiet run).
function Get-QuietState {
    param([hashtable]$Extra = @{})
    $state = @{ current = @{ sha = $script:Sha2; created = '2026-10-13T12:00:00Z'; deployedAt = '2026-10-13T12:05:00Z' } }
    foreach ($k in $Extra.Keys) { $state[$k] = $Extra[$k] }
    $state
}

# What a server that synced the .ipa before ADR-024 still remembers.
$script:RetiredIpa = @{ ipa = @{ sha256 = ('0' * 64); file = 'CollectionMaxxing-unsigned.ipa'; version = '1.0.3'; tag = 'v1.0.3' } }

function Get-IpaRemovals { @(Get-Calls 'docker' '*run --rm --network none*rm -rf /data/files/ipa*') }
function Get-RepoMerges { @(Get-Calls 'git' '*merge --ff-only*') }
function Get-AlertLines { param([string]$Log) @([regex]::Matches($Log, 'ALERT ')).Count }

# ---- the retired .ipa ----------------------------------------------------------------------------------------------------

Test-Case 'retired .ipa: a quiet run on a server that still has one removes ipa/ from the files volume and forgets it' {
    $r = Invoke-TestDeploy -State (Get-QuietState $script:RetiredIpa)
    Assert-Equal 'up-to-date' $r.Result.Outcome
    Assert-Equal 0 $r.Result.ExitCode
    $removals = Get-IpaRemovals
    Assert-Equal 1 $removals.Count
    Assert-Match $removals[0].Display '-v collectionmaxxing_files:/data/files'
    Assert-Match $removals[0].Display "collectionmaxxing-backup:$($script:Sha2) -c `"rm -rf /data/files/ipa`"$"
    Assert-True $removals[0].Mutating
    Assert-False $r.State.ContainsKey('ipa') 'the state forgets the .ipa'
    Assert-Equal $script:Sha2 $r.State.current.sha 'nothing else changes'
    Assert-Match $r.Log 'TestFlight'
    Assert-Equal 0 (Get-AlertCalls).Count
}

Test-Case 'retired .ipa: a remembered failed sync attempt is forgotten as well' {
    $r = Invoke-TestDeploy -State (Get-QuietState @{ ipaAttempt = @{ tag = 'v1.0.3'; outcome = 'no-release'; at = '2026-10-20T00:00:00Z' } })
    Assert-Equal 1 (Get-IpaRemovals).Count
    Assert-False $r.State.ContainsKey('ipaAttempt')
}

Test-Case 'retired .ipa: once it is gone the next runs do nothing more' {
    $first = Invoke-TestDeploy -State (Get-QuietState $script:RetiredIpa)
    $global:CmCalls.Clear()
    $second = Invoke-TestDeploy -State $first.State
    Assert-Equal 'up-to-date' $second.Result.Outcome
    Assert-Equal 0 (Get-IpaRemovals).Count
    Assert-Equal 0 (@(Get-MutatingCalls) | Where-Object { $_.Display -notmatch '^docker pull|^git ' }).Count 'only the pull and the clone move'
}

Test-Case 'retired .ipa: a server that never had one is left alone' {
    $r = Invoke-TestDeploy -State (Get-QuietState)
    Assert-Equal 0 (Get-IpaRemovals).Count
    Assert-Equal 0 (Get-Calls 'docker' '*run --rm --network none*').Count
}

Test-Case 'retired .ipa: a removal that fails is a warning, not an alert, and is tried again on the next run' {
    $r = Invoke-TestDeploy -State (Get-QuietState $script:RetiredIpa) -Machine @{ HelperFails = $true }
    Assert-Equal 'up-to-date' $r.Result.Outcome
    Assert-Equal 0 $r.Result.ExitCode
    Assert-Equal 0 (Get-AlertCalls).Count
    Assert-Equal 0 (Get-AlertLines $r.Log)
    Assert-Match $r.Log 'WARN .*old \.ipa'
    Assert-True $r.State.ContainsKey('ipa') 'still remembered, so the next run tries again'
    $global:CmCalls.Clear()
    $again = Invoke-TestDeploy -State $r.State
    Assert-Equal 1 (Get-IpaRemovals).Count
    Assert-False $again.State.ContainsKey('ipa')
}

Test-Case 'retired .ipa: the old staging folder inside the root goes too' {
    $r = Invoke-TestDeploy -State (Get-QuietState $script:RetiredIpa) -Setup {
        param($root)
        New-Item -ItemType Directory -Force -Path (Join-Path $root 'state/ipa-stage') | Out-Null
        Set-Content -LiteralPath (Join-Path $root 'state/ipa-stage/app.ipa') -Value 'old'
    } -Inspect { param($root) Test-Path -LiteralPath (Join-Path $root 'state/ipa-stage') }
    Assert-False $r.Inspected 'state/ipa-stage was removed'
}

Test-Case 'retired .ipa: a good deploy removes it too, after the poll-start fast-forward and the smoke test' {
    $state = Get-OldGoodState
    $state['ipa'] = $script:RetiredIpa['ipa']
    $r = Invoke-TestDeploy -State $state
    Assert-Equal 'deployed' $r.Result.Outcome
    Assert-Equal 0 $r.Result.ExitCode
    $remove = Get-CallIndex '*rm -rf /data/files/ipa*'
    $merge = Get-CallIndex 'git * merge --ff-only*'
    $smoke = Get-CallIndex 'curl*health/ready'
    Assert-True ($merge -ge 0 -and $merge -lt $smoke -and $smoke -lt $remove) "merge=$merge smoke=$smoke remove=$remove"
    Assert-Match (Get-IpaRemovals)[0].Display "collectionmaxxing-backup:$($script:Sha2) "
    Assert-False $r.State.ContainsKey('ipa')
}

Test-Case 'retired .ipa: a rolled-back deploy does not remove it (the run already alerts for the rollback)' {
    $state = Get-OldGoodState
    $state['ipa'] = $script:RetiredIpa['ipa']
    $r = Invoke-TestDeploy -State $state -Machine @{ UnhealthyTags = @($script:Sha2) }
    Assert-Equal 'rolled-back' $r.Result.Outcome
    Assert-Equal 0 (Get-IpaRemovals).Count
}

Test-Case 'releases: neither a quiet run nor a deploy reads GitHub releases any more' {
    Invoke-TestDeploy -State (Get-QuietState $script:RetiredIpa) | Out-Null
    Invoke-TestDeploy -State (Get-OldGoodState) | Out-Null
    Assert-Equal 0 (Get-Calls 'gh' 'gh release *').Count
}

# ---- every poll starts by fast-forwarding the deployer's own clone to origin/main (#499) --------------------------------

Test-Case 'deploy: a good deploy starts by fast-forwarding the deployer clone to origin/main, and only that' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState)
    Assert-Equal 'deployed' $r.Result.Outcome
    $merges = Get-RepoMerges
    Assert-Equal 1 $merges.Count
    Assert-Match $merges[0].Display 'merge --ff-only origin/main\s*$'
    Assert-Match $merges[0].Display '^git -C \S*repo merge'
    Assert-Equal 0 (Get-Calls 'git' '*reset*').Count 'never resets'
    Assert-Equal 0 (Get-Calls 'git' '*checkout*').Count 'never switches or discards'
    Assert-Equal 0 (Get-Calls 'git' '*--force*').Count
    Assert-Equal 0 (Get-Calls 'git' '* clean*').Count
    Assert-Equal 0 (Get-AlertCalls).Count
}

Test-Case 'deploy: the clone is moved before the first image pull, after a fetch and the clean/on-main checks' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine @{ MergeMovesTo = ('c4' * 20) }
    $fetch = Get-CallIndex 'git * fetch*'
    $status = Get-CallIndex 'git * status --porcelain*'
    $branch = Get-CallIndex 'git * symbolic-ref*'
    $merge = Get-CallIndex 'git * merge --ff-only*'
    $pull = Get-CallIndex 'docker pull*'
    Assert-True ($fetch -ge 0 -and $fetch -lt $merge) "fetch=$fetch merge=$merge"
    Assert-True ($status -ge 0 -and $branch -ge 0 -and $status -lt $merge -and $branch -lt $merge) "status=$status branch=$branch merge=$merge"
    Assert-True ($merge -ge 0 -and $merge -lt $pull) "merge=$merge pull=$pull"
    Assert-Match (Get-Calls 'git' '*status --porcelain*')[0].Display '--untracked-files=no'
    Assert-Match $r.Log 'moved the clone at .* to origin/main'
}

Test-Case 'deploy: a quiet run still fast-forwards the clone, so deployer fixes self-deliver with no deploy' {
    $r = Invoke-TestDeploy -State (Get-QuietState) -Machine @{ MergeMovesTo = ('c4' * 20) }
    Assert-Equal 'up-to-date' $r.Result.Outcome
    Assert-Equal 0 $r.Result.ExitCode
    Assert-Equal 1 (Get-RepoMerges).Count
    Assert-Match $r.Log 'moved the clone at .* to origin/main'
    Assert-Equal 0 (Get-AlertCalls).Count
}

Test-Case 'deploy: a poll that finds the clone already at origin/main does not claim it moved it' {
    # The poll still fetches and fast-forwards (that is how the clone follows main), but "moved" is only true when it moved.
    $r = Invoke-TestDeploy -State (Get-QuietState)
    Assert-Equal 'up-to-date' $r.Result.Outcome
    Assert-Equal 1 (Get-RepoMerges).Count 'the fast-forward still runs every poll'
    Assert-False ($r.Log -match 'moved the clone') "the log must not say the clone moved: $($r.Log)"
    Assert-False ($r.Log -match 'WARN|ERROR') 'and nothing is wrong either'
    $heads = @(Get-Calls 'git' '*rev-parse HEAD*')
    Assert-Equal 2 $heads.Count 'the position is read before and after the fast-forward'
    Assert-False ($heads[0].Mutating -or $heads[1].Mutating) 'reading a position changes nothing'
}

Test-Case 'deploy: when the clone cannot say where it is, the poll still reports the fast-forward, and says it could not tell' {
    $root = New-TestRoot
    try {
        New-FakeMachine @{ RepoHead = '' } | Out-Null      # git rev-parse prints nothing
        Initialize-Cm -Apply $true
        $ctx = New-TestContext -Root $root
        $out = Update-CmRepoToMain -Ctx $ctx 6>&1 | Out-String
        Assert-Match $out 'fast-forwarded the clone at .* to origin/main \(could not tell whether it moved\)'
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'deploy: a known-bad run still fast-forwards the clone' {
    $state = Get-OldGoodState; $state['failedSha'] = $script:Sha2
    $r = Invoke-TestDeploy -State $state
    Assert-Equal 'known-bad' $r.Result.Outcome
    Assert-Equal 1 (Get-RepoMerges).Count
    Assert-Equal 0 (Get-Calls 'docker' '*run --rm migrate*').Count
}

Test-Case 'deploy: a clone with local changes is left alone with a warning, not an alert, and the poll continues' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine @{ RepoDirty = $true }
    Assert-Equal 'deployed' $r.Result.Outcome
    Assert-Equal 0 $r.Result.ExitCode
    Assert-Equal 0 (Get-RepoMerges).Count
    Assert-Match $r.Log 'WARN .*local changes'
    Assert-Equal 0 (Get-AlertCalls).Count
    Assert-Equal 0 (Get-AlertLines $r.Log)
}

Test-Case 'deploy: a clone that is not on main is left alone with a warning' {
    foreach ($branch in 'my-feature', $null) {
        $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine @{ RepoBranch = $branch }
        Assert-Equal 'deployed' $r.Result.Outcome "(branch: $branch)"
        Assert-Equal 0 (Get-RepoMerges).Count "(branch: $branch)"
        Assert-Match $r.Log 'WARN .*not on main'
        Assert-Equal 0 (Get-AlertCalls).Count
    }
}

Test-Case 'deploy: a clone that cannot be fast-forwarded is warned about and nothing is forced' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine @{ MergeFails = $true }
    Assert-Equal 'deployed' $r.Result.Outcome
    Assert-Equal 0 $r.Result.ExitCode
    Assert-Equal 1 (Get-RepoMerges).Count
    Assert-Match $r.Log 'WARN .*fast-forward'
    Assert-Equal 0 (Get-AlertCalls).Count
    Assert-Equal 0 (Get-Calls 'git' '*reset*').Count
}

Test-Case 'deploy: a poll whose fetch fails warns about the clone and still deploys' {
    $r = Invoke-TestDeploy -Machine @{ FetchFails = $true }
    Assert-Equal 'deployed' $r.Result.Outcome
    Assert-Equal 0 $r.Result.ExitCode
    Assert-Equal 0 (Get-RepoMerges).Count
    Assert-Match $r.Log 'WARN .*could not fetch main'
    Assert-Equal 0 (Get-AlertCalls).Count
}

Test-Case 'deploy: a rolled-back or failed deploy still moved the clone at poll start' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine @{ UnhealthyTags = @($script:Sha2) }
    Assert-Equal 'rolled-back' $r.Result.Outcome
    Assert-Equal 1 (Get-RepoMerges).Count
    $global:CmCalls.Clear()
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine @{ MigrateExit = 1 }
    Assert-Equal 'migrate-failed' $r.Result.Outcome
    Assert-Equal 1 (Get-RepoMerges).Count
}

Test-Case 'deploy: a manual rollback leaves the clone alone (only polls move it)' {
    $r = Invoke-TestDeploy -RollbackTo $script:Sha1 -State (Get-StateAt $script:Sha2) -Machine @{ Sha = $script:Sha1 }
    Assert-Equal 'manual-rollback' $r.Result.Outcome
    Assert-Equal 0 (Get-RepoMerges).Count
}

Test-Case 'deploy: last-good stays in the state file while the clone tracks main across a rollback' {
    # Rolled back to Sha1 while :main still offers Sha2: the poll is known-bad, the state keeps Sha1 as last good,
    # and the clone still moves to origin/main. The clone position is never the rollback anchor.
    $r = Invoke-TestDeploy -State @{ current = @{ sha = $script:Sha1 }; failedSha = $script:Sha2 }
    Assert-Equal 'known-bad' $r.Result.Outcome
    Assert-Equal $script:Sha1 $r.State.current.sha 'last good is untouched by the clone move'
    Assert-Equal 1 (Get-RepoMerges).Count 'the clone still tracks main'
    Assert-Match (Get-RepoMerges)[0].Display 'merge --ff-only origin/main'
}

# ---- the dry run and the plan ------------------------------------------------------------------------------------------------

Test-Case 'plan: the dry run lists the poll-start fast-forward and the one-time .ipa removal, and no release check' {
    $root = New-TestRoot
    try {
        $plan = (Get-CmPlan -Ctx (New-TestContext -Root $root)) -join "`n"
        Assert-Match $plan 'merge --ff-only origin/main'
        Assert-Match $plan 'rm -rf /data/files/ipa'
        Assert-False ($plan -match 'release view|Sync-Ipa') 'nothing about releases'
    }
    finally { Remove-TestRoot $root }
}
