# Tests for the eval-due check (deploy/agent/Cm.EvalDue.ps1): the watcher opens, retitles and closes the one needs:eval-run issue
# that asks the owner for a phone eval run when a TestFlight build changed the phone's recognition code. Every test is a whole
# watcher pass (Invoke-CmAgentWatch) against a throwaway root, so it also proves the check leaves the agent pass alone.
# Nothing here calls the real gh: external commands go through Invoke-Cm ($global:CmRunner, a fake GitHub with workflow runs,
# jobs, compares and issues). Dot-sourced by Run-Tests.ps1, so the same file must pass on Linux (CI) and Windows.

$script:EvalAgentDir = (Resolve-Path (Join-Path $PSScriptRoot '../agent')).Path
$script:EvalSlug = 'coryfinnegan/CollectionMaxxing'
$script:EvalNow = [DateTimeOffset]::new(2026, 11, 1, 20, 0, 0, [TimeSpan]::FromHours(-6))
# The story every test tells a part of: build 131 uploaded 14:49Z, build 133 at 16:49Z, build 135 at 19:17Z (all UTC). The phone's
# first run started 15:30Z (so it ran build 131), its second 17:52Z (build 133). The times are written with other offsets on purpose.
$script:EvalRun1Start = '2026-11-01T09:30:00.1234567-06:00'
$script:EvalRun2Start = '2026-11-01T17:52:05.7726985+00:00'

# ---- the fake GitHub --------------------------------------------------------------------------------------------------------

function Get-EvalSha { param([int]$Number) (('{0:x2}' -f $Number) * 20) }

function New-EvalBuild {
    <#
      One workflow run of ios-build.yml on main. Files are what the commit changed; Build is what its `build` job concluded
      (skipped for a [skip ios-build] push). A schedule run has the sha of the commit before it and changed nothing.
    #>
    param([int]$Number, [string]$At, [string[]]$Files = @('app/app.config.ts'), [string]$Build = 'success', [string]$Event = 'push', [string]$Conclusion = 'success', [string]$Status = 'completed', [string]$Subject = '')
    @{ Number = $Number; At = $At; Files = $Files; Build = $Build; Event = $Event; Conclusion = $Conclusion; Status = $Status
        Subject = $(if ($Subject) { $Subject } else { "[Scan] Change number $Number (#$Number)" }) }
}

function New-EvalIssue {
    param([int]$Number, [string]$Title = 'An eval issue', [string]$Body = '', [string]$Created = '2026-11-01T16:55:00Z', [string]$Author = 'coryfinnegan', [string[]]$Labels = @('needs:eval-run', 'needs:cory'))
    [pscustomobject]@{ number = $Number; title = $Title; body = $Body; labels = $Labels; state = 'open'; createdAt = $Created; author = $Author
        comments = [System.Collections.Generic.List[string]]::new(); closeComment = '' }
}

function New-EvalMarkedIssue {
    param([int]$Number, [int]$Build, [string]$Created = '2026-11-01T16:55:00Z')
    New-EvalIssue -Number $Number -Title "[Owner] Run evals on TestFlight build $Build" -Created $Created -Body "Text for an assistant.`n`n<!-- eval-due build=$Build sha=$(Get-EvalSha $Build) -->"
}

