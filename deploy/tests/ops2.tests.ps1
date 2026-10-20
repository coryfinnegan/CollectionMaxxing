# OPS-IPA2: a run with nothing to deploy still syncs a new release's .ipa, and a good deploy keeps the deployer's own scripts current.
# Dot-sourced by Run-Tests.ps1 after deployer.tests.ps1 and ops1b.tests.ps1 (it uses their helpers).

# The deployed commit is already the newest main image (a quiet run). The last .ipa sync was release v1.0.1.
function Get-QuietState {
    param([hashtable]$Extra = @{}, [string]$IpaTag = 'v1.0.1')
    $state = @{
        current = @{ sha = $script:Sha2; created = '2026-10-13T12:00:00Z'; deployedAt = '2026-10-13T12:05:00Z' }
        ipa     = @{ sha256 = ('0' * 64); file = 'CollectionMaxxing-unsigned.ipa'; version = ($IpaTag -replace '^v'); tag = $IpaTag }
    }
    foreach ($k in $Extra.Keys) { $state[$k] = $Extra[$k] }
    $state
}

function Get-Downloads { @(Get-Calls 'gh' '*release download*') }
function Get-IpaCopies { @(Get-Calls 'docker' '*run --rm --network none*') }
function Get-RepoMerges { @(Get-Calls 'git' '*merge --ff-only*') }
function Get-AlertLines { param([string]$Log) @([regex]::Matches($Log, 'ALERT ')).Count }

# ---- a quiet run syncs a new release ------------------------------------------------------------------------------------

Test-Case 'quiet run: a new release tag is synced once, even though there is nothing to deploy' {
    $r = Invoke-TestDeploy -State (Get-QuietState) -Machine @{ Tag = 'v1.0.2' }
    Assert-Equal 'ipa-synced' $r.Result.Outcome
    Assert-Equal 0 $r.Result.ExitCode
    Assert-Equal 1 (Get-Downloads).Count
    Assert-Equal 1 (Get-IpaCopies).Count
    Assert-Equal 'v1.0.2' $r.State.ipa.tag 'the synced tag is remembered in the state file'
    Assert-Equal '1.0.2' $r.State.ipa.version
    Assert-Equal $script:Sha2 $r.State.current.sha 'nothing was deployed'
    Assert-Equal 0 (Get-Calls 'docker' '*up -d*').Count
    Assert-Equal 0 (Get-AlertCalls).Count
}

Test-Case 'quiet run: the same release tag as last time costs one gh release view and nothing else' {
    $r = Invoke-TestDeploy -State (Get-QuietState -IpaTag 'v1.0.2') -Machine @{ Tag = 'v1.0.2' }
    Assert-Equal 'up-to-date' $r.Result.Outcome
    Assert-Equal 0 $r.Result.ExitCode
    Assert-Equal 1 (Get-Calls 'gh' '*release view*').Count
    Assert-Equal 0 (Get-Downloads).Count
    Assert-Equal 0 (Get-IpaCopies).Count
}

Test-Case 'quiet run: the next run after a sync does not download again' {
    $first = Invoke-TestDeploy -State (Get-QuietState) -Machine @{ Tag = 'v1.0.2' }
    $global:CmCalls.Clear()
    $second = Invoke-TestDeploy -State $first.State -Machine @{ Tag = 'v1.0.2' }
    Assert-Equal 'up-to-date' $second.Result.Outcome
    Assert-Equal 0 (Get-Downloads).Count
}

Test-Case 'quiet run: an install that never synced an .ipa syncs the latest release' {
    $state = Get-QuietState
    $state.Remove('ipa')
    $r = Invoke-TestDeploy -State $state -Machine @{ Tag = 'v1.0.2' }
    Assert-Equal 'ipa-synced' $r.Result.Outcome
    Assert-Equal 'v1.0.2' $r.State.ipa.tag
}

Test-Case 'quiet run: a state file from before tags were remembered syncs once, then remembers the tag' {
    $state = Get-QuietState
    $state.ipa.Remove('tag')
    $r = Invoke-TestDeploy -State $state -Machine @{ Tag = 'v1.0.2' }
    Assert-Equal 'ipa-synced' $r.Result.Outcome
    Assert-Equal 'v1.0.2' $r.State.ipa.tag
}

Test-Case 'quiet run: a new tag whose .ipa is byte for byte the one already served only records the tag' {
    $sha = (Get-FileHash -InputStream ([IO.MemoryStream]::new([byte[]](1, 2, 3, 4))) -Algorithm SHA256).Hash.ToLower()
    $state = Get-QuietState
    $state.ipa.sha256 = $sha
    $state.ipa.version = '1.0.2'
    $r = Invoke-TestDeploy -State $state -Machine @{ Tag = 'v1.0.2' }
    Assert-Equal 'up-to-date' $r.Result.Outcome
    Assert-Equal 0 (Get-IpaCopies).Count 'nothing to copy'
    Assert-Equal 'v1.0.2' $r.State.ipa.tag 'so the next run does not download it again'
}

