# OPS-IPA2, then ADR-024: a good deploy keeps the deployer's own scripts current, and the .ipa the server used to serve
# (before the iPhone app moved to TestFlight) is removed once from the files volume. Nothing reads GitHub releases now.
# Dot-sourced by Run-Tests.ps1 after deployer.tests.ps1 and ops1b.tests.ps1 (it uses their helpers).

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
    Assert-Equal 0 (@(Get-MutatingCalls) | Where-Object { $_.Display -notmatch '^docker pull' }).Count 'only the pull'
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

Test-Case 'retired .ipa: a good deploy removes it too, after the smoke test and the fast-forward' {
    $state = Get-OldGoodState
    $state['ipa'] = $script:RetiredIpa['ipa']
    $r = Invoke-TestDeploy -State $state
    Assert-Equal 'deployed' $r.Result.Outcome
    Assert-Equal 0 $r.Result.ExitCode
    $remove = Get-CallIndex '*rm -rf /data/files/ipa*'
    $merge = Get-CallIndex 'git * merge --ff-only*'
    $smoke = Get-CallIndex 'curl*health/ready'
    Assert-True ($remove -gt $merge -and $merge -gt $smoke) "smoke=$smoke merge=$merge remove=$remove"
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

# ---- after a good deploy the deployer's own scripts follow the deployed commit --------------------------------------------

Test-Case 'deploy: a good deploy fast-forwards the deployer clone to the deployed commit, and only that' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState)
    Assert-Equal 'deployed' $r.Result.Outcome
    $merges = Get-RepoMerges
    Assert-Equal 1 $merges.Count
    Assert-Match $merges[0].Display "merge --ff-only $($script:Sha2)\s*$"
    Assert-Match $merges[0].Display '^git -C \S*repo merge'
    Assert-Equal 0 (Get-Calls 'git' '*reset*').Count 'never resets'
    Assert-Equal 0 (Get-Calls 'git' '*checkout*').Count 'never switches or discards'
    Assert-Equal 0 (Get-Calls 'git' '*--force*').Count
    Assert-Equal 0 (Get-Calls 'git' '* clean*').Count
    Assert-Equal 0 (Get-AlertCalls).Count
}

Test-Case 'deploy: the clone is only touched after the new version passed its smoke test' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState)
    $smoke = Get-CallIndex 'curl*health/ready'
    $merge = Get-CallIndex 'git * merge --ff-only*'
    Assert-True ($smoke -ge 0 -and $merge -gt $smoke) "smoke=$smoke merge=$merge"
}

Test-Case 'deploy: it checks the clone is clean and on main before it merges' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState)
    $status = Get-CallIndex 'git * status --porcelain*'
    $branch = Get-CallIndex 'git * symbolic-ref*'
    $merge = Get-CallIndex 'git * merge --ff-only*'
    Assert-True ($status -ge 0 -and $branch -ge 0 -and $status -lt $merge -and $branch -lt $merge) "status=$status branch=$branch merge=$merge"
    Assert-Match (Get-Calls 'git' '*status --porcelain*')[0].Display '--untracked-files=no'
}

Test-Case 'deploy: a clone with local changes is left alone with a warning, not an alert' {
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

Test-Case 'deploy: the first ever deploy has nothing fetched yet, so the clone is only moved if the commit is there' {
    $r = Invoke-TestDeploy -Machine @{ MergeFails = $true }
    Assert-Equal 'deployed' $r.Result.Outcome
    Assert-Equal 0 $r.Result.ExitCode
}

Test-Case 'deploy: a rolled-back or failed deploy never moves the clone' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine @{ UnhealthyTags = @($script:Sha2) }
    Assert-Equal 'rolled-back' $r.Result.Outcome
    Assert-Equal 0 (Get-RepoMerges).Count
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine @{ MigrateExit = 1 }
    Assert-Equal 0 (Get-RepoMerges).Count
}

Test-Case 'deploy: a quiet run and a manual rollback leave the clone alone' {
    $r = Invoke-TestDeploy -State (Get-QuietState)
    Assert-Equal 0 (Get-RepoMerges).Count
    $global:CmCalls.Clear()
    $r = Invoke-TestDeploy -RollbackTo $script:Sha1 -State (Get-StateAt $script:Sha2) -Machine @{ Sha = $script:Sha1 }
    Assert-Equal 'manual-rollback' $r.Result.Outcome
    Assert-Equal 0 (Get-RepoMerges).Count
}

# ---- the dry run and the plan ------------------------------------------------------------------------------------------------

Test-Case 'plan: the dry run lists the fast-forward and the one-time .ipa removal, and no release check' {
    $root = New-TestRoot
    try {
        $plan = (Get-CmPlan -Ctx (New-TestContext -Root $root)) -join "`n"
        Assert-Match $plan 'merge --ff-only <sha>'
        Assert-Match $plan 'rm -rf /data/files/ipa'
        Assert-False ($plan -match 'release view|Sync-Ipa') 'nothing about releases'
    }
    finally { Remove-TestRoot $root }
}