function New-EvalMachine {
    <#
      Installs the fake GitHub. Options (all optional):
        Builds          New-EvalBuild list (what `gh run list` shows, newest first or not)
        Issues          the repository's issues (New-EvalIssue); pass one list to every pass of a test so the issues carry over
        ThrowOn         a regex: a gh call whose arguments match it throws
        RunListExit, ApiExit, IssueListExit, WriteExit    exit codes of `gh run list`, `gh api`, `gh issue list` (the eval label), and every gh call that changes an issue
    #>
    param([hashtable]$Opt = @{})
    $m = @{ Now = $script:EvalNow; Builds = @(); Issues = [System.Collections.Generic.List[object]]::new(); RunListExit = 0; ApiExit = 0; IssueListExit = 0; WriteExit = 0 }
    foreach ($k in $Opt.Keys) { $m[$k] = $Opt[$k] }
    $global:CmCalls.Clear()
    $global:EvalMachine = $m
    $global:CmAgentNow = { $global:EvalMachine.Now }
    $global:CmNow = $m.Now.UtcDateTime
    $global:CmRunner = {
        param($Command, $Arguments, $Env, $Mutating, $InputText, $Timeout)
        $m = $global:EvalMachine
        $display = Format-CmCommand -Command $Command -Arguments $Arguments -Env $Env
        $global:CmCalls.Add([pscustomobject]@{ Command = $Command; Arguments = @($Arguments); Env = $Env; Mutating = $Mutating; Display = $display; InputText = $InputText })
        $ok = { param($text = '') [pscustomobject]@{ ExitCode = 0; Output = @($text) } }
        $failed = { param($code) [pscustomobject]@{ ExitCode = $code; Output = @("fake failure $code") } }
        $line = $Arguments -join ' '
        if ($m.ThrowOn -and $line -match $m.ThrowOn) { throw 'gh exploded' }
        $json = { param($value) ConvertTo-Json -InputObject $value -Depth 8 -Compress }
        # The builds that exist in git history, as the compare and commit calls see them (a scheduled run is no commit).
        $commits = @($m.Builds | Where-Object { $_.Event -ne 'schedule' } | Sort-Object { $_.Number })
        $numberOf = { param($sha) @($m.Builds | Where-Object { (Get-EvalSha $_.Number) -eq $sha -and $_.Event -ne 'schedule' })[0].Number }
        if ($Command -ne 'gh') { return & $ok }
        if ($line -match '^run list ') {
            if ($m.RunListExit) { return & $failed $m.RunListExit }
            $rows = @($m.Builds | Sort-Object { - $_.Number } | ForEach-Object {
                    @{ number = $_.Number; headSha = (Get-EvalSha $(if ($_.Event -eq 'schedule') { $_.Number - 1 } else { $_.Number })); updatedAt = $_.At; status = $_.Status; conclusion = $_.Conclusion; databaseId = (5000 + $_.Number); event = $_.Event }
                })
            return & $ok (& $json $rows)
        }
        if ($line -match '^api ') {
            if ($m.ApiExit) { return & $failed $m.ApiExit }
            if ($line -match '/actions/runs/(\d+)/jobs') {
                $run = @($m.Builds | Where-Object { 5000 + $_.Number -eq [int]$Matches[1] })[0]
                return & $ok (& $json @(@{ name = 'build'; conclusion = $run.Build }, @{ name = 'release'; conclusion = 'skipped' }))
            }
            if ($line -match '/compare/([0-9a-f]{40})\.\.\.([0-9a-f]{40})') {
                $from = & $numberOf $Matches[1]; $to = & $numberOf $Matches[2]
                $between = @($commits | Where-Object { $_.Number -gt $from -and $_.Number -le $to })
                return & $ok (& $json @{ files = @($between | ForEach-Object { $_.Files } | Select-Object -Unique); commits = @($between | ForEach-Object { Get-EvalSha $_.Number }) })
            }
            if ($line -match '/commits\?sha=([0-9a-f]{40})&path=(\S+)&per_page') {
                $head = & $numberOf $Matches[1]; $prefix = $Matches[2] + '/'
                $rows = @($commits | Where-Object { $_.Number -le $head -and @($_.Files | Where-Object { $_.StartsWith($prefix) }).Count -gt 0 } | Sort-Object { - $_.Number } | ForEach-Object { @{ sha = (Get-EvalSha $_.Number); subject = $_.Subject } })
                return & $ok (& $json $rows)
            }
            return & $ok
        }
        if ($line -match '^issue list .*--label agent-task') { return & $ok '[]' }   # the agent's own queue: empty
        if ($line -match '^issue list .*--label needs:eval-run') {
            if ($m.IssueListExit) { return & $failed $m.IssueListExit }
            $rows = @($m.Issues | Where-Object { $_.state -eq 'open' } | ForEach-Object { @{ number = $_.number; title = $_.title; body = $_.body; createdAt = $_.createdAt; author = @{ login = $_.author } } })
            return & $ok (& $json $rows)
        }
        if ($line -match '^issue (create|edit|comment|close)') {
            if ($m.WriteExit) { return & $failed $m.WriteExit }
            $at = { param($flag) $i = [array]::IndexOf($Arguments, $flag); if ($i -ge 0) { $Arguments[$i + 1] } }
            if ($Matches[1] -eq 'create') {
                $labels = @(for ($i = 0; $i -lt $Arguments.Count - 1; $i++) { if ($Arguments[$i] -eq '--label') { $Arguments[$i + 1] } })
                $number = 200 + $m.Issues.Count + 1
                $m.Issues.Add((New-EvalIssue -Number $number -Title (& $at '--title') -Body "$InputText" -Labels $labels -Created $m.Now.UtcDateTime.ToString('o')))
                return & $ok "https://github.com/$($script:EvalSlug)/issues/$number"
            }
            $issue = @($m.Issues | Where-Object { $_.number -eq [int]$Arguments[2] })[0]
            if (-not $issue) { return & $failed 1 }
            switch ($Matches[1]) {
                'edit' { $issue.title = & $at '--title'; $issue.body = "$InputText" }
                'comment' { $issue.comments.Add("$InputText") }
                'close' { $issue.state = 'closed'; $issue.closeComment = & $at '--comment' }
            }
            return & $ok
        }
        & $ok
    }
    $m
}

# ---- the PC's evals folder --------------------------------------------------------------------------------------------------

function Set-EvalResults {
    <#
      Writes files the server leaves in <root>/evals/results. Header is open-run/run.json (state Open, Finishing or Done, openedAt),
      Kept is latest-inputs.json (runId, readAt: the last SCORED phone run), Score is latest.json. *Text writes that text instead,
      for a file that is not JSON. Nothing is written for a null.
    #>
    param([string]$Root, $Header = $null, $Kept = $null, $Score = $null, [string]$HeaderText = '', [string]$KeptText = '')
    $results = Join-Path $Root 'evals/results'
    New-Item -ItemType Directory -Force -Path (Join-Path $results 'open-run') | Out-Null
    $write = { param($name, $value, $text) if ($text) { Set-Content -LiteralPath (Join-Path $results $name) -Value $text } elseif ($null -ne $value) { $value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $results $name) } }
    & $write 'open-run/run.json' $Header $HeaderText
    & $write 'latest-inputs.json' $Kept $KeptText
    & $write 'latest.json' $Score ''
}

function New-EvalPhoneRun {
    <# The files of one phone run that was scored: its run.json (Done) and its kept readings, and the score of it. -State Open is a run still going. #>
    param([string]$Id = 'phone-run-2', [string]$Start = $script:EvalRun2Start, [string]$State = 'Done', [int]$Failing = 3, [string]$Verdict = 'fail', [int]$PhotosRun = 32, [int]$PhotosInSet = 32)
    $measures = @(1..$Failing | ForEach-Object { @{ name = "m$_"; status = 'fail' } }) + @(@{ name = 'ok1'; status = 'pass' }, @{ name = 'ok2'; status = 'pass' })
    @{
        Header = @{ runId = $Id; openedAt = $Start; state = $State; partial = $false; finishedAt = $null; photos = @(@{ id = 'shelf-01'; set = 'shelf' }) }
        # readAt is also written inside a photo, where it must not be read.
        Kept   = @{ runId = $Id; readAt = $Start; partial = $false; photos = @(@{ photoId = 'shelf-01'; results = @(@{ readAt = '1999-01-01T00:00:00Z' }) }) }
        # The PC scores the readings again on every new server build, so the score is of a later run that came from this one.
        Score  = @{ runId = 'regrounded-after'; regroundedFrom = @{ runId = $Id; readAt = $Start }; gate = @{ verdict = $Verdict; measures = $measures }; photosRun = $PhotosRun; photosInSet = $PhotosInSet }
    }
}