Test-Case 'quiet run: a latest release that cannot be read is silent and changes nothing' {
    $r = Invoke-TestDeploy -State (Get-QuietState) -Machine @{ Tag = $null }
    Assert-Equal 'up-to-date' $r.Result.Outcome
    Assert-Equal 0 $r.Result.ExitCode
    Assert-Equal 0 (Get-Downloads).Count
    Assert-Equal 0 (Get-AlertCalls).Count
    Assert-Equal 'v1.0.1' $r.State.ipa.tag
}

Test-Case 'quiet run: a commit that already failed here still gets its release synced' {
    $state = Get-QuietState
    $state.current.sha = $script:Sha1
    $state.failedSha = $script:Sha2
    $r = Invoke-TestDeploy -State $state -Machine @{ Tag = 'v1.0.2' }
    Assert-Equal 'ipa-synced' $r.Result.Outcome
    Assert-Equal 0 (Get-Calls 'docker' '*up -d*').Count
}

Test-Case 'quiet run: a downgrade refusal does not sync (it is a loud failure of its own)' {
    $state = Get-QuietState
    $r = Invoke-TestDeploy -State $state -Machine @{ Tag = 'v1.0.2'; Sha = $script:Sha1 }
    Assert-Equal 'downgrade-refused' $r.Result.Outcome
    Assert-Equal 0 (Get-Downloads).Count
}

# ---- failures: one alert per tag, then quiet --------------------------------------------------------------------------------

Test-Case 'quiet run: a sync that fails its checksum exits 5 once, alerts once and leaves the served .ipa alone' {
    $r = Invoke-TestDeploy -State (Get-QuietState) -Machine @{ Tag = 'v1.0.2'; IpaSha256 = ('f' * 64) }
    Assert-Equal 'ipa-failed' $r.Result.Outcome
    Assert-Equal 5 $r.Result.ExitCode
    Assert-Equal 0 (Get-IpaCopies).Count
    Assert-Equal 1 (Get-AlertCalls).Count
    Assert-Equal 1 (Get-AlertLines $r.Log)
    Assert-Equal 'v1.0.1' $r.State.ipa.tag 'the served .ipa is still the old release'
    Assert-Equal 'v1.0.2' $r.State.ipaAttempt.tag
    Assert-Equal $script:Sha2 $r.State.current.sha 'the deploy state is untouched'
}

Test-Case 'quiet run: the same failing tag on the next runs is silent: no download, no log line, no alert, exit 0' {
    $first = Invoke-TestDeploy -State (Get-QuietState) -Machine @{ Tag = 'v1.0.2'; IpaSha256 = ('f' * 64) }
    $global:CmCalls.Clear()
    $second = Invoke-TestDeploy -State $first.State -Machine @{ Tag = 'v1.0.2'; IpaSha256 = ('f' * 64) }
    Assert-Equal 0 $second.Result.ExitCode
    Assert-Equal 0 (Get-Downloads).Count
    Assert-Equal 0 (Get-AlertCalls).Count
    Assert-Equal '' $second.Log.Trim() 'nothing is logged'
}

Test-Case 'quiet run: a failing tag is tried again after the wait, silently, and a fixed .ipa then syncs' {
    $attempt = @{ tag = 'v1.0.2'; outcome = 'checksum-mismatch'; at = [DateTime]::UtcNow.AddMinutes(-20).ToString('o') }
    $still = Invoke-TestDeploy -State (Get-QuietState -Extra @{ ipaAttempt = $attempt }) -Machine @{ Tag = 'v1.0.2'; IpaSha256 = ('f' * 64) }
    Assert-Equal 1 (Get-Downloads).Count 'tried again'
    Assert-Equal 0 $still.Result.ExitCode 'a repeat failure does not alert again'
    Assert-Equal 0 (Get-AlertCalls).Count
    Assert-True ((ConvertTo-CmUtc $still.State.ipaAttempt.at) -gt [DateTime]::UtcNow.AddMinutes(-1)) 'the wait starts over'

    $global:CmCalls.Clear()
    $fixed = Invoke-TestDeploy -State (Get-QuietState -Extra @{ ipaAttempt = $attempt }) -Machine @{ Tag = 'v1.0.2' }
    Assert-Equal 'ipa-synced' $fixed.Result.Outcome
    Assert-Equal 'v1.0.2' $fixed.State.ipa.tag
    Assert-False $fixed.State.ContainsKey('ipaAttempt') 'the failure is forgotten'
}

