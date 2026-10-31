# Alerts as GitHub issues (deploy/deployer/Cm.Alerts.ps1). Dot-sourced by Run-Tests.ps1 after TestKit.ps1 and the deployer
# libraries. Self-contained on purpose: the test files load in alphabetical order, so nothing here may use a helper that
# another test file defines. The fake machine's `Issues` list plays GitHub; a test shares one list across its polls so an
# issue opened by the first poll is there for the next, and $global:CmNow plays the clock.

$script:AlertT0 = [DateTime]::new(2026, 10, 31, 12, 0, 0, [DateTimeKind]::Utc)
$script:AlertSha3 = ('c3' * 20)

function Get-AlertOldState {
    @{ current = @{ sha = $script:Sha1; created = '2026-10-12T12:00:00.0000000Z'; deployedAt = '2026-10-12T12:05:00Z' } }
}

function Get-AlertAt { param([double]$Minutes) $script:AlertT0.AddMinutes($Minutes) }

function Invoke-AlertPolls {
    <#
      Runs several deployer polls against ONE throwaway root, so the state file, the log and the fake GitHub carry over.
      A step is @{ At = <UTC time>; Machine = @{ ...New-FakeMachine options... }; RollbackTo; DryRun; LockHeldMinutes;
      After = <scriptblock given the fake GitHub's issue list once the poll is over> }.
      Returns Polls (Result and the calls that poll made), Issues (the fake GitHub's list), State, Log.
    #>
    param([object[]]$Steps, $State = $null, $StateText = $null, [string[]]$ExtraEnv = @(), [scriptblock]$Setup = $null, $SeedIssues = @())
    $root = New-TestRoot
    try {
        if ($State) { Set-TestState -Root $root -State $State }
        if ($null -ne $StateText) {
            New-Item -ItemType Directory -Force -Path (Join-Path $root 'state') | Out-Null
            Set-Content -LiteralPath (Join-Path $root 'state/deployer-state.json') -Value $StateText -NoNewline
        }
        if ($ExtraEnv.Count -gt 0) { Add-Content -LiteralPath (Join-Path $root '.env') -Value $ExtraEnv }
        if ($Setup) { & $Setup $root }
        $store = [System.Collections.Generic.List[object]]::new()
        foreach ($seed in @($SeedIssues)) { $store.Add($seed) }
        $polls = [System.Collections.Generic.List[object]]::new()
        foreach ($step in $Steps) {
            $global:CmNow = $step.At
            $machine = @{}
            if ($step.Machine) { foreach ($k in $step.Machine.Keys) { $machine[$k] = $step.Machine[$k] } }
            $machine['Issues'] = $store
            New-FakeMachine $machine | Out-Null
            Initialize-Cm -Apply (-not $step.DryRun)
            $ctx = New-TestContext -Root $root
            $global:CmCalls.Clear()
            $held = $null
            try {
                if ($step.LockHeldMinutes) {
                    $lockPath = Join-Path $root 'state/deployer.lock'
                    New-Item -ItemType Directory -Force -Path (Join-Path $root 'state') | Out-Null
                    Set-Content -LiteralPath $lockPath -Value 'pid=99999' -NoNewline
                    [IO.File]::SetLastWriteTimeUtc($lockPath, [DateTime]::UtcNow.AddMinutes(-$step.LockHeldMinutes))
                    $held = [System.IO.File]::Open($lockPath, 'Open', 'ReadWrite', 'None')
                }
                $rollback = if ($step.RollbackTo) { "$($step.RollbackTo)" } else { '' }
                $result = Invoke-CmDeploy -Ctx $ctx -RollbackTo $rollback 6>$null
            }
            finally { if ($held) { $held.Dispose() } }
            $polls.Add([pscustomobject]@{ Result = $result; Calls = @($global:CmCalls) })
            if ($step.After) { & $step.After $store }
        }
        $logPath = Join-Path $root 'logs/deployer.log'
        $log = if (Test-Path -LiteralPath $logPath) { Get-Content -LiteralPath $logPath -Raw } else { '' }
        $stateText = if (Test-Path -LiteralPath (Join-Path $root 'state/deployer-state.json')) { Get-Content -LiteralPath (Join-Path $root 'state/deployer-state.json') -Raw } else { '' }
        [pscustomobject]@{ Polls = @($polls); Issues = $store; State = (Get-TestState -Root $root); StateText = $stateText; Log = $log; Root = $root }
    }
    finally { $global:CmNow = $null; Remove-TestRoot $root }
}

function Get-AlertGhCalls {
    <# The `gh issue <verb>` calls one poll made (verb: list, create, comment or close). #>
    param($Poll, [string]$Verb)
    @($Poll.Calls | Where-Object { $_.Command -eq 'gh' -and $_.Display -like "*issue $Verb *" })
}

function Get-AlertIssueCalls {
    <# Every `gh issue ...` call of one poll (the attestation checks are gh calls too, but not these). #>
    param($Poll)
    @($Poll.Calls | Where-Object { $_.Command -eq 'gh' -and $_.Display -like '*issue *' })
}

function Get-AlertWrites {
    <# Every create, comment and close call of one poll. #>
    param($Poll)
    @($Poll.Calls | Where-Object { $_.Command -eq 'gh' -and $_.Display -match 'issue (create|comment|close) ' })
}

function Get-AlertPings { param($Poll) @($Poll.Calls | Where-Object { $_.Command -eq 'curl' -and $_.Display -like '*/fail*' }) }

function New-AlertSeedIssue {
    param([int]$Number, [string]$Kind, [string]$State = 'open')
    [pscustomobject]@{ number = $Number; title = 'seeded'; body = "<!-- cm-alert:$Kind -->`nseeded"; labels = @('alert', 'type:infra'); state = $State; comments = [System.Collections.Generic.List[string]]::new(); closeComment = ''; updatedAt = '2026-10-30T00:00:00Z' }
}

$script:AgentLabels = 'alert,type:infra,agent-task,p1,sev:blocker'
$script:OwnerLabels = 'alert,type:infra,needs:cory,p1'

# ---- the first alert of a kind opens one issue, with the right labels ------------------------------------------------------

Test-Case 'alert issues: a deploy that fails its smoke test opens one issue with the agent labels' {
    $r = Invoke-AlertPolls -State (Get-AlertOldState) -Steps @(@{ At = $script:AlertT0; Machine = @{ UnhealthyTags = @($script:Sha2) } })
    Assert-Equal 'rolled-back' $r.Polls[0].Result.Outcome
    Assert-Equal 1 $r.Issues.Count
    Assert-Equal $script:AgentLabels ($r.Issues[0].labels -join ',')
    Assert-Match $r.Issues[0].body '<!-- cm-alert:smoke-failed -->'
    Assert-Match $r.Issues[0].title 'smoke test'
    Assert-Equal 1 (Get-AlertGhCalls $r.Polls[0] 'create').Count
    Assert-Equal 1 (Get-AlertPings $r.Polls[0]).Count 'the heartbeat ping is still sent'
    Assert-Match $r.Log 'ALERT deploy rolled-back'
}

Test-Case 'alert issues: a rolled-back deploy whose services fail is a different kind from a failed smoke test' {
    $r = Invoke-AlertPolls -State (Get-AlertOldState) -Steps @(@{ At = $script:AlertT0; Machine = @{ BrokenServices = @{ $script:Sha2 = @('backup') } } })
    Assert-Equal 'rolled-back' $r.Polls[0].Result.Outcome
    Assert-Equal 1 $r.Issues.Count
    Assert-Match $r.Issues[0].body '<!-- cm-alert:deploy-rolled-back -->'
    Assert-Equal $script:AgentLabels ($r.Issues[0].labels -join ',')
}

Test-Case 'alert issues: every kind gets its labels: code problems an agent can fix are agent tasks, the rest need the owner' {
    $cases = @(
        @{ Kind = 'migrate-failed'; Labels = $script:AgentLabels; Machine = @{ MigrateExit = 1 }; State = (Get-AlertOldState) },
        @{ Kind = 'config-guard-refused'; Labels = $script:AgentLabels; State = (Get-AlertOldState)
            Machine = @{ ComposeConfig = [pscustomobject]@{ services = [pscustomobject]@{ api = [pscustomobject]@{ ports = @([pscustomobject]@{ host_ip = '0.0.0.0'; published = '5480'; target = 5480 }) } } } } },
        @{ Kind = 'attestation-failed'; Labels = $script:OwnerLabels; Machine = @{ BadAttest = @('api') }; State = (Get-AlertOldState) },
        @{ Kind = 'pre-migrate-dump-failed'; Labels = $script:OwnerLabels; Machine = @{ DumpExit = 1; ChangedFiles = @('src/Api/Migrations/20261031_Add.cs') }; State = (Get-AlertOldState) },
        @{ Kind = 'downgrade-refused'; Labels = $script:OwnerLabels; Machine = @{ Sha = $script:Sha1 }; State = @{ current = @{ sha = $script:Sha2; created = '2026-10-14T00:00:00.0000000Z' } } },
        @{ Kind = 'deployer-setup-refused'; Labels = $script:OwnerLabels; Machine = @{ RunningServices = @('abc123') }; State = $null }
    )
    foreach ($case in $cases) {
        $r = Invoke-AlertPolls -State $case.State -Steps @(@{ At = $script:AlertT0; Machine = $case.Machine })
        Assert-Equal 1 $r.Issues.Count "$($case.Kind): one issue"
        Assert-Match $r.Issues[0].body "<!-- cm-alert:$($case.Kind) -->" "$($case.Kind): the marker"
        Assert-Equal $case.Labels ($r.Issues[0].labels -join ',') "$($case.Kind): labels"
    }
}

Test-Case 'alert issues: a stuck lock opens a needs:cory issue' {
    $r = Invoke-AlertPolls -State (Get-AlertOldState) -Steps @(@{ At = $script:AlertT0; LockHeldMinutes = 45 })
    Assert-Equal 'stale-lock' $r.Polls[0].Result.Outcome
    Assert-Equal 1 $r.Issues.Count
    Assert-Match $r.Issues[0].body '<!-- cm-alert:deployer-stuck -->'
    Assert-Equal $script:OwnerLabels ($r.Issues[0].labels -join ',')
}

Test-Case 'alert issues: the body says what happened, when, both commits, what to do, and the log tail' {
    $r = Invoke-AlertPolls -State (Get-AlertOldState) -Steps @(@{ At = $script:AlertT0; Machine = @{ BadAttest = @('api') } })
    $body = $r.Issues[0].body
    Assert-Match $body '\*\*What happened:\*\*'
    Assert-Match $body '2026-10-31 12:00:00 UTC'
    Assert-Match $body ([regex]::Escape($script:Sha1.Substring(0, 12))) 'the deployed commit'
    Assert-Match $body ([regex]::Escape($script:Sha2.Substring(0, 12))) 'the new commit'
    Assert-Match $body '\*\*What to do:\*\*'
    Assert-Match $body 'ALERT deploy refused' 'the log tail includes the alert line'
    Assert-Match $body 'Do not bypass' 'an attestation failure is never something to bypass'
    Assert-False ($body -match 'agent-task') 'owner issues do not talk to agents'
    $agent = Invoke-AlertPolls -State (Get-AlertOldState) -Steps @(@{ At = $script:AlertT0; Machine = @{ MigrateExit = 1 } })
    Assert-Match $agent.Issues[0].body 'deployer\.log' 'an agent task points at the log'
}

Test-Case 'alert issues: the log tail is the last 20 lines' {
    $setup = { param($root) New-Item -ItemType Directory -Force -Path (Join-Path $root 'logs') | Out-Null; Set-Content -LiteralPath (Join-Path $root 'logs/deployer.log') -Value (1..60 | ForEach-Object { "old line $_" }) }
    $r = Invoke-AlertPolls -State (Get-AlertOldState) -Setup $setup -Steps @(@{ At = $script:AlertT0; Machine = @{ BadAttest = @('api') } })
    $body = $r.Issues[0].body
    Assert-False ($body -match 'old line 1\b') 'old lines are not in the tail'
    Assert-Match $body 'old line 60\b' 'the newest old line is'
    $inside = [regex]::Match($body, '(?s)```text\n(.*?)\n```').Groups[1].Value
    Assert-True ((@($inside -split "`n")).Count -le 20) 'at most 20 lines'
}

# ---- one issue per problem: a repeat comments, at most once an hour ------------------------------------------------------

Test-Case 'alert issues: polls repeating the same problem within the hour do not touch GitHub again' {
    $steps = 0, 2, 4, 30, 59 | ForEach-Object { @{ At = (Get-AlertAt $_); Machine = @{ BadAttest = @('api') } } }
    $r = Invoke-AlertPolls -State (Get-AlertOldState) -Steps $steps
    Assert-Equal 1 $r.Issues.Count
    Assert-Equal 0 $r.Issues[0].comments.Count
    Assert-Equal 1 (Get-AlertWrites $r.Polls[0]).Count
    foreach ($i in 1..4) { Assert-Equal 0 (Get-AlertIssueCalls $r.Polls[$i]).Count "poll $i makes no gh issue call" }
    foreach ($i in 0..4) { Assert-Equal 1 (Get-AlertPings $r.Polls[$i]).Count "the heartbeat still pings on poll $i" }
}

Test-Case 'alert issues: a repeat after the hour adds a comment with the time and how many times today' {
    $steps = 0, 2, 60 | ForEach-Object { @{ At = (Get-AlertAt $_); Machine = @{ BadAttest = @('api') } } }
    $r = Invoke-AlertPolls -State (Get-AlertOldState) -Steps $steps
    Assert-Equal 1 $r.Issues.Count 'still one issue'
    Assert-Equal 1 $r.Issues[0].comments.Count
    Assert-Match $r.Issues[0].comments[0] 'Again at 2026-10-31 13:00 UTC, 3 times today'
    Assert-Equal 1 (Get-AlertGhCalls $r.Polls[2] 'comment').Count
    Assert-Equal 0 (Get-AlertGhCalls $r.Polls[2] 'create').Count
}

Test-Case 'alert issues: at most one comment per kind per hour' {
    $steps = 0, 30, 59, 60, 61, 119, 120, 150 | ForEach-Object { @{ At = (Get-AlertAt $_); Machine = @{ BadAttest = @('api') } } }
    $r = Invoke-AlertPolls -State (Get-AlertOldState) -Steps $steps
    Assert-Equal 1 $r.Issues.Count
    Assert-Equal 2 $r.Issues[0].comments.Count 'one at +60 and one at +120'
}

Test-Case 'alert issues: the times-today count starts again on a new day' {
    $steps = @(
        @{ At = (Get-AlertAt 0); Machine = @{ BadAttest = @('api') } },
        @{ At = (Get-AlertAt 30); Machine = @{ BadAttest = @('api') } },
        @{ At = (Get-AlertAt 1500); Machine = @{ BadAttest = @('api') } })   # 25 hours later: the next day
    $r = Invoke-AlertPolls -State (Get-AlertOldState) -Steps $steps
    Assert-Match $r.Issues[0].comments[0] 'Again at 2026-11-01 13:00 UTC, 1 time today'
}

Test-Case 'alert issues: two different problems are two issues' {
    $r = Invoke-AlertPolls -State (Get-AlertOldState) -Steps @(
        @{ At = (Get-AlertAt 0); Machine = @{ BadAttest = @('api') } },
        @{ At = (Get-AlertAt 2); Machine = @{ MigrateExit = 1; Sha = $script:AlertSha3; Graph = @{ $script:Sha1 = @(); $script:Sha2 = @($script:Sha1); $script:AlertSha3 = @($script:Sha1, $script:Sha2) } } })
    Assert-Equal 2 $r.Issues.Count
    Assert-Match $r.Issues[0].body 'cm-alert:attestation-failed'
    Assert-Match $r.Issues[1].body 'cm-alert:migrate-failed'
}

Test-Case 'alert issues: an open issue is found by its marker even when the state remembers nothing' {
    $seed = New-AlertSeedIssue -Number 7 -Kind 'attestation-failed'
    $r = Invoke-AlertPolls -State (Get-AlertOldState) -SeedIssues @($seed) -Steps @(@{ At = $script:AlertT0; Machine = @{ BadAttest = @('api') } })
    Assert-Equal 1 $r.Issues.Count 'no duplicate'
    Assert-Equal 0 (Get-AlertGhCalls $r.Polls[0] 'create').Count
    Assert-Equal 1 $r.Issues[0].comments.Count
    Assert-Equal 7 $r.State.alerts.kinds['attestation-failed'].issue
}

Test-Case 'alert issues: an issue the owner closed does not swallow the next alert' {
    $r = Invoke-AlertPolls -State (Get-AlertOldState) -Steps @(
        @{ At = (Get-AlertAt 0); Machine = @{ BadAttest = @('api') }; After = { param($issues) $issues[0].state = 'closed' } },
        @{ At = (Get-AlertAt 70); Machine = @{ BadAttest = @('api') } })
    Assert-Equal 2 $r.Issues.Count 'a new issue, because the old one is closed'
    Assert-Equal 'open' $r.Issues[1].state
    Assert-Equal 0 $r.Issues[0].comments.Count 'nothing is added to the closed one'
}

# ---- closing: the issue closes itself when the condition clears ----------------------------------------------------------

Test-Case 'alert issues: the next good deploy closes the issue with a line saying what cleared it' {
    $r = Invoke-AlertPolls -State (Get-AlertOldState) -Steps @(
        @{ At = (Get-AlertAt 0); Machine = @{ BadAttest = @('api') } },
        @{ At = (Get-AlertAt 4) })
    Assert-Equal 'deployed' $r.Polls[1].Result.Outcome
    Assert-Equal 1 $r.Issues.Count
    Assert-Equal 'closed' $r.Issues[0].state
    Assert-Match $r.Issues[0].closeComment 'Resolved at 2026-10-31 12:04 UTC: the deploy of b2b2b2b2b2b2 succeeded'
    Assert-Equal 1 (Get-AlertGhCalls $r.Polls[1] 'close').Count
    Assert-False ($r.State.alerts.kinds.ContainsKey('attestation-failed')) 'nothing left to remember'
    Assert-Equal $script:Sha2 $r.State.current.sha 'the deploy itself is untouched'
}

Test-Case 'alert issues: a good deploy closes every deploy problem that was open' {
    $r = Invoke-AlertPolls -State (Get-AlertOldState) -Steps @(
        @{ At = (Get-AlertAt 0); Machine = @{ BadAttest = @('api') } },
        @{ At = (Get-AlertAt 2); Machine = @{ MigrateExit = 1 } },
        @{ At = (Get-AlertAt 4); Machine = @{ Sha = $script:AlertSha3; Graph = @{ $script:Sha1 = @(); $script:Sha2 = @($script:Sha1); $script:AlertSha3 = @($script:Sha1, $script:Sha2) } } })
    Assert-Equal 'deployed' $r.Polls[2].Result.Outcome
    Assert-Equal 2 $r.Issues.Count
    Assert-Equal 0 (@($r.Issues | Where-Object { $_.state -eq 'open' })).Count
    Assert-Equal 2 (Get-AlertGhCalls $r.Polls[2] 'close').Count
}

Test-Case 'alert issues: a failed deploy is not cleared by the polls that follow it, only by a newer good one' {
    $r = Invoke-AlertPolls -State (Get-AlertOldState) -Steps @(
        @{ At = (Get-AlertAt 0); Machine = @{ UnhealthyTags = @($script:Sha2) } },
        @{ At = (Get-AlertAt 2) },
        @{ At = (Get-AlertAt 4) })
    Assert-Equal 'rolled-back' $r.Polls[0].Result.Outcome
    Assert-Equal 'known-bad' $r.Polls[1].Result.Outcome
    Assert-Equal 'known-bad' $r.Polls[2].Result.Outcome
    Assert-Equal 'open' $r.Issues[0].state 'the bad commit is still main'
    Assert-Equal 0 (Get-AlertWrites $r.Polls[1]).Count
}

Test-Case 'alert issues: when main is the running version again the failed deploy is no longer a problem' {
    $r = Invoke-AlertPolls -State (Get-AlertOldState) -Steps @(
        @{ At = (Get-AlertAt 0); Machine = @{ UnhealthyTags = @($script:Sha2) } },
        @{ At = (Get-AlertAt 2); Machine = @{ Sha = $script:Sha1 } })
    Assert-Equal 'up-to-date' $r.Polls[1].Result.Outcome
    Assert-Equal 'closed' $r.Issues[0].state
    Assert-Match $r.Issues[0].closeComment 'running version'
}

Test-Case 'alert issues: a problem that comes back after it was closed opens a new issue' {
    $r = Invoke-AlertPolls -State (Get-AlertOldState) -Steps @(
        @{ At = (Get-AlertAt 0); Machine = @{ BadAttest = @('api') } },
        @{ At = (Get-AlertAt 2) },
        @{ At = (Get-AlertAt 4); Machine = @{ BadAttest = @('api'); Sha = $script:AlertSha3; Up = $script:Sha2; Graph = @{ $script:Sha1 = @(); $script:Sha2 = @($script:Sha1); $script:AlertSha3 = @($script:Sha1, $script:Sha2) } } })
    Assert-Equal 2 $r.Issues.Count
    Assert-Equal 'closed' $r.Issues[0].state
    Assert-Equal 'open' $r.Issues[1].state
}

# ---- transient problems wait ---------------------------------------------------------------------------------------------

Test-Case 'alert issues: Docker being unreachable files nothing until it has lasted 10 polls in a row' {
    $steps = 0..9 | ForEach-Object { @{ At = (Get-AlertAt (2 * $_)); Machine = @{ PullFails = $true } } }
    $r = Invoke-AlertPolls -State (Get-AlertOldState) -Steps $steps
    foreach ($i in 0..8) { Assert-Equal 0 (Get-AlertIssueCalls $r.Polls[$i]).Count "poll $($i + 1) files nothing" }
    Assert-Equal 3 $r.Polls[9].Result.ExitCode
    Assert-Equal 1 $r.Issues.Count 'the 10th poll files it'
    Assert-Equal 'alert,type:infra,needs:cory,p2' ($r.Issues[0].labels -join ',') 'p2: the deployer resumes by itself'
    Assert-Match $r.Issues[0].body 'cm-alert:docker-unavailable'
    foreach ($i in 0..9) { Assert-Equal 1 (Get-AlertPings $r.Polls[$i]).Count "the ping and the log line are immediate on poll $($i + 1)" }
}

Test-Case 'alert issues: a poll that works resets the wait for a transient problem' {
    $steps = @(0..4 | ForEach-Object { @{ At = (Get-AlertAt (2 * $_)); Machine = @{ PullFails = $true } } }) +
    @(@{ At = (Get-AlertAt 10); Machine = @{ Sha = $script:Sha1 } }) +
    @(6..10 | ForEach-Object { @{ At = (Get-AlertAt (2 * $_)); Machine = @{ PullFails = $true } } })
    $r = Invoke-AlertPolls -State (Get-AlertOldState) -Steps $steps
    Assert-Equal 'up-to-date' $r.Polls[5].Result.Outcome
    Assert-Equal 0 $r.Issues.Count '5 failures, a good poll, 5 failures: never 10 in a row'
}

Test-Case 'alert issues: a different problem in between also restarts the wait for a transient one' {
    $moving = @{ MainMovesTo = $script:AlertSha3 }
    $steps = @(0..4 | ForEach-Object { @{ At = (Get-AlertAt (2 * $_)); Machine = $moving } }) +
    @(@{ At = (Get-AlertAt 10); Machine = @{ BadAttest = @('api') } }) +
    @(6..10 | ForEach-Object { @{ At = (Get-AlertAt (2 * $_)); Machine = $moving } })
    $r = Invoke-AlertPolls -State (Get-AlertOldState) -Steps $steps
    Assert-Equal 'refused' $r.Polls[5].Result.Outcome
    Assert-Equal 1 $r.Issues.Count 'only the attestation problem, which is not transient'
    Assert-Match $r.Issues[0].body 'cm-alert:attestation-failed'
}

Test-Case 'alert issues: Docker coming back closes the issue' {
    $steps = @(0..9 | ForEach-Object { @{ At = (Get-AlertAt (2 * $_)); Machine = @{ PullFails = $true } } }) + @(@{ At = (Get-AlertAt 20); Machine = @{ Sha = $script:Sha1 } })
    $r = Invoke-AlertPolls -State (Get-AlertOldState) -Steps $steps
    Assert-Equal 1 $r.Issues.Count
    Assert-Equal 'closed' $r.Issues[0].state
    Assert-Match $r.Issues[0].closeComment 'Docker and GHCR answered again'
}

Test-Case 'alert issues: the main tag moving during the checks files nothing until it persists for 10 polls' {
    $steps = 0..9 | ForEach-Object { @{ At = (Get-AlertAt (2 * $_)); Machine = @{ MainMovesTo = $script:AlertSha3 } } }
    $r = Invoke-AlertPolls -State (Get-AlertOldState) -Steps $steps
    Assert-Equal 'refused' $r.Polls[0].Result.Outcome
    foreach ($i in 0..8) { Assert-Equal 0 (Get-AlertIssueCalls $r.Polls[$i]).Count "poll $($i + 1) files nothing" }
    Assert-Equal 1 $r.Issues.Count
    Assert-Match $r.Issues[0].body 'cm-alert:main-tag-moving'
    Assert-Equal 'alert,type:infra,needs:cory,p2' ($r.Issues[0].labels -join ',')
}

Test-Case 'alert issues: an unexpected crash is reported as an agent task only when it repeats 3 polls in a row' {
    $steps = 0..2 | ForEach-Object { @{ At = (Get-AlertAt (2 * $_)); Machine = @{ PullThrows = $true } } }
    $r = Invoke-AlertPolls -State (Get-AlertOldState) -Steps $steps
    Assert-Equal 'crashed' $r.Polls[0].Result.Outcome
    Assert-Equal 0 (Get-AlertWrites $r.Polls[0]).Count
    Assert-Equal 0 (Get-AlertWrites $r.Polls[1]).Count
    Assert-Equal 1 $r.Issues.Count
    Assert-Equal $script:AgentLabels ($r.Issues[0].labels -join ',')
    Assert-Match $r.Issues[0].body 'cm-alert:deployer-crashed'
}

# ---- the switch, and a gh that does not work -----------------------------------------------------------------------------

Test-Case 'alert issues: CM_ALERT_ISSUES=0 turns issue filing off and leaves the log line and the ping alone' {
    foreach ($off in '0', 'false', 'off', 'No') {
        $r = Invoke-AlertPolls -State (Get-AlertOldState) -ExtraEnv @("CM_ALERT_ISSUES=$off") -Steps @(@{ At = $script:AlertT0; Machine = @{ BadAttest = @('api'); BackupHealth = 'unhealthy' } })
        Assert-Equal 4 $r.Polls[0].Result.ExitCode "CM_ALERT_ISSUES=$off"
        Assert-Equal 0 (Get-AlertIssueCalls $r.Polls[0]).Count "CM_ALERT_ISSUES=$off makes no gh issue call"
        Assert-Equal 0 $r.Issues.Count
        Assert-Equal 1 (Get-AlertPings $r.Polls[0]).Count 'the ping is still sent'
        Assert-Match $r.Log 'ALERT deploy refused'
        Assert-False ($r.StateText -match 'alerts') 'nothing is remembered'
    }
}

Test-Case 'alert issues: CM_ALERT_ISSUES=1 or unset leaves it on' {
    $r = Invoke-AlertPolls -State (Get-AlertOldState) -ExtraEnv @('CM_ALERT_ISSUES=1') -Steps @(@{ At = $script:AlertT0; Machine = @{ BadAttest = @('api') } })
    Assert-Equal 1 $r.Issues.Count
    Assert-True (Test-CmAlertIssuesEnabled -EnvMap @{} -ProcessValue '')
    Assert-False (Test-CmAlertIssuesEnabled -EnvMap @{} -ProcessValue '0') 'the process environment works too'
}

Test-Case 'alert issues: a gh that fails never changes the exit code or stops the deploy' {
    $r = Invoke-AlertPolls -State (Get-AlertOldState) -Steps @(@{ At = $script:AlertT0; Machine = @{ BadAttest = @('api'); GhFails = $true } })
    Assert-Equal 4 $r.Polls[0].Result.ExitCode
    Assert-Equal 1 (Get-AlertPings $r.Polls[0]).Count
    Assert-Match $r.Log 'WARN .*alert issue'
    Assert-Match $r.Log 'ALERT deploy refused'
    # A deploy that succeeds while gh is down still succeeds.
    $ok = Invoke-AlertPolls -State (Get-AlertOldState) -Steps @(
        @{ At = (Get-AlertAt 0); Machine = @{ BadAttest = @('api') } },
        @{ At = (Get-AlertAt 4); Machine = @{ GhFails = $true } })
    Assert-Equal 'deployed' $ok.Polls[1].Result.Outcome
    Assert-Equal 0 $ok.Polls[1].Result.ExitCode
    Assert-Equal $script:Sha2 $ok.State.current.sha
    Assert-Equal 'open' $ok.Issues[0].state 'the close did not happen'
    Assert-Match $ok.Log 'WARN .*alert issue'
}

Test-Case 'alert issues: a gh that throws never changes the exit code' {
    $r = Invoke-AlertPolls -State (Get-AlertOldState) -Steps @(@{ At = $script:AlertT0; Machine = @{ BadAttest = @('api'); GhThrows = $true } })
    Assert-Equal 4 $r.Polls[0].Result.ExitCode
    Assert-Match $r.Log 'WARN .*alert issue'
}

Test-Case 'alert issues: after a gh failure the deployer waits 15 minutes before asking again, then closes the issue' {
    $r = Invoke-AlertPolls -State (Get-AlertOldState) -Steps @(
        @{ At = (Get-AlertAt 0); Machine = @{ BadAttest = @('api') } },
        @{ At = (Get-AlertAt 4); Machine = @{ GhFails = $true } },
        @{ At = (Get-AlertAt 6); Machine = @{ Sha = $script:Sha2; Up = $script:Sha2 } },
        @{ At = (Get-AlertAt 20); Machine = @{ Sha = $script:Sha2; Up = $script:Sha2 } })
    Assert-Equal 'up-to-date' $r.Polls[2].Result.Outcome
    Assert-Equal 0 (Get-AlertWrites $r.Polls[2]).Count 'inside the 15 minutes nothing is tried'
    Assert-Equal 1 (Get-AlertGhCalls $r.Polls[3] 'close').Count
    Assert-Equal 'closed' $r.Issues[0].state
}

Test-Case 'alert issues: a one-off failure that could not be filed because gh was down is filed once gh works, with the time it happened' {
    $r = Invoke-AlertPolls -State (Get-AlertOldState) -Steps @(
        @{ At = (Get-AlertAt 0); Machine = @{ UnhealthyTags = @($script:Sha2); GhFails = $true } },
        @{ At = (Get-AlertAt 6) },
        @{ At = (Get-AlertAt 16) })
    Assert-Equal 'rolled-back' $r.Polls[0].Result.Outcome
    Assert-Equal 'known-bad' $r.Polls[1].Result.Outcome
    Assert-Equal 0 (Get-AlertWrites $r.Polls[1]).Count 'the 15 minutes are not over'
    Assert-Equal 1 $r.Issues.Count
    Assert-Match $r.Issues[0].body 'cm-alert:smoke-failed'
    Assert-Match $r.Issues[0].body '\| When \| 2026-10-31 12:00:00 UTC' 'the time it happened, not the time it was filed'
    Assert-False ($r.StateText -match 'pending') 'nothing is left pending'
}

Test-Case 'alert issues: a failure that could not be filed is forgotten when the problem clears first' {
    $graph = @{ $script:Sha1 = @(); $script:Sha2 = @($script:Sha1); $script:AlertSha3 = @($script:Sha1, $script:Sha2) }
    $r = Invoke-AlertPolls -State (Get-AlertOldState) -Steps @(
        @{ At = (Get-AlertAt 0); Machine = @{ UnhealthyTags = @($script:Sha2); GhFails = $true } },
        @{ At = (Get-AlertAt 4); Machine = @{ Sha = $script:AlertSha3; Graph = $graph } },
        @{ At = (Get-AlertAt 40); Machine = @{ Sha = $script:AlertSha3; Up = $script:AlertSha3; Graph = $graph } })
    Assert-Equal 'deployed' $r.Polls[1].Result.Outcome
    Assert-Equal 0 $r.Issues.Count 'an issue for a problem that is already over would only be noise'
    Assert-False ($r.StateText -match 'pending')
}

Test-Case 'alert issues: a manual rollback never files an issue (someone is watching the console)' {
    $r = Invoke-AlertPolls -State @{ current = @{ sha = $script:Sha2; created = '2026-10-14T00:00:00.0000000Z' } } -Steps @(
        @{ At = $script:AlertT0; RollbackTo = $script:AlertSha3; Machine = @{ Sha = $script:AlertSha3 } })
    Assert-Equal 'rollback-refused' $r.Polls[0].Result.Outcome
    Assert-Equal 4 $r.Polls[0].Result.ExitCode
    Assert-Equal 0 (@($r.Polls[0].Calls | Where-Object { $_.Command -eq 'gh' -and $_.Display -like '*issue *' })).Count
    Assert-Equal 1 (Get-AlertPings $r.Polls[0]).Count 'the ping stays'
}

Test-Case 'alert issues: a dry run never touches GitHub' {
    $r = Invoke-AlertPolls -State (Get-AlertOldState) -Steps @(@{ At = $script:AlertT0; DryRun = $true; Machine = @{ BadAttest = @('api'); BackupHealth = 'unhealthy' } })
    Assert-Equal 'dry-run' $r.Polls[0].Result.Outcome
    Assert-Equal 0 (@($r.Polls[0].Calls | Where-Object { $_.Command -eq 'gh' -and $_.Display -like '*issue *' })).Count
}

Test-Case 'alert issues: a poll that did not run (another run holds the lock) neither raises nor clears' {
    $r = Invoke-AlertPolls -State (Get-AlertOldState) -Steps @(
        @{ At = (Get-AlertAt 0); Machine = @{ BadAttest = @('api') } },
        @{ At = (Get-AlertAt 2); LockHeldMinutes = 1 })
    Assert-Equal 'busy' $r.Polls[1].Result.Outcome
    Assert-Equal 0 $r.Polls[1].Calls.Count 'a busy poll makes no calls at all'
    Assert-Equal 'open' $r.Issues[0].state
}

# ---- state: kept in the deployer's state file, which stays intact --------------------------------------------------------

Test-Case 'alert issues: what the deployer remembers lives in its state file and leaves the rest of it alone' {
    $r = Invoke-AlertPolls -State (Get-AlertOldState) -Steps @(@{ At = $script:AlertT0; Machine = @{ BadAttest = @('api') } })
    Assert-Equal $script:Sha1 $r.State.current.sha
    Assert-Equal 101 $r.State.alerts.kinds['attestation-failed'].issue 'the issue number is remembered'
    Assert-Equal 1 $r.State.alerts.kinds['attestation-failed'].count
    Assert-Equal 1 $r.State.alerts.writes.count 'one write so far today'
    $core = Invoke-AlertPolls -State (Get-AlertOldState) -Steps @(@{ At = $script:AlertT0; Machine = @{ UnhealthyTags = @($script:Sha2) } })
    Assert-Equal $script:Sha2 $core.State.failedSha 'the deploy bookkeeping is still written'
    Assert-Equal $script:Sha1 $core.State.current.sha
}

Test-Case 'alert issues: an unreadable state file is never overwritten, and the issue is still filed once' {
    $r = Invoke-AlertPolls -StateText 'not json {' -Steps @(
        @{ At = (Get-AlertAt 0) }, @{ At = (Get-AlertAt 2) }, @{ At = (Get-AlertAt 61) })
    Assert-Equal 'refused' $r.Polls[0].Result.Outcome
    Assert-Equal 'not json {' $r.StateText 'the state file is untouched'
    Assert-Equal 1 $r.Issues.Count
    Assert-Match $r.Issues[0].body 'cm-alert:deployer-setup-refused'
    Assert-Equal 0 (Get-AlertWrites $r.Polls[1]).Count 'the issue was updated minutes ago'
    Assert-Equal 1 $r.Issues[0].comments.Count 'an hour later it comments'
}

# ---- nothing secret leaves the PC ----------------------------------------------------------------------------------------

Test-Case 'alert issues: the body and the comments hide every secret, token, host name and home folder' {
    $setup = {
        param($root)
        New-Item -ItemType Directory -Force -Path (Join-Path $root 'logs') | Out-Null
        Set-Content -LiteralPath (Join-Path $root 'logs/deployer.log') -Value @(
            'leak 1: the password test-only-password and the url https://hc.example/test-uuid',
            'leak 2: Authorization: Bearer abcdefghijklmnopqrstuvwxyz0123456789',
            'leak 3: token ghp_0123456789abcdefghijklmnopqrstuvwxyz and github_pat_11ABCDEFG0123456789_abcdefghijklmnop',
            'leak 4: opening https://owners-pc.tail1234.ts.net/health for C:\Users\ownername\source\x or me@example.com',
            'leak 5: api key AIza-fake-google-key-123456 and client secret test-secret and CM_TMDB_API_KEY=eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abcdefghij')
    }
    $extraEnv = @('CM_GOOGLE_BOOKS_API_KEY=AIza-fake-google-key-123456', 'CM_PUBLIC_ORIGIN=https://owners-pc.tail1234.ts.net')
    $r = Invoke-AlertPolls -State (Get-AlertOldState) -Setup $setup -ExtraEnv $extraEnv -Steps @(
        @{ At = (Get-AlertAt 0); Machine = @{ BadAttest = @('api') } },
        @{ At = (Get-AlertAt 61); Machine = @{ BadAttest = @('api') } })
    $everything = @($r.Issues[0].title, $r.Issues[0].body) + @($r.Issues[0].comments)
    $text = $everything -join "`n"
    Assert-Match $text 'leak 1' 'the log tail is there'
    foreach ($secret in 'test-only-password', 'test-uuid', 'hc.example', 'abcdefghijklmnopqrstuvwxyz0123456789', 'ghp_0123', 'github_pat_11', 'owners-pc', 'tail1234', 'ownername', 'me@example.com', 'AIza-fake', 'test-secret', 'eyJhbGci') {
        Assert-False ($text -like "*$secret*") "'$secret' must not be in anything posted to GitHub"
    }
}

Test-Case 'alert issues: the reason of a failure is hidden too' {
    $root = New-TestRoot
    try {
        New-FakeMachine @{} | Out-Null
        Initialize-Cm -Apply $true
        $ctx = New-TestContext -Root $root
        $envMap = Initialize-CmRun -Ctx $ctx
        $result = New-CmResult 'refused' 4 'the stack said password=hunter2-very-secret and test-only-password for owner@example.com' -Kind 'attestation-failed'
        Add-CmAlertIssue -Ctx $ctx -Result $result -EnvMap $envMap 6>$null
        $issue = $global:CmMachine.Issues[0]
        foreach ($secret in 'hunter2', 'test-only-password', 'owner@example.com') { Assert-False ($issue.body -like "*$secret*") "'$secret' hidden" }
        Assert-Match $issue.body '\*\*\*'
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'alert issues: Hide-CmAlertText masks the shapes of tokens and keeps ordinary text' {
    $envMap = @{ POSTGRES_PASSWORD = 'pg-secret-value'; CM_PUBLIC_ORIGIN = 'https://owners-pc.tail1234.ts.net'; CM_API_PORT = '5480'; CM_BACKUP_DIR = 'F:/Backups/CollectionMaxxing'; CM_AUTH0_DOMAIN = 'tenant-abc.us.auth0.com' }
    $in = 'pull ghcr.io/coryfinnegan/collectionmaxxing-api:main failed for pg-secret-value at owners-pc.tail1234.ts.net port 5480 domain tenant-abc.us.auth0.com in F:/Backups/CollectionMaxxing'
    $out = Hide-CmAlertText -Text $in -EnvMap $envMap
    Assert-False ($out -match 'pg-secret-value|owners-pc|tenant-abc') $out
    Assert-Match $out 'ghcr\.io/coryfinnegan/collectionmaxxing-api:main' 'public names stay'
    Assert-Match $out 'port 5480' 'a port is not a secret'
    Assert-Equal 'a tailnet host: pc.tailnet.ts.net' (Hide-CmAlertText -Text 'a tailnet host: laptop.tail9.ts.net')
    Assert-Equal 'C:\Users\<user>\x' (Hide-CmAlertText -Text 'C:\Users\someone\x')
}

# ---- backups: a missed nightly backup reaches the owner without a deploy ---------------------------------------------------

Test-Case 'alert issues: an unhealthy backup container opens a needs:cory issue on a quiet poll' {
    $r = Invoke-AlertPolls -State @{ current = @{ sha = $script:Sha2 } } -Steps @(@{ At = $script:AlertT0; Machine = @{ BackupHealth = 'unhealthy' } })
    Assert-Equal 'up-to-date' $r.Polls[0].Result.Outcome
    Assert-Equal 0 $r.Polls[0].Result.ExitCode 'a quiet poll stays quiet'
    Assert-Equal 1 $r.Issues.Count
    Assert-Equal $script:OwnerLabels ($r.Issues[0].labels -join ',')
    Assert-Match $r.Issues[0].body 'cm-alert:backup-unhealthy'
    Assert-Match $r.Issues[0].body 'newest successful backup is 31 h old' 'what the health check itself said'
    Assert-Equal 0 (Get-AlertPings $r.Polls[0]).Count 'the heartbeat check is the backup''s own, not the deployer''s'
}

Test-Case 'alert issues: a healthy backup closes the backup issue, and an unhealthy one waits an hour to comment' {
    $up = @{ current = @{ sha = $script:Sha2 } }
    $r = Invoke-AlertPolls -State $up -Steps @(
        @{ At = (Get-AlertAt 0); Machine = @{ BackupHealth = 'unhealthy' } },
        @{ At = (Get-AlertAt 2); Machine = @{ BackupHealth = 'unhealthy' } },
        @{ At = (Get-AlertAt 61); Machine = @{ BackupHealth = 'unhealthy' } },
        @{ At = (Get-AlertAt 64); Machine = @{ BackupHealth = 'healthy' } })
    Assert-Equal 1 $r.Issues.Count
    Assert-Equal 0 (Get-AlertWrites $r.Polls[1]).Count
    Assert-Equal 1 $r.Issues[0].comments.Count
    Assert-Match $r.Issues[0].comments[0] 'Again at 2026-10-31 13:01 UTC, 3 times today'
    Assert-Equal 'closed' $r.Issues[0].state
    Assert-Match $r.Issues[0].closeComment 'backup container reports healthy'
}

Test-Case 'alert issues: a backup that is still starting, or whose health cannot be read, raises and clears nothing' {
    foreach ($case in @(@{ BackupHealth = 'starting' }, @{ BackupHealth = 'none' }, @{ BackupPsFails = $true }, @{ BackupInspectFails = $true })) {
        $r = Invoke-AlertPolls -State @{ current = @{ sha = $script:Sha2 } } -Steps @(@{ At = $script:AlertT0; Machine = $case })
        Assert-Equal 0 $r.Issues.Count ($case.Keys -join ',')
    }
    $open = Invoke-AlertPolls -State @{ current = @{ sha = $script:Sha2 } } -Steps @(
        @{ At = (Get-AlertAt 0); Machine = @{ BackupHealth = 'unhealthy' } },
        @{ At = (Get-AlertAt 2); Machine = @{ BackupHealth = 'starting' } })
    Assert-Equal 'open' $open.Issues[0].state 'starting does not clear it'
}

Test-Case 'alert issues: a backup container that is not running counts as unhealthy' {
    $r = Invoke-AlertPolls -State @{ current = @{ sha = $script:Sha2 } } -Steps @(@{ At = $script:AlertT0; Machine = @{ BackupContainerMissing = $true } })
    Assert-Equal 1 $r.Issues.Count
    Assert-Match $r.Issues[0].body 'backup container is not running'
}

Test-Case 'alert issues: the backup is also looked at on a poll that is known-bad and right after a deploy' {
    $known = Invoke-AlertPolls -State @{ current = @{ sha = $script:Sha1 }; failedSha = $script:Sha2 } -Steps @(@{ At = $script:AlertT0; Machine = @{ BackupHealth = 'unhealthy' } })
    Assert-Equal 'known-bad' $known.Polls[0].Result.Outcome
    Assert-Equal 1 $known.Issues.Count
    $deployed = Invoke-AlertPolls -State (Get-AlertOldState) -Steps @(@{ At = $script:AlertT0; Machine = @{ BackupHealth = 'unhealthy' } })
    Assert-Equal 'deployed' $deployed.Polls[0].Result.Outcome
    Assert-Equal 1 $deployed.Issues.Count
}

Test-Case 'alert issues: a quiet poll with nothing wrong makes no gh call at all' {
    $r = Invoke-AlertPolls -State @{ current = @{ sha = $script:Sha2 } } -Steps @(@{ At = (Get-AlertAt 0) }, @{ At = (Get-AlertAt 2) })
    foreach ($poll in $r.Polls) { Assert-Equal 0 (Get-AlertIssueCalls $poll).Count }
    Assert-Equal 'up-to-date' $r.Polls[1].Result.Outcome
    Assert-False ($r.StateText -match 'alerts') 'and nothing is written to the state file'
}

Test-Case 'alert issues: the backup check reads the container, it changes nothing' {
    $r = Invoke-AlertPolls -State @{ current = @{ sha = $script:Sha2 } } -Steps @(@{ At = $script:AlertT0; Machine = @{ BackupHealth = 'unhealthy' } })
    $docker = @($r.Polls[0].Calls | Where-Object { $_.Command -eq 'docker' -and ($_.Display -like '* ps -q backup' -or $_.Display -like '*State.Health*') })
    Assert-Equal 2 $docker.Count
    Assert-Equal 0 (@($docker | Where-Object { $_.Mutating })).Count
}

# ---- the daily cap -------------------------------------------------------------------------------------------------------

Test-Case 'alert issues: no more than 10 issue writes a day, and the cap resets with the day' {
    $root = New-TestRoot
    try {
        New-FakeMachine @{} | Out-Null
        Initialize-Cm -Apply $true
        $ctx = New-TestContext -Root $root
        $envMap = Initialize-CmRun -Ctx $ctx
        $kinds = @((Get-CmAlertKinds).GetEnumerator() | Where-Object { $_.Value.After -le 1 } | ForEach-Object { $_.Key })
        Assert-Equal 10 $kinds.Count 'ten kinds alert at once, so ten creates fill the day'
        $global:CmNow = $script:AlertT0
        foreach ($kind in $kinds) { Add-CmAlertIssue -Ctx $ctx -Result (New-CmResult 'refused' 4 'x' -Kind $kind) -EnvMap $envMap 6>$null }
        $issues = $global:CmMachine.Issues
        Assert-Equal 10 $issues.Count
        $global:CmNow = Get-AlertAt 61
        Add-CmAlertIssue -Ctx $ctx -Result (New-CmResult 'refused' 4 'x' -Kind $kinds[0]) -EnvMap $envMap 6>$null
        Assert-Equal 0 $issues[0].comments.Count 'the 11th write is held back'
        $log = Get-Content -LiteralPath $ctx.LogFile -Raw
        Assert-Match $log 'WARN .*10 issue writes'
        Add-CmAlertIssue -Ctx $ctx -Result (New-CmResult 'refused' 4 'x' -Kind $kinds[0]) -EnvMap $envMap 6>$null
        Assert-Equal 1 ([regex]::Matches((Get-Content -LiteralPath $ctx.LogFile -Raw), '10 issue writes')).Count 'and the warning is logged once a day'
        $global:CmNow = Get-AlertAt 780   # 01:00 the next day (UTC)
        Add-CmAlertIssue -Ctx $ctx -Result (New-CmResult 'refused' 4 'x' -Kind $kinds[0]) -EnvMap $envMap 6>$null
        Assert-Equal 1 $issues[0].comments.Count 'the next day it goes through'
    }
    finally { $global:CmNow = $null; Remove-TestRoot $root }
}

# ---- the kinds -----------------------------------------------------------------------------------------------------------

Test-Case 'alert issues: every outcome maps to a kind, and manual rollbacks to none' {
    $cases = @(
        @('rolled-back', 'the smoke test failed for abc', 'smoke-failed'), @('rolled-back', 'the service check failed (backup is restarting)', 'deploy-rolled-back'),
        @('failed', 'the smoke test failed for abc and there is no previous good version', 'smoke-failed'), @('failed', 'starting the containers failed', 'deploy-rolled-back'),
        @('migrate-failed', '', 'migrate-failed'), @('dump-failed', '', 'pre-migrate-dump-failed'), @('config-error', '', 'config-guard-refused'),
        @('crashed', '', 'deployer-crashed'), @('stale-lock', '', 'deployer-stuck'), @('no-docker', '', 'docker-unavailable'),
        @('downgrade-refused', '', 'downgrade-refused'), @('refused', '', 'attestation-failed'),
        @('rollback-refused', '', ''), @('rollback-failed', '', ''), @('deployed', '', ''), @('up-to-date', '', ''))
    foreach ($c in $cases) {
        Assert-Equal $c[2] (Get-CmAlertKind -Result (New-CmResult $c[0] 1 $c[1])) "$($c[0]) / $($c[1])"
    }
    Assert-Equal 'main-tag-moving' (Get-CmAlertKind -Result (New-CmResult 'refused' 4 'x' -Kind 'main-tag-moving')) 'an explicit kind wins'
}

Test-Case 'alert issues: the labels of every kind follow the rules' {
    $kinds = Get-CmAlertKinds
    $agent = 'deploy-rolled-back', 'smoke-failed', 'migrate-failed', 'config-guard-refused', 'deployer-crashed'
    $owner = 'attestation-failed', 'downgrade-refused', 'deployer-setup-refused', 'pre-migrate-dump-failed', 'deployer-stuck', 'backup-unhealthy', 'docker-unavailable', 'main-tag-moving'
    Assert-Equal ((@($agent) + @($owner) | Sort-Object) -join ',') (@($kinds.Keys | Sort-Object) -join ',') 'exactly these kinds'
    foreach ($k in $kinds.Keys) {
        $labels = @($kinds[$k].Labels)
        Assert-True ($labels -contains 'alert' -and $labels -contains 'type:infra') "$k has alert and type:infra"
        if ($agent -contains $k) {
            Assert-Equal $script:AgentLabels ($labels -join ',') $k
        }
        else {
            Assert-True ($labels -contains 'needs:cory') "$k needs the owner"
            Assert-False ($labels -contains 'agent-task') "$k must never be handed to an agent"
            $p = if ($k -in 'docker-unavailable', 'main-tag-moving') { 'p2' } else { 'p1' }
            Assert-True ($labels -contains $p) "$k is $p"
        }
        Assert-True ($kinds[$k].Summary -and $kinds[$k].Todo -and $kinds[$k].Title -and $kinds[$k].Cleared) "$k has its texts"
    }
    foreach ($k in 'docker-unavailable', 'main-tag-moving') { Assert-Equal 10 $kinds[$k].After $k }
    foreach ($k in $kinds.Keys) { if ($k -notin 'docker-unavailable', 'main-tag-moving', 'deployer-crashed') { Assert-Equal 1 $kinds[$k].After $k } }
}

Test-Case 'alert issues: the README documents every kind, its labels and the switch, and .env.example has the switch' {
    $readme = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../README.md') -Raw
    foreach ($k in (Get-CmAlertKinds).Keys) { Assert-Match $readme ([regex]::Escape($k)) "README names $k" }
    Assert-Match $readme 'CM_ALERT_ISSUES'
    Assert-Match $readme 'needs:cory'
    Assert-Match $readme 'agent-task'
    $example = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../.env.example') -Raw
    Assert-Match $example 'CM_ALERT_ISSUES'
}

Test-Case 'alert issues: the deployer never prints its own gh calls with a secret on the command line' {
    $r = Invoke-AlertPolls -State (Get-AlertOldState) -Steps @(@{ At = $script:AlertT0; Machine = @{ BadAttest = @('api') } })
    $create = Get-AlertGhCalls $r.Polls[0] 'create'
    Assert-Match $create[0].Display '--body-file -' 'the body goes through standard input'
    Assert-False ($create[0].Display -match 'test-only-password')
    Assert-Match $create[0].Display '--label alert --label type:infra'
    Assert-Match $create[0].Display '--repo coryfinnegan/CollectionMaxxing'
}