# ---- one watcher pass -------------------------------------------------------------------------------------------------------

function New-EvalRoot {
    <# A throwaway root with an evals folder (-NoEvals leaves it out: a PC that does no evals). #>
    param([switch]$NoEvals)
    $root = New-TestRoot
    if (-not $NoEvals) { New-Item -ItemType Directory -Force -Path (Join-Path $root 'evals/results') | Out-Null }
    $root
}

function Invoke-EvalPass {
    <# One whole watcher pass over $Root. Returns the pass's result, what it printed, the log file and the eval-due state. #>
    param([string]$Root, [switch]$DryRun, [string[]]$EnvLines = @())
    if ($EnvLines.Count -gt 0) { Add-Content -LiteralPath (Join-Path $Root '.env') -Value $EnvLines }
    Initialize-Cm -Apply (-not $DryRun)
    $ctx = New-CmAgentContext -Root $Root -Repo $script:EvalSlug -AgentDir $script:EvalAgentDir
    $all = @(Invoke-CmAgentWatch -Ctx $ctx 6>&1)
    $logPath = Join-Path $Root 'logs/agent-watcher.log'
    $statePath = Join-Path $Root 'state/eval-due.json'
    [pscustomobject]@{
        Result = ($all | Where-Object { $_ -isnot [System.Management.Automation.InformationRecord] } | Select-Object -Last 1)
        Screen = (($all | Where-Object { $_ -is [System.Management.Automation.InformationRecord] } | ForEach-Object { "$_" }) -join "`n")
        Log    = $(if (Test-Path -LiteralPath $logPath) { Get-Content -LiteralPath $logPath -Raw } else { '' })
        State  = $(if (Test-Path -LiteralPath $statePath) { Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json -AsHashtable } else { $null })
    }
}

function Get-EvalWrites { @($global:CmCalls | Where-Object { $_.Command -eq 'gh' -and $_.Arguments[0] -eq 'issue' -and $_.Arguments[1] -in @('create', 'edit', 'comment', 'close') }) }
function Get-EvalGhReads { @($global:CmCalls | Where-Object { $_.Command -eq 'gh' -and -not ($_.Arguments -join ' ').StartsWith('issue list --repo coryfinnegan/CollectionMaxxing --state open --label agent-task') -and $_.Arguments[1] -notin @('create', 'edit', 'comment', 'close') }) }
function Get-EvalArg { param($Call, [string]$Flag) $i = [array]::IndexOf($Call.Arguments, $Flag); if ($i -ge 0) { $Call.Arguments[$i + 1] } }

# The usual starting point: tested build 131, the phone's first run in between, and a recognition change in 133.
function Get-EvalStory {
    param([string[]]$Files133 = @('app/src/features/scan/shelf.ts'))
    @((New-EvalBuild 131 '2026-11-01T14:49:46Z'), (New-EvalBuild 133 '2026-11-01T16:49:53Z' -Files $Files133 -Subject '[Scan] A shelf row splits per spine (#634)'))
}

# ---- when an issue opens -----------------------------------------------------------------------------------------------------