Test-Case 'quiet run: a failing tag within the wait is not tried again' {
    $attempt = @{ tag = 'v1.0.2'; outcome = 'copy-failed'; at = [DateTime]::UtcNow.AddMinutes(-3).ToString('o') }
    $r = Invoke-TestDeploy -State (Get-QuietState -Extra @{ ipaAttempt = $attempt }) -Machine @{ Tag = 'v1.0.2' }
    Assert-Equal 0 (Get-Downloads).Count
}

Test-Case 'quiet run: the next release after a failed one is tried at once, and alerts once if it fails too' {
    $attempt = @{ tag = 'v1.0.2'; outcome = 'checksum-mismatch'; at = [DateTime]::UtcNow.AddMinutes(-1).ToString('o') }
    $r = Invoke-TestDeploy -State (Get-QuietState -Extra @{ ipaAttempt = $attempt }) -Machine @{ Tag = 'v1.0.3'; IpaSha256 = ('f' * 64) }
    Assert-Equal 'ipa-failed' $r.Result.Outcome
    Assert-Equal 1 (Get-AlertCalls).Count
    Assert-Equal 'v1.0.3' $r.State.ipaAttempt.tag
}

Test-Case 'quiet run: a failed copy into the volume is the same kind of failure' {
    $r = Invoke-TestDeploy -State (Get-QuietState) -Machine @{ Tag = 'v1.0.2'; CopyFails = $true }
    Assert-Equal 'ipa-failed' $r.Result.Outcome
    Assert-Equal 5 $r.Result.ExitCode
    Assert-Equal 'copy-failed' $r.State.ipaAttempt.outcome
}

Test-Case 'quiet run: a release that has no .ipa yet is not an alert, is logged once and is looked at again later' {
    $first = Invoke-TestDeploy -State (Get-QuietState) -Machine @{ Tag = 'v1.0.2'; NoIpaAsset = $true }
    Assert-Equal 0 $first.Result.ExitCode
    Assert-Equal 0 (Get-AlertCalls).Count
    Assert-Equal 'no-release' $first.State.ipaAttempt.outcome
    Assert-Equal 'v1.0.1' $first.State.ipa.tag
    Assert-Match $first.Log 'v1\.0\.2'

    $global:CmCalls.Clear()
    $soon = Invoke-TestDeploy -State $first.State -Machine @{ Tag = 'v1.0.2'; NoIpaAsset = $true }
    Assert-Equal 0 (Get-Downloads).Count 'not hammered while it is still missing'
    Assert-Equal '' $soon.Log.Trim()

    $global:CmCalls.Clear()
    $state = $first.State
    $state.ipaAttempt.at = [DateTime]::UtcNow.AddMinutes(-20).ToString('o')
    $later = Invoke-TestDeploy -State $state -Machine @{ Tag = 'v1.0.2' }
    Assert-Equal 'ipa-synced' $later.Result.Outcome
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
    $r = Invoke-TestDeploy -State (Get-QuietState -IpaTag 'v1.0.2') -Machine @{ Tag = 'v1.0.2' }
    Assert-Equal 0 (Get-RepoMerges).Count
    $global:CmCalls.Clear()
    $r = Invoke-TestDeploy -RollbackTo $script:Sha1 -State (Get-StateAt $script:Sha2) -Machine @{ Sha = $script:Sha1 }
    Assert-Equal 'manual-rollback' $r.Result.Outcome
    Assert-Equal 0 (Get-RepoMerges).Count
}

Test-Case 'deploy: a failed .ipa sync after a good deploy still exits 5 and the clone has already moved' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState) -Machine @{ IpaSha256 = ('f' * 64) }
    Assert-Equal 'ipa-failed' $r.Result.Outcome
    Assert-Equal 1 (Get-RepoMerges).Count
    Assert-Equal 1 (Get-AlertCalls).Count
    Assert-Equal 'v1.0.2' $r.State.ipaAttempt.tag 'so the quiet runs after it do not alert again'
}

Test-Case 'deploy: the post-deploy sync records the tag it synced' {
    $r = Invoke-TestDeploy -State (Get-OldGoodState)
    Assert-Equal 'v1.0.2' $r.State.ipa.tag
}

# ---- the dry run and the plan ------------------------------------------------------------------------------------------------

Test-Case 'plan: the dry run lists the release check and the fast-forward' {
    $root = New-TestRoot
    try {
        $plan = (Get-CmPlan -Ctx (New-TestContext -Root $root)) -join "`n"
        Assert-Match $plan 'release view'
        Assert-Match $plan 'merge --ff-only <sha>'
    }
    finally { Remove-TestRoot $root }
}