Test-Case 'eval-due: a recognition change between the phone-tested build and the newest opens one issue for the newest build' {
    $root = New-EvalRoot
    try {
        $run = New-EvalPhoneRun -Id 'phone-run-1' -Start $script:EvalRun1Start
        Set-EvalResults -Root $root -Header $run.Header -Kept $run.Kept -Score $run.Score
        $m = New-EvalMachine @{ Builds = (Get-EvalStory) }
        $r = Invoke-EvalPass -Root $root
        Assert-Equal 'idle' $r.Result.Outcome 'the agent pass is untouched'
        $writes = Get-EvalWrites
        Assert-Equal 1 $writes.Count 'one issue and nothing else'
        $create = $writes[0]
        Assert-Equal 'create' $create.Arguments[1]
        Assert-Equal '[Owner] Run evals on TestFlight build 133' (Get-EvalArg $create '--title')
        Assert-Equal 2 @($create.Arguments | Where-Object { $_ -eq '--label' }).Count
        Assert-True ($create.Arguments -contains 'needs:eval-run') 'carries needs:eval-run'
        Assert-True ($create.Arguments -contains 'needs:cory') 'carries needs:cory'
        Assert-True $create.Mutating
        Assert-Match $create.InputText ([regex]::Escape("<!-- eval-due build=133 sha=$(Get-EvalSha 133) -->"))
        Assert-Match $create.InputText 'A shelf row splits per spine \(#634\)'
        Assert-Match $create.InputText 'since build 131'
        Assert-Match $create.InputText 'Install build 133 from TestFlight'
        Assert-Match $create.InputText 'usually within half an hour'
        Assert-Match $create.InputText 'Settings > Recognition > Run evals'
        Assert-Match $create.InputText 'The PC scores it, and this issue closes itself\.'
        Assert-Equal 1 $m.Issues.Count
        Assert-True ($r.State.checkedUnix -gt 0) 'the time of the check is kept'
        # Only the runs it needed: the jobs of build 133 (newest) and 131 (phone-tested), nothing more.
        Assert-Equal 2 @($global:CmCalls | Where-Object { ($_.Arguments -join ' ') -match '/actions/runs/\d+/jobs' }).Count
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'eval-due: a newest build that changes only other app files opens nothing' {
    $root = New-EvalRoot
    try {
        $run = New-EvalPhoneRun -Id 'phone-run-1' -Start $script:EvalRun1Start
        Set-EvalResults -Root $root -Header $run.Header -Kept $run.Kept -Score $run.Score
        $m = New-EvalMachine @{ Builds = (Get-EvalStory -Files133 @('app/app.config.ts', 'app/src/features/library/List.tsx', 'src/CollectionMaxxing.Api/Program.cs', 'app/src/features/scanner-notes.md')) }
        $r = Invoke-EvalPass -Root $root
        Assert-Equal 'idle' $r.Result.Outcome
        Assert-Equal 0 (Get-EvalWrites).Count
        Assert-Equal 0 $m.Issues.Count
        Assert-Equal '' $r.Log 'a quiet check writes nothing'
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'eval-due: each of the three recognition folders counts' {
    foreach ($file in 'app/src/features/scan/a.ts', 'app/src/features/evals/b.ts', 'app/modules/cm-vision/ios/c.swift') {
        $root = New-EvalRoot
        try {
            $run = New-EvalPhoneRun -Id 'phone-run-1' -Start $script:EvalRun1Start
            Set-EvalResults -Root $root -Header $run.Header -Kept $run.Kept
            [void](New-EvalMachine @{ Builds = (Get-EvalStory -Files133 @($file)) })
            [void](Invoke-EvalPass -Root $root)
            Assert-Equal 1 (Get-EvalWrites).Count $file
        }
        finally { Remove-TestRoot $root }
    }
}

Test-Case 'eval-due: a run whose build job was skipped by [skip ios-build] is not a build' {
    $root = New-EvalRoot
    try {
        $run = New-EvalPhoneRun -Id 'phone-run-1' -Start $script:EvalRun1Start
        Set-EvalResults -Root $root -Header $run.Header -Kept $run.Kept
        # 135 is newer and changes recognition, but its push said [skip ios-build]: nothing was uploaded. The newest build is 133.
        $builds = @(Get-EvalStory) + @(New-EvalBuild 135 '2026-11-01T19:17:07Z' -Files @('app/src/features/scan/b.ts') -Build 'skipped')
        [void](New-EvalMachine @{ Builds = $builds })
        [void](Invoke-EvalPass -Root $root)
        $writes = Get-EvalWrites
        Assert-Equal 1 $writes.Count
        Assert-Equal '[Owner] Run evals on TestFlight build 133' (Get-EvalArg $writes[0] '--title')
        Assert-Match $writes[0].InputText 'build=133 '
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'eval-due: when the only newer run was skipped there is nothing to install and nothing opens' {
    $root = New-EvalRoot
    try {
        $run = New-EvalPhoneRun -Id 'phone-run-2'
        Set-EvalResults -Root $root -Header $run.Header -Kept $run.Kept -Score $run.Score   # tested 133
        $builds = @(Get-EvalStory) + @(New-EvalBuild 135 '2026-11-01T19:17:07Z' -Files @('app/src/features/scan/b.ts') -Build 'skipped')
        [void](New-EvalMachine @{ Builds = $builds })
        [void](Invoke-EvalPass -Root $root)
        Assert-Equal 0 (Get-EvalWrites).Count
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'eval-due: a failed run, a run in progress and a scheduled rebuild of a release are not builds' {
    $root = New-EvalRoot
    try {
        $run = New-EvalPhoneRun -Id 'phone-run-1' -Start $script:EvalRun1Start
        Set-EvalResults -Root $root -Header $run.Header -Kept $run.Kept
        $builds = @(Get-EvalStory) + @(
            (New-EvalBuild 134 '2026-11-01T17:00:00Z' -Files @('app/src/features/scan/x.ts') -Conclusion 'failure' -Build 'failure'),
            (New-EvalBuild 135 '2026-11-01T19:00:00Z' -Files @('app/src/features/scan/y.ts') -Status 'in_progress' -Conclusion ''),
            (New-EvalBuild 136 '2026-11-01T19:30:00Z' -Event 'schedule'))
        [void](New-EvalMachine @{ Builds = $builds })
        [void](Invoke-EvalPass -Root $root)
        $writes = Get-EvalWrites
        Assert-Equal 1 $writes.Count
        Assert-Equal '[Owner] Run evals on TestFlight build 133' (Get-EvalArg $writes[0] '--title')
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'eval-due: the issue names at most 10 commits, newest first, and only those that touched recognition' {
    $root = New-EvalRoot
    try {
        $run = New-EvalPhoneRun -Id 'phone-run-1' -Start $script:EvalRun1Start
        Set-EvalResults -Root $root -Header $run.Header -Kept $run.Kept
        # Build 100 is the phone-tested one (it finished long before the run at 15:30Z); 101 to 113 all finished after it.
        $builds = @(New-EvalBuild 100 '2026-11-01T01:00:00Z')
        foreach ($n in 101..113) {
            $files = if ($n -eq 105) { @('app/app.config.ts') } else { @('app/src/features/scan/a.ts') }
            $builds += New-EvalBuild $n ('2026-11-01T16:{0:00}:00Z' -f ($n - 100)) -Files $files -Subject "Change $n"
        }
        [void](New-EvalMachine @{ Builds = $builds })
        [void](Invoke-EvalPass -Root $root)
        $lines = @((Get-EvalWrites)[0].InputText -split "`n" | Where-Object { $_ -like '- *' })
        Assert-Equal 10 $lines.Count
        Assert-Equal '- Change 113' $lines[0] 'newest first'
        Assert-False ((Get-EvalWrites)[0].InputText -match 'Change 105') 'a commit that touched no recognition file is not named'
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'eval-due: a compare cut off at 300 files counts as due' {
    $root = New-EvalRoot
    try {
        $run = New-EvalPhoneRun -Id 'phone-run-1' -Start $script:EvalRun1Start
        Set-EvalResults -Root $root -Header $run.Header -Kept $run.Kept
        [void](New-EvalMachine @{ Builds = (Get-EvalStory -Files133 @(1..300 | ForEach-Object { "docs/page-$_.md" })) })
        [void](Invoke-EvalPass -Root $root)
        Assert-Equal 1 (Get-EvalWrites).Count
        Assert-Match (Get-EvalWrites)[0].InputText 'cut off'
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'eval-due: with no finished phone run an issue is opened for the newest build' {
    $root = New-EvalRoot
    try {
        [void](New-EvalMachine @{ Builds = (Get-EvalStory) })
        $r = Invoke-EvalPass -Root $root
        Assert-Equal 'idle' $r.Result.Outcome
        $writes = Get-EvalWrites
        Assert-Equal 1 $writes.Count
        Assert-Equal '[Owner] Run evals on TestFlight build 133' (Get-EvalArg $writes[0] '--title')
        Assert-Match $writes[0].InputText 'No finished phone eval run is on the PC yet'
        Assert-Equal 0 @($global:CmCalls | Where-Object { ($_.Arguments -join ' ') -match '/compare/' }).Count 'nothing to compare with'
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'eval-due: a phone-tested build that is not among the recent runs counts as due' {
    $root = New-EvalRoot
    try {
        $old = New-EvalPhoneRun -Id 'phone-run-0' -Start '2026-06-01T12:00:00Z'
        Set-EvalResults -Root $root -Header $old.Header -Kept $old.Kept
        [void](New-EvalMachine @{ Builds = (Get-EvalStory) })
        [void](Invoke-EvalPass -Root $root)
        $writes = Get-EvalWrites
        Assert-Equal 1 $writes.Count
        Assert-Match $writes[0].InputText 'not among the recent TestFlight builds'
    }
    finally { Remove-TestRoot $root }
}

# ---- when an issue is retitled or closed -------------------------------------------------------------------------------------

Test-Case 'eval-due: an open issue for build 133 is retitled to 135 when 135 changes recognition too, not duplicated' {
    $root = New-EvalRoot
    try {
        $run = New-EvalPhoneRun -Id 'phone-run-1' -Start $script:EvalRun1Start
        Set-EvalResults -Root $root -Header $run.Header -Kept $run.Kept
        $builds = @(Get-EvalStory) + @(New-EvalBuild 135 '2026-11-01T19:17:07Z' -Files @('app/modules/cm-vision/ios/V.swift') -Subject '[Vision] A tighter box (#650)')
        $issues = [System.Collections.Generic.List[object]]::new(); $issues.Add((New-EvalMarkedIssue -Number 190 -Build 133))
        $m = New-EvalMachine @{ Builds = $builds; Issues = $issues }
        [void](Invoke-EvalPass -Root $root)
        $writes = Get-EvalWrites
        Assert-Equal 0 @($writes | Where-Object { $_.Arguments[1] -eq 'create' }).Count 'no second issue'
        $edit = @($writes | Where-Object { $_.Arguments[1] -eq 'edit' })
        Assert-Equal 1 $edit.Count
        Assert-Equal '190' $edit[0].Arguments[2]
        Assert-Equal '[Owner] Run evals on TestFlight build 135' (Get-EvalArg $edit[0] '--title')
        Assert-Match $edit[0].InputText ([regex]::Escape("<!-- eval-due build=135 sha=$(Get-EvalSha 135) -->"))
        Assert-Match $edit[0].InputText 'A tighter box \(#650\)'
        Assert-Match $edit[0].InputText 'A shelf row splits per spine' 'it names the earlier change too'
        Assert-Equal 1 $m.Issues[0].comments.Count
        Assert-Match $m.Issues[0].comments[0] 'Build 135 changes recognition too'
        Assert-Equal 'open' $m.Issues[0].state
        Assert-Equal 1 $m.Issues.Count
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'eval-due: an open issue that already names the newest build is left alone' {
    $root = New-EvalRoot
    try {
        $run = New-EvalPhoneRun -Id 'phone-run-1' -Start $script:EvalRun1Start
        Set-EvalResults -Root $root -Header $run.Header -Kept $run.Kept
        $issues = [System.Collections.Generic.List[object]]::new(); $issues.Add((New-EvalMarkedIssue -Number 190 -Build 133))
        [void](New-EvalMachine @{ Builds = (Get-EvalStory); Issues = $issues })
        [void](Invoke-EvalPass -Root $root)
        Assert-Equal 0 (Get-EvalWrites).Count
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'eval-due: a finished phone run after build N closes the marked issue, and the comment carries the verdict' {
    $root = New-EvalRoot
    try {
        $run = New-EvalPhoneRun -Id 'phone-run-2' -Failing 3 -Verdict 'fail'   # started 17:52Z, after build 133 finished
        Set-EvalResults -Root $root -Header $run.Header -Kept $run.Kept -Score $run.Score
        $issues = [System.Collections.Generic.List[object]]::new(); $issues.Add((New-EvalMarkedIssue -Number 190 -Build 133))
        $m = New-EvalMachine @{ Builds = (Get-EvalStory); Issues = $issues }
        $r = Invoke-EvalPass -Root $root
        Assert-Equal 'idle' $r.Result.Outcome
        $writes = Get-EvalWrites
        Assert-Equal 1 $writes.Count 'closes it and opens nothing'
        Assert-Equal 'close' $writes[0].Arguments[1]
        Assert-Equal 'closed' $m.Issues[0].state
        $comment = $m.Issues[0].closeComment
        Assert-Match $comment 'phone-run-2'
        Assert-Match $comment '2026-11-01 17:52 UTC' 'when the run started, in UTC from the raw time'
        Assert-Match $comment '32 of 32 photos'
        Assert-Match $comment 'gate verdict fail'
        Assert-Match $comment '3 failing measures'
        Assert-Match $comment 'Report: \S*evals[\\/]results[\\/]report\.md$'
        Assert-False ($comment -match '["\r\n]') 'one plain line'
        Assert-Match $r.Log 'closed issue #190'
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'eval-due: the comment says one failing measure in the singular and omits a score that belongs to another run' {
    $root = New-EvalRoot
    try {
        $run = New-EvalPhoneRun -Id 'phone-run-2' -Failing 1 -Verdict 'fail'
        Set-EvalResults -Root $root -Header $run.Header -Kept $run.Kept -Score $run.Score
        $issues = [System.Collections.Generic.List[object]]::new(); $issues.Add((New-EvalMarkedIssue -Number 190 -Build 133))
        $m = New-EvalMachine @{ Builds = (Get-EvalStory); Issues = $issues }
        [void](Invoke-EvalPass -Root $root)
        Assert-Match $m.Issues[0].closeComment '1 failing measure[,.]'
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'eval-due: when latest.json scores another run the issue still closes, without a verdict, and a WARN says so' {
    $root = New-EvalRoot
    try {
        $run = New-EvalPhoneRun -Id 'phone-run-2'
        $run.Score.runId = 'some-other-run'; $run.Score.regroundedFrom.runId = 'some-other-run'
        Set-EvalResults -Root $root -Header $run.Header -Kept $run.Kept -Score $run.Score
        $issues = [System.Collections.Generic.List[object]]::new(); $issues.Add((New-EvalMarkedIssue -Number 190 -Build 133))
        $m = New-EvalMachine @{ Builds = (Get-EvalStory); Issues = $issues }
        $r = Invoke-EvalPass -Root $root
        Assert-Equal 'closed' $m.Issues[0].state
        Assert-False ($m.Issues[0].closeComment -match 'verdict')
        Assert-Match $r.Log 'WARN .*scores another run'
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'eval-due: closing the answered issue and opening the next one happen in the same pass' {
    $root = New-EvalRoot
    try {
        $run = New-EvalPhoneRun -Id 'phone-run-2'   # tested 133
        Set-EvalResults -Root $root -Header $run.Header -Kept $run.Kept -Score $run.Score
        $builds = @(Get-EvalStory) + @(New-EvalBuild 135 '2026-11-01T19:17:07Z' -Files @('app/src/features/scan/b.ts'))
        $issues = [System.Collections.Generic.List[object]]::new(); $issues.Add((New-EvalMarkedIssue -Number 190 -Build 133))
        $m = New-EvalMachine @{ Builds = $builds; Issues = $issues }
        [void](Invoke-EvalPass -Root $root)
        Assert-Equal 'closed' $m.Issues[0].state
        Assert-Equal 2 $m.Issues.Count
        Assert-Equal '[Owner] Run evals on TestFlight build 135' $m.Issues[1].title
        Assert-Match $m.Issues[1].body 'since build 133'
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'eval-due: a run still in progress closes nothing' {
    $root = New-EvalRoot
    try {
        # The first run finished after build 133; a second is open (the phone is sending photos). The earlier answer waits for the second.
        $done = New-EvalPhoneRun -Id 'phone-run-2'
        $going = New-EvalPhoneRun -Id 'phone-run-3' -Start '2026-11-01T18:30:00Z' -State 'Open'
        Set-EvalResults -Root $root -Header $going.Header -Kept $done.Kept -Score $done.Score
        $issues = [System.Collections.Generic.List[object]]::new(); $issues.Add((New-EvalMarkedIssue -Number 190 -Build 133)); $issues.Add((New-EvalIssue -Number 191 -Created '2026-11-01T16:00:00Z'))
        $m = New-EvalMachine @{ Builds = (Get-EvalStory); Issues = $issues }
        foreach ($state in 'Open', 'Finishing') {
            $going.Header.state = $state
            Set-EvalResults -Root $root -Header $going.Header
            Remove-Item -LiteralPath (Join-Path $root 'state/eval-due.json') -ErrorAction SilentlyContinue
            [void](Invoke-EvalPass -Root $root)
            Assert-Equal 0 (Get-EvalWrites).Count $state
            Assert-Equal 'open' $m.Issues[0].state
            Assert-Equal 'open' $m.Issues[1].state
        }
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'eval-due: an issue opened by hand, with no marker, closes after a later finished phone run' {
    $root = New-EvalRoot
    try {
        $run = New-EvalPhoneRun -Id 'phone-run-2'   # started 17:52Z
        Set-EvalResults -Root $root -Header $run.Header -Kept $run.Kept -Score $run.Score
        $issues = [System.Collections.Generic.List[object]]::new()
        $issues.Add((New-EvalIssue -Number 191 -Title 'Run evals when you can' -Body 'Please run the evals.' -Created '2026-11-01T16:00:00Z'))
        $issues.Add((New-EvalIssue -Number 192 -Title 'Run evals again' -Body 'Once more.' -Created '2026-11-01T18:30:00Z'))
        $m = New-EvalMachine @{ Builds = (Get-EvalStory); Issues = $issues }
        [void](Invoke-EvalPass -Root $root)
        Assert-Equal 'closed' $m.Issues[0].state 'opened before the run started'
        Assert-Match $m.Issues[0].closeComment 'phone-run-2'
        Assert-Equal 'open' $m.Issues[1].state 'opened after it: the next run answers that one'
        Assert-Equal 1 (Get-EvalWrites).Count
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'eval-due: an issue by anyone but the repo owner is never touched' {
    $root = New-EvalRoot
    try {
        $run = New-EvalPhoneRun -Id 'phone-run-2'
        Set-EvalResults -Root $root -Header $run.Header -Kept $run.Kept -Score $run.Score
        $issues = [System.Collections.Generic.List[object]]::new(); $issues.Add((New-EvalIssue -Number 195 -Author 'someone-else' -Created '2026-11-01T16:00:00Z'))
        $m = New-EvalMachine @{ Builds = (Get-EvalStory); Issues = $issues }
        [void](Invoke-EvalPass -Root $root)
        Assert-Equal 0 (Get-EvalWrites).Count
        Assert-Equal 'open' $m.Issues[0].state
    }
    finally { Remove-TestRoot $root }
}

# ---- how often, and when it must not run -----------------------------------------------------------------------------------

Test-Case 'eval-due: a second pass within 30 minutes makes no gh call of its own, and one after 30 minutes checks again' {
    $root = New-EvalRoot
    try {
        $run = New-EvalPhoneRun -Id 'phone-run-1' -Start $script:EvalRun1Start
        Set-EvalResults -Root $root -Header $run.Header -Kept $run.Kept
        $m = New-EvalMachine @{ Builds = (Get-EvalStory) }
        [void](Invoke-EvalPass -Root $root)
        Assert-Equal 1 $m.Issues.Count
        $before = $global:CmCalls.Count
        $m.Now = $m.Now.AddMinutes(29)
        $global:CmNow = $m.Now.UtcDateTime
        $r = Invoke-EvalPass -Root $root
        Assert-Equal 'idle' $r.Result.Outcome
        Assert-Equal 1 ($global:CmCalls.Count - $before) 'only the agent''s own queue read'
        Assert-Equal 0 @($global:CmCalls | Select-Object -Skip $before | Where-Object { $_.Display -match 'needs:eval-run|run list|api ' }).Count
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'eval-due: a pass 31 minutes after the last check checks again' {
    $root = New-EvalRoot
    try {
        $m = New-EvalMachine @{ Builds = (Get-EvalStory) }
        [void](Invoke-EvalPass -Root $root)
        $first = @(Get-EvalGhReads).Count
        Assert-True ($first -gt 0) 'the first pass read GitHub'
        $global:CmCalls.Clear()
        $m.Now = $m.Now.AddMinutes(31)
        $global:CmNow = $m.Now.UtcDateTime
        [void](Invoke-EvalPass -Root $root)
        Assert-True (@(Get-EvalGhReads).Count -gt 0) 'the second pass read it again'
        Assert-Equal 1 $m.Issues.Count 'and found the issue it had opened still open and current'
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'eval-due: a PC without an evals folder, or with CM_EVALS_ENABLED off, does nothing and makes no call' {
    $root = New-EvalRoot -NoEvals
    try {
        [void](New-EvalMachine @{ Builds = (Get-EvalStory) })
        $r = Invoke-EvalPass -Root $root
        Assert-Equal 'idle' $r.Result.Outcome
        Assert-Equal 1 $global:CmCalls.Count 'the agent''s own queue read, nothing else'
        Assert-Equal $null $r.State
        New-Item -ItemType Directory -Force -Path (Join-Path $root 'evals/results') | Out-Null
        $global:CmCalls.Clear()
        [void](Invoke-EvalPass -Root $root -EnvLines @('CM_EVALS_ENABLED=false'))
        Assert-Equal 1 $global:CmCalls.Count
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'eval-due: CM_EVALS_DIR moves the folder the results are read from' {
    $root = New-EvalRoot -NoEvals
    try {
        $elsewhere = Join-Path $root 'elsewhere'
        New-Item -ItemType Directory -Force -Path (Join-Path $elsewhere 'results/open-run') | Out-Null
        [void](New-EvalMachine @{ Builds = (Get-EvalStory) })
        [void](Invoke-EvalPass -Root $root -EnvLines @("CM_EVALS_DIR=$elsewhere"))
        Assert-Equal 1 (Get-EvalWrites).Count
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'eval-due: with the agent switched off the check still runs, and the agent still does not' {
    $root = New-EvalRoot
    try {
        $run = New-EvalPhoneRun -Id 'phone-run-1' -Start $script:EvalRun1Start
        Set-EvalResults -Root $root -Header $run.Header -Kept $run.Kept
        $m = New-EvalMachine @{ Builds = (Get-EvalStory) }
        $r = Invoke-EvalPass -Root $root -EnvLines @('CM_AGENT_ENABLED=0')
        Assert-Equal 'disabled' $r.Result.Outcome
        Assert-Equal 0 $r.Result.ExitCode
        Assert-Equal 1 $m.Issues.Count
        Assert-Equal 0 @($global:CmCalls | Where-Object { ($_.Arguments -join ' ') -match '--label agent-task' }).Count 'the agent queue is not read'
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'eval-due: a secret in a commit subject is hidden in what is posted, with the agent off too' {
    $root = New-EvalRoot
    try {
        $run = New-EvalPhoneRun -Id 'phone-run-1' -Start $script:EvalRun1Start
        Set-EvalResults -Root $root -Header $run.Header -Kept $run.Kept
        # test-only-password is POSTGRES_PASSWORD in New-TestRoot's .env.
        $builds = @((New-EvalBuild 131 '2026-11-01T14:49:46Z'), (New-EvalBuild 133 '2026-11-01T16:49:53Z' -Files @('app/src/features/scan/a.ts') -Subject 'Use test-only-password in the scan fixture'))
        $m = New-EvalMachine @{ Builds = $builds }
        [void](Invoke-EvalPass -Root $root -EnvLines @('CM_AGENT_ENABLED=0'))
        Assert-Equal 1 $m.Issues.Count
        Assert-False ($m.Issues[0].body -match 'test-only-password') 'the password is not posted'
        Assert-Match $m.Issues[0].body 'Use \*\*\* in the scan fixture'
    }
    finally { Remove-TestRoot $root }
}

# ---- failures never reach the pass ------------------------------------------------------------------------------------------

Test-Case 'eval-due: gh failing is a WARN, the pass keeps its outcome, and the check waits 30 minutes' {
    foreach ($fail in 'RunListExit', 'ApiExit', 'IssueListExit') {
        $root = New-EvalRoot
        try {
            $run = New-EvalPhoneRun -Id 'phone-run-1' -Start $script:EvalRun1Start
            Set-EvalResults -Root $root -Header $run.Header -Kept $run.Kept
            $m = New-EvalMachine @{ Builds = (Get-EvalStory); $fail = 1 }
            $r = Invoke-EvalPass -Root $root
            Assert-Equal 'idle' $r.Result.Outcome $fail
            Assert-Equal 0 $r.Result.ExitCode $fail
            Assert-Match $r.Log 'WARN .*eval-due: the check stopped' $fail
            Assert-Equal 0 (Get-EvalWrites).Count $fail
            Assert-True ($r.State.checkedUnix -gt 0) "${fail}: a failed check is not retried on the next pass"
        }
        finally { Remove-TestRoot $root }
    }
}

Test-Case 'eval-due: an issue write that fails is a WARN, and a gh call that throws is one too; the pass is unchanged' {
    foreach ($option in @(@{ WriteExit = 1 }, @{ ThrowOn = '^run list' }, @{ ThrowOn = '^issue list .*needs:eval-run' })) {
        $root = New-EvalRoot
        try {
            $run = New-EvalPhoneRun -Id 'phone-run-1' -Start $script:EvalRun1Start
            Set-EvalResults -Root $root -Header $run.Header -Kept $run.Kept
            $machine = @{ Builds = (Get-EvalStory) } + $option
            $m = New-EvalMachine $machine
            $r = Invoke-EvalPass -Root $root
            Assert-Equal 'idle' $r.Result.Outcome
            Assert-Equal 0 $r.Result.ExitCode
            Assert-Match $r.Log 'WARN .*eval-due' ($option.Keys -join '')
            Assert-Equal 0 $m.Issues.Count
        }
        finally { Remove-TestRoot $root }
    }
}

Test-Case 'eval-due: a corrupt latest-inputs.json or run.json is a WARN, changes nothing and leaves the pass alone' {
    foreach ($case in @(@{ Kept = '{"runId": "x", "readAt": '; Header = '' }, @{ Kept = ''; Header = 'not json at all' }, @{ Kept = '[1,2]'; Header = '' })) {
        $root = New-EvalRoot
        try {
            $run = New-EvalPhoneRun -Id 'phone-run-1' -Start $script:EvalRun1Start
            Set-EvalResults -Root $root -Header $(if ($case.Header) { $null } else { $run.Header }) -HeaderText $case.Header -Kept $(if ($case.Kept) { $null } else { $run.Kept }) -KeptText $case.Kept
            $m = New-EvalMachine @{ Builds = (Get-EvalStory); Issues = ([System.Collections.Generic.List[object]]@((New-EvalMarkedIssue -Number 190 -Build 133))) }
            $r = Invoke-EvalPass -Root $root
            Assert-Equal 'idle' $r.Result.Outcome
            Assert-Equal 0 $r.Result.ExitCode
            Assert-Match $r.Log 'WARN .*is not valid JSON'
            Assert-Equal 0 (Get-EvalWrites).Count
            Assert-Equal 'open' $m.Issues[0].state
        }
        finally { Remove-TestRoot $root }
    }
}

Test-Case 'eval-due: a half-written latest.json does not stop the close, it only drops the score' {
    $root = New-EvalRoot
    try {
        $run = New-EvalPhoneRun -Id 'phone-run-2'
        Set-EvalResults -Root $root -Header $run.Header -Kept $run.Kept
        Set-Content -LiteralPath (Join-Path $root 'evals/results/latest.json') -Value '{"runId": "phone-run-2", "gate": {'
        $issues = [System.Collections.Generic.List[object]]::new(); $issues.Add((New-EvalMarkedIssue -Number 190 -Build 133))
        $m = New-EvalMachine @{ Builds = (Get-EvalStory); Issues = $issues }
        $r = Invoke-EvalPass -Root $root
        Assert-Equal 'closed' $m.Issues[0].state
        Assert-Match $r.Log 'WARN .*score of phone run'
        Assert-False ($m.Issues[0].closeComment -match 'verdict')
    }
    finally { Remove-TestRoot $root }
}

# ---- dry run -------------------------------------------------------------------------------------------------------------------

Test-Case 'eval-due: a dry run makes no mutating call, writes no state, and prints what it would do' {
    $root = New-EvalRoot
    try {
        $run = New-EvalPhoneRun -Id 'phone-run-1' -Start $script:EvalRun1Start
        Set-EvalResults -Root $root -Header $run.Header -Kept $run.Kept
        $m = New-EvalMachine @{ Builds = (Get-EvalStory) }
        $r = Invoke-EvalPass -Root $root -DryRun
        Assert-Equal 'idle' $r.Result.Outcome
        Assert-Equal 0 @($global:CmCalls | Where-Object { $_.Mutating }).Count 'no mutating call reached the runner'
        Assert-Equal 0 (Get-EvalWrites).Count
        Assert-Equal 0 $m.Issues.Count
        Assert-True (@(Get-EvalGhReads).Count -gt 0) 'but it did read GitHub'
        Assert-Match $r.Screen 'dry run, would run: .*gh issue create'
        Assert-Match $r.Screen 'would open an issue asking for an eval run on build 133'
        Assert-Equal $null $r.State 'a dry run leaves no state, so the next real pass checks at once'
    }
    finally { Remove-TestRoot $root }
}

Test-Case 'eval-due: a dry run that would close an issue prints it and closes nothing' {
    $root = New-EvalRoot
    try {
        $run = New-EvalPhoneRun -Id 'phone-run-2'
        Set-EvalResults -Root $root -Header $run.Header -Kept $run.Kept -Score $run.Score
        $issues = [System.Collections.Generic.List[object]]::new(); $issues.Add((New-EvalMarkedIssue -Number 190 -Build 133))
        $m = New-EvalMachine @{ Builds = (Get-EvalStory); Issues = $issues }
        $r = Invoke-EvalPass -Root $root -DryRun
        Assert-Match $r.Screen 'dry run, would run: .*gh issue close 190'
        Assert-Equal 'open' $m.Issues[0].state
    }
    finally { Remove-TestRoot $root }
}

# ---- what the agents are told ------------------------------------------------------------------------------------------------

Test-Case 'eval-due: the agent prompt tells agents not to ask for an eval run in a comment' {
    $prompt = Get-Content -LiteralPath (Join-Path $script:EvalAgentDir 'agent-prompt.md') -Raw
    Assert-Match $prompt 'Do not ask the owner for a phone eval run in a comment'
    Assert-Match $prompt 'needs:eval-run'
}
